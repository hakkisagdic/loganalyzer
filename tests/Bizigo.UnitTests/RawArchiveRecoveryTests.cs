using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Kayıp ham nesnenin yerel WAL segmentinden geri yüklenmesi (T40).
///
/// <para>
/// <b>Neden birim testi, neden konteyner değil:</b> buradaki kararların hiçbiri
/// S3'e bakmıyor. Silme kapısı manifest satırına, kurtarma kararı da segmentin
/// hâlâ yerinde olup olmadığına bakıyor. Gerçek RustFS'e karşı koşan uçtan uca
/// kurtarma ayrı ve <c>RawArchiveTests</c>'in genişletmesi (§2, koordinatör
/// koşturur).
/// </para>
///
/// <para>
/// <b>T04'ün açık kalemi neden ticket oldu:</b> 48 saatlik WAL saklama
/// penceresinin <i>sebebi</i> "nesne kaybolursa yerelden yeniden yükle" idi,
/// <c>RawManifestEntity.WalSegment</c> o bağı taşıyordu, scrub kaybı görüyordu
/// — ve kurtarmayı yapan kod <b>yoktu</b>. Koruma tasarlanmış, saklama süresi
/// ona göre seçilmiş, mekanizma yazılmamıştı.
/// </para>
/// </summary>
public sealed class RawArchiveRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly InMemoryObjectStore _store = new();
    private readonly InMemorySegmentSource _segments = new();
    private readonly FakeTimeProvider _time = new(Now);

    private RawStoreOptions _options = new()
    {
        Bucket = "test",
        SegmentRetention = TimeSpan.FromHours(48),
        ScrubInterval = TimeSpan.FromHours(6),
        ScrubSampleSize = 20,
    };

    private RawArchiveUploader Uploader() => new(
        _segments,
        _store,
        _factory,
        new SourceDirectory(_factory),
        new NoOpRawRefSink(),
        Options.Create(_options),
        NullLogger<RawArchiveUploader>.Instance,
        _time);

    private static RawRecord Record(string body) => new()
    {
        EventId = Guid.CreateVersion7(Now),
        ReceivedAt = Now,
        SourceKey = "10.1.2.3",
        Body = Encoding.UTF8.GetBytes(body),
    };

    /// <summary>Bir segment yükler ve manifest satırını döndürür.</summary>
    private async Task<RawManifestEntity> UploadAsync(string segmentId = "segment-1")
    {
        _segments.Add(segmentId, [(ReadOnlyMemory<byte>)RawRecordCodec.ToLine(Record("veri"))]);

        await Uploader().RunOnceAsync(TestContext.Current.CancellationToken);

        await using var db = _factory.CreateDbContext();
        return db.RawManifest.Single(m => m.WalSegment == segmentId);
    }

    private async Task SetStateAsync(string objectKey, RawObjectState state)
    {
        await using var db = _factory.CreateDbContext();
        var row = db.RawManifest.Single(m => m.ObjectKey == objectKey);
        row.State = state;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>Kaybı tespit etmiş olmak kurtarma kaynağını koruyor.</b>
    ///
    /// <para>
    /// Bulunuş biçimi: <c>DeleteExpiredSegmentsAsync</c> silme kararını
    /// <b>yalnızca</b> <c>VerifiedAt</c>'e bakarak veriyordu; <c>State</c>
    /// sorguya hiç girmiyordu. Önce doğrulanıp (damga düşmüş) sonra kaybolan bir
    /// nesnenin satırı, silme kararında hâlâ <i>"doğrulanmış ve süresi dolmuş"</i>
    /// görünüyordu.
    /// </para>
    ///
    /// <para>
    /// Sonucu, mekanizma yazılsa bile açık kalacak bir yarıştı: kurtarma
    /// <b>kendi kaynağını sildirebilirdi</b>. Bu yüzden ticket'ın ilk maddesi
    /// kurtarma değil bu kapı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kayip_nesnenin_segmenti_silinmiyor()
    {
        var entry = await UploadAsync();

        // Nesne depodan kayboluyor ve scrub bunu görüp işaretliyor.
        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Missing);

        // Saklama süresi doluyor — eski kodda tam burada siliniyordu.
        _time.Advance(TimeSpan.FromHours(49));
        var report = await Uploader().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, report.SegmentsDeleted);
        Assert.DoesNotContain("segment-1", _segments.Deleted);
    }

    /// <summary>Bozulmuş nesnenin segmenti de korunuyor — aynı gerekçe.</summary>
    [Fact]
    public async Task Bozulmus_nesnenin_segmenti_de_silinmiyor()
    {
        var entry = await UploadAsync();

        await SetStateAsync(entry.ObjectKey, RawObjectState.ChecksumMismatch);

        _time.Advance(TimeSpan.FromHours(49));
        var report = await Uploader().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, report.SegmentsDeleted);
    }

    /// <summary>
    /// Sağlam nesnenin segmenti süresi dolunca <b>siliniyor</b>.
    ///
    /// <para>
    /// Pozitif taraf ayrıca sınanıyor: yeni kapı her segmenti sonsuza kadar
    /// tutsaydı testler yine yeşil yanardı ve disk sessizce dolardı — "koruma
    /// çalışıyor" ile "silme hiç çalışmıyor" ayırt edilemezdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Saglam_nesnenin_segmenti_suresi_dolunca_siliniyor()
    {
        await UploadAsync();

        _time.Advance(TimeSpan.FromHours(49));
        var report = await Uploader().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.SegmentsDeleted);
        Assert.Contains("segment-1", _segments.Deleted);
    }

    public void Dispose() => _factory.Dispose();
}

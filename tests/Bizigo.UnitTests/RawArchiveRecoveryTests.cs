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

    /// <summary>
    /// <b>Kayıp nesne yerel segmentten geri yükleniyor.</b>
    ///
    /// <para>
    /// T04'ün 48 saatlik penceresinin <i>sebebi</i> buydu ve bugüne kadar bunu
    /// yapan kod yoktu: manifest kaybı görüyor, <c>WalSegment</c> kaynağı
    /// söylüyordu, ve zincir orada bitiyordu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kayip_nesne_segmentten_geri_yukleniyor()
    {
        var entry = await UploadAsync();

        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Missing);

        var report = await Uploader().RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, report.Recovered);
        Assert.True(_store.Has(entry.ObjectKey));

        await using var db = _factory.CreateDbContext();
        var row = db.RawManifest.Single(m => m.ObjectKey == entry.ObjectKey);

        Assert.Equal(RawObjectState.Verified, row.State);
        Assert.NotNull(row.VerifiedAt);
    }

    /// <summary>
    /// Kurtarılan nesnenin içeriği <b>birebir aynı</b>.
    ///
    /// <para>
    /// Kurtarma nesneyi sha256'sından tanıyor, yani yazılan şey manifest'in
    /// kaydettiği şeyle aynı olmak zorunda. Bu test o eşitliği doğrudan
    /// ölçüyor: yeniden kurulan baytlar kaybolan baytlarla aynı değilse
    /// "kurtarıldı" demek, sessiz bir bozulmayı başarı diye raporlamak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kurtarilan_nesne_kaybolanla_ayni()
    {
        var entry = await UploadAsync();

        var original = await _store.GetAsync(entry.ObjectKey, TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Missing);

        await Uploader().RecoverAsync(TestContext.Current.CancellationToken);

        var restored = await _store.GetAsync(entry.ObjectKey, TestContext.Current.CancellationToken);
        Assert.Equal(original, restored);
    }

    /// <summary>
    /// <b>Segment artık yoksa sessizce geçilmiyor.</b>
    ///
    /// <para>
    /// Deneme hakkı dolduğunda durum <c>Unrecoverable</c> oluyor. Ayrı bir
    /// değer olması gerekiyor: <c>Missing</c>'de kalsaydı "kurtarma sırasını
    /// bekliyor" ile "denendi, olmadı" ayırt edilemezdi ve operatörün bakması
    /// gereken tek liste ikincisi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Segment_yoksa_kurtarilamaz_isaretleniyor()
    {
        var entry = await UploadAsync();

        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Missing);
        _segments.Forget("segment-1");

        // Üst sınıra kadar deneniyor; sınıra gelmeden durum değişmiyor.
        for (var i = 1; i < _options.MaxRecoveryAttempts; i++)
        {
            var partial = await Uploader().RecoverAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, partial.Unrecoverable);
        }

        var final = await Uploader().RecoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, final.Unrecoverable);

        await using var db = _factory.CreateDbContext();
        var row = db.RawManifest.Single(m => m.ObjectKey == entry.ObjectKey);

        Assert.Equal(RawObjectState.Unrecoverable, row.State);
        Assert.Equal(_options.MaxRecoveryAttempts, row.RecoveryAttempts);
    }

    /// <summary>
    /// <b>Kurtarılamaz işaretlenen nesne yeniden denenmiyor.</b>
    ///
    /// <para>
    /// Sınır olmasaydı bozuk bir S3 yapılandırması her scrub turunda yeniden
    /// yazma denerdi — sessiz ve sonsuz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kurtarilamaz_nesne_yeniden_denenmiyor()
    {
        var entry = await UploadAsync();

        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Unrecoverable);

        var report = await Uploader().RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Attempted);
    }

    /// <summary>
    /// <b>Yeniden kurulan içerik manifest'le uyuşmuyorsa yazılmıyor.</b>
    ///
    /// <para>
    /// Manifest'in sha256'sı doğru kabul ediliyor; sapan taraf kurtarmadır.
    /// Yanlış içerikle üzerine yazmak, kaybı <b>sessiz bir bozulmaya</b>
    /// çevirirdi — ve bozulma, kaybın aksine, hiçbir yerde "eksik" görünmezdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sha256_tutmayan_yeniden_kurulum_yazilmiyor()
    {
        var entry = await UploadAsync();

        _store.Lose(entry.ObjectKey);

        // Manifest'teki sha256 bozuluyor: yeniden kurulan nesne artık hiçbir
        // adayla eşleşmiyor.
        await using (var db = _factory.CreateDbContext())
        {
            var row = db.RawManifest.Single(m => m.ObjectKey == entry.ObjectKey);
            row.State = RawObjectState.Missing;
            row.Sha256 = new string('0', 64);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var report = await Uploader().RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Attempted);
        Assert.Equal(0, report.Recovered);
        Assert.False(_store.Has(entry.ObjectKey));
    }

    /// <summary>
    /// Kurtarma <b>saklama saatini yeniden başlatıyor</b> — ve koruma kalıcı
    /// değil.
    ///
    /// <para>
    /// Kurtarma <c>VerifiedAt</c>'i günceliyor, dolayısıyla segment kurtarmadan
    /// sonra 48 saat daha tutuluyor. Bu <b>kasıtlı</b> ve penceremizin kendi
    /// gerekçesinden çıkıyor: pencere, nesnenin kaybolmasının en olası olduğu
    /// dönemi kapsamak için var, ve <i>az önce yazılmış</i> bir nesne tam olarak
    /// o dönemde. Eski damgayı korumak, kurtarılan nesneyi ikinci bir kayba
    /// karşı korumasız bırakırdı.
    /// </para>
    ///
    /// <para>
    /// Bedeli kayıtta dursun: aynı nesne tekrar tekrar kaybolursa segmenti
    /// süresiz tutar. Deneme üst sınırı bunu sınırlıyor — <c>Unrecoverable</c>
    /// olan nesne artık kurtarılmıyor, yani saat de yenilenmiyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kurtarma_saklama_saatini_yeniden_baslatiyor()
    {
        var entry = await UploadAsync();

        _store.Lose(entry.ObjectKey);
        await SetStateAsync(entry.ObjectKey, RawObjectState.Missing);

        _time.Advance(TimeSpan.FromHours(49));

        // Kurtarmadan önce: kapı segmenti tutuyor (durum `Missing`).
        Assert.Equal(0, (await Uploader().RunOnceAsync(TestContext.Current.CancellationToken)).SegmentsDeleted);

        await Uploader().RecoverAsync(TestContext.Current.CancellationToken);

        // Kurtarmadan hemen sonra: durum sağlam ama damga tazelendi, saat
        // yeniden işliyor.
        Assert.Equal(0, (await Uploader().RunOnceAsync(TestContext.Current.CancellationToken)).SegmentsDeleted);

        // Yeni pencere de dolunca silinebiliyor: koruma kalıcı değil.
        _time.Advance(TimeSpan.FromHours(49));
        Assert.Equal(1, (await Uploader().RunOnceAsync(TestContext.Current.CancellationToken)).SegmentsDeleted);
    }

    public void Dispose() => _factory.Dispose();
}

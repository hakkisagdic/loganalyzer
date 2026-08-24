using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Zincir — parçalar değil</b> (T27).
///
/// <para>
/// <c>F2FlowTests</c>'in başındaki zincir haritası her halkanın hangi testte
/// kapalı olduğunu gösteriyor ve doğru bir belge. Ama bir <b>parça listesi</b>
/// ile bir <b>zincir</b> aynı şey değil: parçalar tek tek doğru olabilir ve
/// birinin çıktısı ötekinin girdisine hiç bağlanmamış olabilir. Ticket'ın kendi
/// cümlesi bunu istiyor — <i>"tek tek doğru olan parçaların birlikte doğru
/// olduğunu göstermek"</i>.
/// </para>
///
/// <para>
/// Bu dosya <c>F2FlowTests</c>'ten <b>ayrı</b>, çünkü o dosya bu turda üç kez
/// çakışma noktası oldu ve aynı sınıfa iki ajanın paralel yazması protokol §9'un
/// uyardığı durum.
/// </para>
///
/// <h3>Dışarıda bırakılan halka ve neden</h3>
///
/// <para>
/// <b>Zincir kimlikten değil, kapsamdan başlıyor.</b> Giriş ayağı burada yok ve
/// bu bilinçli bir karar: onu katmak <c>DevStackFixture</c>'a Keycloak eklemeyi
/// gerektirirdi, ve o ayak zincirin <b>ölçülmemiş</b> kısmı değil —
/// <c>ui/tests/token-isolation.test.ts</c> (15 test) ve
/// <c>KeycloakRealmTests</c> onu kapsıyor, üstelik canlı Keycloak'a karşı da
/// doğrulandı (<c>scope=openid</c> geçiyor, <c>openid profile email</c>
/// <c>invalid_scope</c> alıyor).
/// </para>
///
/// <para>
/// <b>Bir sonraki okuyucu için:</b> burada kanıtlanan şey "uçtan uca giriş
/// akışı" DEĞİL. Kanıtlanan, <c>AccessScope</c> elde edildikten sonrasının
/// zincir hâlinde doğru olduğu. Bu ayrımın yazılı olması gerekiyor, çünkü tam
/// bu turda adı iddiasından geniş olan bir test bulundu ve okuyanı
/// yanıltıyordu.
/// </para>
///
/// <h3>Koşturulduğunda ne kanıtlayacak</h3>
///
/// <para>
/// <b>Docker gerektiriyor, bu dalda koşturulmadı</b> (protokol §2). Her testin
/// özet yorumunda koşturulduğunda ne kanıtlayacağı tek cümleyle yazılı;
/// koşturan kişi onları beklenen çıktı olarak okuyabilir.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class F2ChainTests(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>Mikrosaniye bileşeni kasıtlı — K29'un kırpılma tuzağı zincirde de var.</summary>
    private static readonly DateTimeOffset Observed =
        new DateTimeOffset(2026, 8, 17, 9, 30, 15, TimeSpan.Zero) + TimeSpan.FromTicks(7_431_002);

    private const string Core = "net-core";
    private const string Edge = "net-edge";

    /// <summary>Zincirin aradığı satır. Çok alfabeli: kodlama tespiti bu üçünde kırılıyordu.</summary>
    private static readonly byte[] Wire =
        Encoding.UTF8.GetBytes("kullanıcı oturum açma başarısız — المستخدم — 用户登录失败");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ClickHouseContext _context = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private ScopedQuery _query = null!;
    private RawEventLocator _locator = null!;
    private string _bucket = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(Token);
        await new ClickHouseMigrator(_context).MigrateAsync(RepoPath("db/clickhouse"), Token);

        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);
        await using var db = await _factory.CreateDbContextAsync(Token);
        await db.Database.MigrateAsync(Token);
        await db.RawManifest.ExecuteDeleteAsync(Token);
        await db.Sources.ExecuteDeleteAsync(Token);

        db.Sources.Add(new SourceEntity
        {
            SourceId = "fg-core",
            OwnerGroup = Core,
            PeerAddress = "10.9.9.9",
        });
        await db.SaveChangesAsync(Token);

        _bucket = "bizigo-zincir-" + Guid.NewGuid().ToString("N")[..8];

        var writer = new EventWriter(_context);

        _query = new ScopedQuery(
            new EventReader(_context),
            new ChangeEventReader(_context),
            new CorrelationReader(_context),
            writer,
            await _factory.CreateDbContextAsync(Token),
            new NoOpAuditSink());
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ zincir

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> <c>analyst.core</c>'un kapsamıyla
    /// yapılan bir arama bir olay döndürüyor, o olayın <b>kimliğiyle</b> tekil
    /// okuma aynı olayı veriyor, ve <b>o olayın</b> <c>raw_ref</c>'iyle arşivden
    /// çekilen baytlar cihazın gönderdiğinin birebir aynısı.
    ///
    /// <para>
    /// Zincir olmasının anlamı şu: her adımın girdisi bir önceki adımın
    /// <b>çıktısı</b>. Hiçbir yerde sabit bir <c>event_id</c> ya da elle yazılmış
    /// bir nesne anahtarı yok — halkalardan biri kopsa (arama başka olay
    /// döndürse, <c>raw_ref</c> yanlış ön ek taşısa, manifest zamanı kırpılma
    /// yüzünden eşleşmese) test düşer.
    /// </para>
    ///
    /// <para>
    /// Parçaları ayrı ayrı sınayan testler zaten var
    /// (<c>EventPaginationTests</c>, <c>OcsfOtelViewTests</c>,
    /// <c>RawEventLocatorTests</c>). Buradaki iddia farklı: <b>birlikte</b>
    /// doğrular.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Arama_detay_ham_bayt_ayni_olayi_tasiyor()
    {
        var (record, objectKey, store) = await ArchiveAsync();
        _locator = new RawEventLocator(_factory, store);

        await WriteEventAsync(record, objectKey, Core);

        var scope = AccessScope.ForGroups("analyst.core", [Core]);

        // 1 · ARAMA — kapsam kapısından.
        var page = await _query.SearchEventsAsync(Query(), scope, Token);
        var found = Assert.Single(page.Events);

        // 2 · DETAY — girdisi aramanın çıktısı.
        var detail = await _query.GetEventAsync(found.EventId, scope, Token);
        Assert.NotNull(detail);
        Assert.Equal(found.EventId, detail.EventId);

        // 3 · HAM BAYT — girdisi detayın çıktısı.
        var lookup = await _locator.FindAsync(detail, scope, Token);

        Assert.NotNull(lookup);
        Assert.Equal(record.EventId, lookup.Record.EventId);
        Assert.Equal(Wire, lookup.Record.Body);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> aynı zincir <c>analyst.edge</c>'in
    /// kapsamıyla yürütüldüğünde <b>üç halkanın üçü de</b> kapalı — arama boş
    /// dönüyor, tekil okuma <c>null</c>, ve ham okuma nesneyi <b>hiç açmadan</b>
    /// reddediyor.
    ///
    /// <para>
    /// Neden üçünü birden sınıyor: kapsam ayrışmasının tehlikeli hâli "bir uç
    /// sızdırıyor" değil, <b>zincirin bir halkasının</b> ötekilerden farklı
    /// davranması. Arama boş dönerken tekil okumanın kimliği kabul etmesi,
    /// kullanıcının olayı aramada göremeyip adresini bilerek açabilmesi demek
    /// olurdu — ve o, hiçbir yerde belirti üretmez.
    /// </para>
    ///
    /// <para>
    /// Reddin <c>RawAccessDeniedException</c> ile gelmesi de iddianın parçası:
    /// <c>null</c> dönmek "nesne yok" ile "izin yok"u birleştirirdi, ve ikisi
    /// farklı sorunlar.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Zincirin_hicbir_halkasi_baska_grubun_olayini_vermiyor()
    {
        var (record, objectKey, store) = await ArchiveAsync();
        _locator = new RawEventLocator(_factory, store);

        var written = await WriteEventAsync(record, objectKey, Core);

        var foreign = AccessScope.ForGroups("analyst.edge", [Edge]);

        var page = await _query.SearchEventsAsync(Query(), foreign, Token);
        Assert.Empty(page.Events);

        // Kimliği BİLEREK veriyoruz: "aramada görünmüyor" ile "adresini bilen
        // açamaz" ayrı iddialar ve ikincisi burada sınanıyor.
        Assert.Null(await _query.GetEventAsync(record.EventId, foreign, Token));

        await Assert.ThrowsAsync<RawAccessDeniedException>(
            () => _locator.FindAsync(written, foreign, Token));
    }

    // ----------------------------------------------------------------- yardımcı

    private static EventQuery Query() => new()
    {
        From = Observed.AddHours(-1),
        To = Observed.AddHours(1),
    };

    /// <summary>Ham kaydı gerçekten arşive yazıyor ve manifest satırını döndürüyor.</summary>
    private async Task<(RawRecord Record, string ObjectKey, IRawObjectStore Store)> ArchiveAsync()
    {
        var record = new RawRecord
        {
            EventId = Guid.CreateVersion7(Observed),
            ReceivedAt = Observed.AddSeconds(1),
            ObservedAt = Observed,
            SourceKey = "10.9.9.9",
            Body = Wire,
        };

        var options = new RawStoreOptions
        {
            ServiceUrl = stack.S3ServiceUrl,
            Bucket = _bucket,
            AccessKey = "bizigoadmin",
            SecretKey = "bizigoadmin",
            ForcePathStyle = true,
            SegmentRetention = TimeSpan.FromHours(48),
        };

        var store = new S3RawObjectStore(Microsoft.Extensions.Options.Options.Create(options));
        await store.EnsureBucketAsync(Token);

        var segments = new FakeSegmentSource();
        segments.Add("segment-zincir", [(ReadOnlyMemory<byte>)RawRecordCodec.ToLine(record)]);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            _factory,
            new SourceDirectory(_factory),
            new NullRawRefSink(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance);

        var report = await uploader.RunOnceAsync(Token);
        Assert.Equal(1, report.ObjectsWritten);

        await using var db = await _factory.CreateDbContextAsync(Token);
        var key = await db.RawManifest.Select(m => m.ObjectKey).SingleAsync(Token);

        return (record, key, store);
    }

    /// <summary>
    /// Olay satırını yazıyor. <c>Timestamp</c> ClickHouse'un yazacağı gibi
    /// <b>milisaniyeye kırpılmış</b> — K29'un tuzağı zincirde de var ve buradan
    /// geçiyor olması iddianın parçası.
    /// </summary>
    private async Task<LogEvent> WriteEventAsync(RawRecord record, string objectKey, string ownerGroup)
    {
        var logEvent = new LogEvent
        {
            EventId = record.EventId,
            Timestamp = new DateTimeOffset(
                Observed.UtcDateTime.AddTicks(-(Observed.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)),
                TimeSpan.Zero),
            OwnerGroup = ownerGroup,
            SourceId = "fg-core",
            Host = "fw-01",
            ParseStatus = ParseStatus.Ok,
            Body = Encoding.UTF8.GetString(Wire),
            RawRef = objectKey[..(objectKey.LastIndexOf('/') + 1)],
        };

        await new EventWriter(_context).WriteEventsAsync([logEvent], Token);

        return logEvent;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }
}

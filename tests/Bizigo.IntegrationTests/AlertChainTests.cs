using System.Net;
using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Cihaz sussun → alarm → bildirimdeki bağlantı → doğru arama</b> — zincir
/// hâlinde (T27).
///
/// <para>
/// Halkaların üçü de kendi testinde kapalıydı: <c>AlertingTests</c> susan
/// kaynağı yakalıyor, <c>NotificationDispatcherTests</c> mesajın kanala
/// ulaştığını sınıyor, <c>AlertLinkTests</c>/<c>AlertLinkTargetTests</c>
/// bağlantının biçimini. Eksik olan şey <b>birinin çıktısının ötekinin girdisi
/// olduğu</b>: üçü de doğru olup, aradaki bağlar yanlış olabilirdi.
/// </para>
///
/// <para>
/// Somut olarak kaçırılabilecek kusur şu: alarm <c>fw-core-01</c> için
/// tetikleniyor, bağlantı üretiliyor, ama bağlantının taşıdığı zaman aralığı
/// tetiklenmenin aralığı <b>değil</b> — kullanıcı tıklıyor ve olayın olmadığı
/// bir ekrana düşüyor. Her test tek başına yeşil kalır, çünkü hiçbiri iki ucu
/// birden tutmuyor.
/// </para>
///
/// <h3>Koşturulduğunda ne kanıtlayacak</h3>
///
/// <para>
/// <b>Docker gerektiriyor, bu dalda koşturulmadı</b> (protokol §2). Testlerin
/// özet yorumlarında koşturulduğunda ne kanıtlayacakları yazılı.
/// </para>
///
/// <h3>Dışarıda bırakılan halka</h3>
///
/// <para>
/// Kanala <b>gerçekten teslim</b> (HTTP isteği) burada yok:
/// <c>NotificationDispatcherTests</c> onu sahte kanalla kapsıyor ve gerçek bir
/// uç nokta çağırmak testi ağa bağlardı. Buradaki zincirin son halkası
/// "bildirime giren bağlantı", teslimin kendisi değil. Bir sonraki okuyucu bunu
/// "bildirim gönderildi" diye okumasın.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class AlertChainTests(DevStackFixture stack) : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private const string Core = "net-core";
    private const string Edge = "net-edge";

    private ClickHouseContext _context = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private IScopedQuery _query = null!;
    private IAlertQuerySource _source = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(Token);
        await new ClickHouseMigrator(_context).MigrateAsync(RepoPath("db/clickhouse"), Token);

        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(Token);
        await db.Database.MigrateAsync(Token);
        await db.Sources.ExecuteDeleteAsync(Token);

        db.Sources.AddRange(
            new SourceEntity
            {
                SourceId = "fw-core-01",
                OwnerGroup = Core,
                PeerAddress = "10.0.0.1",
                CreatedAt = Now.AddDays(-30),
            },
            new SourceEntity
            {
                SourceId = "fw-edge-01",
                OwnerGroup = Edge,
                PeerAddress = "10.0.0.3",
                CreatedAt = Now.AddDays(-30),
            });

        await db.SaveChangesAsync(Token);

        var writer = new EventWriter(_context);

        await writer.WriteEventsAsync(
        [
            // `fw-core-01` yarım saattir susuyor: son iki olayı eski.
            Sample(Core, "fw-core-01", Now.AddMinutes(-35)),
            Sample(Core, "fw-core-01", Now.AddMinutes(-30)),

            // Başka ekibin kaynağı az önce konuştu. Alarmın kümesine de,
            // bağlantının açtığı aramaya da girmemeli.
            Sample(Edge, "fw-edge-01", Now.AddMinutes(-1)),
        ], Token);

        _query = new ScopedQuery(
            new EventReader(_context),
            new ChangeEventReader(_context),
            new CorrelationReader(_context),
            writer,
            await _factory.CreateDbContextAsync(Token),
            new NoOpAuditSink());

        _source = new SingleQuerySource(_query);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> susan kaynak için tetiklenen
    /// alarmın <b>kendi penceresiyle</b> üretilen bağlantı, o pencereyi taşıyor
    /// ve o pencereyle yapılan arama alarmın işaret ettiği kaynağın grubunu
    /// döndürüyor — başka ekibin kaynağını değil.
    ///
    /// <para>
    /// Zincir olmasının anlamı: bağlantının zaman aralığı elle yazılmıyor,
    /// <b>tetiklenmenin çıktısından</b> geliyor. Değerlendirici pencereyi
    /// değiştirirse ya da bağlantı üreteci başka bir aralık gömerse test düşer;
    /// ikisi ayrı ayrı doğru kalıp arada ayrışamaz.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Susan_kaynagin_alarmi_dogru_aramayi_acan_bir_baglanti_uretiyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();

        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(Now),
            Name = "çekirdek susuyor",
            OwnerSubject = "analyst.core",
            OwnerGroups = Core,
            RuleType = AlertRuleType.Silence,
            SilenceSeconds = 900,
        };

        // 1 · ALARM — gerçek veriyle, gerçek tabloda.
        var evaluator = new AlertEvaluator(
            new AlertingOptions(), stats, NullLogger<AlertEvaluator>.Instance, time);

        var outcome = await evaluator.EvaluateAsync(rule, Context(time, stats), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("fw-core-01", hit.SourceId);

        // 2 · BAĞLANTI — girdisi tetiklenmenin çıktısı. Elle yazılmış aralık yok.
        var link = AlertLinkBuilder.Build(
            new AlertingOptions { ProductBaseUrl = "https://bizigo.example" },
            rule,
            hit.WindowFrom,
            hit.WindowTo);

        Assert.NotNull(link);

        // 3 · ARAMA — girdisi bağlantının kendisi. Kullanıcının tıklayınca
        // göreceği kümeyi, bağlantıdan okunan parametrelerle kuruyoruz.
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);

        var from = DateTimeOffset.Parse(
            query["from"]!, System.Globalization.CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(
            query["to"]!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(hit.WindowFrom, from);
        Assert.Equal(hit.WindowTo, to);

        var page = await _query.SearchEventsAsync(
            new EventQuery { From = from, To = to },
            AccessScope.ForGroups(rule.OwnerSubject, [Core]),
            Token);

        // Alarmın işaret ettiği kaynağın olayları bu pencerede; başka ekibinki
        // hiçbir koşulda değil.
        Assert.All(page.Events, e => Assert.Equal(Core, e.OwnerGroup));
        Assert.Contains(page.Events, e => e.SourceId == "fw-core-01");
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> bağlantı kuralın kimliğini
    /// <c>kural=</c> ile taşıyor ve bu <b>kaynak göstergesi</b> olarak duruyor —
    /// aramanın kapsamını belirleyen şey değil.
    ///
    /// <para>
    /// Ayrım önemli: kimliği çözüp kuralı okuyan bir ekran, kullanıcı günler
    /// sonra tıkladığında <b>bugünkü</b> kuralı gösterirdi. Bağlantı o anın
    /// fotoğrafı olmalı, canlı bir sorgu değil.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Baglanti_kural_kimligini_kaynak_gostergesi_olarak_tasiyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();

        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(Now),
            Name = "çekirdek susuyor",
            OwnerSubject = "analyst.core",
            OwnerGroups = Core,
            RuleType = AlertRuleType.Silence,
            SilenceSeconds = 900,
        };

        var evaluator = new AlertEvaluator(
            new AlertingOptions(), stats, NullLogger<AlertEvaluator>.Instance, time);

        var outcome = await evaluator.EvaluateAsync(rule, Context(time, stats), Token);
        var hit = Assert.Single(outcome.Hits);

        var link = AlertLinkBuilder.Build(
            new AlertingOptions { ProductBaseUrl = "https://bizigo.example" },
            rule,
            hit.WindowFrom,
            hit.WindowTo);

        Assert.Contains("kural=" + rule.Id, new Uri(link!).Query, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- yardımcı

    private AlertEvaluationContext Context(TimeProvider time, AlertingStats stats) => new(
        _source, stats, Now, TimeSpan.FromHours(6), TimeSpan.FromSeconds(20), Token, time);

    /// <summary>
    /// <b><c>IngestedAt</c> açıkça veriliyor ve atlanması testi düşürüyordu.</b>
    ///
    /// <para>
    /// Sessizlik değerlendiricisi <c>Timestamp</c>'e değil
    /// <c>LastIngestedAt</c>'e bakıyor ve bu bilinçli: soru "cihazın saatine
    /// göre en son ne zaman" değil, <b>"ondan en son ne zaman haber aldık"</b> —
    /// saati kayan bir kaynak aksi hâlde susmuş görünürdü.
    /// </para>
    ///
    /// <para>
    /// <c>LogEvent.IngestedAt</c>'in varsayılanı <c>DateTimeOffset.UtcNow</c>,
    /// yani <b>gerçek duvar saati</b>. Alan verilmeyince satırlar sahte
    /// <c>Now</c>'dan (2026-08-19) günler sonrasına damgalandı,
    /// <c>since = now - seen</c> <b>negatif</b> çıktı, eşiğin altında kaldı ve
    /// kaynak "susmuş" sayılmadı — sonuç <c>Quiet</c>. Belirti tetiklenmenin
    /// hiç olmamasıydı; sebebi bir zaman damgasının yokluğuydu.
    /// </para>
    /// </summary>
    private static LogEvent Sample(string ownerGroup, string sourceId, DateTimeOffset at) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Timestamp = at,
        IngestedAt = at,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        ParseStatus = ParseStatus.Ok,
        Action = "deny",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = "satır",
    };

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

using System.Net;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Manifest → senkron → kullanıcı açar → zamanlayıcı koşar → tetiklenir</b>
/// — zincir hâlinde (T33'ün son kabul kriteri).
///
/// <para>
/// Halkaların hepsi kendi testinde kapalı: <c>SigmaRuleSyncTests</c> kararı,
/// <c>SigmaRuleSyncServiceTests</c> yazmayı, <c>AlertSchedulerTests</c>
/// pasif/gated kuralın sorgu üretmediğini sınıyor. Eksik olan şey <b>birinin
/// çıktısının ötekinin girdisi olduğu</b>: hepsi doğru olup aradaki bağlar
/// yanlış olabilirdi.
/// </para>
///
/// <para>
/// Somut olarak kaçırılabilecek kusur: senkron kuralı <c>Disabled</c>
/// indiriyor, kullanıcı açıyor, ama kural <b>kapsamsız</b> ya da
/// <c>Threshold = 0</c> ile inmiş olduğu için zamanlayıcı onu ya hiç görmüyor
/// ya her turda tetikliyor. Her test tek başına yeşil kalır, çünkü hiçbiri
/// senkronun yazdığı satırı zamanlayıcıya <b>gerçekten</b> vermiyor.
/// </para>
///
/// <h3>Zincir olmasının anlamı</h3>
///
/// <para>
/// Kural kimliği, kapsamı ve eşiği <b>elle yazılmıyor</b> — senkronun kayda
/// yazdığı satırdan okunuyor. Senkron varsayılanı değiştirirse ya da
/// zamanlayıcı başka bir alana bakmaya başlarsa test düşer; ikisi ayrı ayrı
/// doğru kalıp arada ayrışamaz.
/// </para>
///
/// <h3>Duvar saati yok</h3>
///
/// <para>
/// Zamanlayıcı turu <c>RunTurnAsync</c> ile <b>açıkça</b> çağrılıyor, süreye
/// bağlanmıyor. Yüklü bir makinede süreye bağlı bir bekleyiş yanlış anı ölçer
/// ve testin geçme sebebi duvar saatiyle ilgili hâle gelir (§6).
/// </para>
///
/// <h3>Koşturulduğunda ne kanıtlayacak</h3>
///
/// <para>
/// <b>Docker gerektiriyor, bu dalda koşturulmadı</b> (§2). Her testin özet
/// yorumunda koşturulduğunda ne kanıtlayacağı yazılı.
/// </para>
///
/// <h3>Dışarıda bırakılan halka</h3>
///
/// <para>
/// Bildirimin kanala <b>gerçekten teslimi</b> burada yok:
/// <c>NotificationDispatcherTests</c> onu sahte kanalla kapsıyor ve gerçek bir
/// uca istek atmak testi ağa bağlardı. Buradaki zincirin son halkası
/// <b>tetiklenmenin kayda düşmesi</b>; bir sonraki okuyucu bunu "bildirim
/// gönderildi" diye okumasın.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class SigmaRuleChainTests(DevStackFixture stack) : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private const string Group = "net-core";
    private const string SigmaId = "03702803-0000-4000-8000-000000000000";

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
        // Tetiklenmeler de siliniyor — kurallar zaten siliniyordu ama
        // tetiklenmeler kalıyordu ve **sınıfın ikinci testi birincinin
        // bıraktığını buluyordu**.
        //
        // Bu temizlik yine de asıl düzeltme DEĞİL: asıl kusur, testin sahip
        // olmadığı bir şey hakkında iddia kurmasıydı (aşağıya bakınız).
        // Temizlik olmadan da doğru olan bir iddia, temizliğe muhtaç olandan
        // sağlam.
        await db.AlertTriggers.ExecuteDeleteAsync(Token);
        await db.AlertRules.ExecuteDeleteAsync(Token);

        var writer = new EventWriter(_context);

        // Kuralın arayacağı olaylar. `IngestedAt` AÇIKÇA veriliyor: varsayılanı
        // `DateTimeOffset.UtcNow` ve sahte saatle çalışırken satırlar
        // GELECEKTE damgalanır — `AlertChainTests` tam bu yüzden bir kez düştü.
        await writer.WriteEventsAsync(
        [
            Sample(Group, "fw-01", Now.AddMinutes(-4)),
            Sample(Group, "fw-01", Now.AddMinutes(-2)),
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
    /// <b>Koşturulduğunda kanıtlayacağı:</b> senkronun yazdığı kural, kullanıcı
    /// onu açtıktan sonra zamanlayıcı turunda <b>gerçekten değerlendiriliyor</b>
    /// ve tetikleniyor — kimliği, kapsamı ve eşiği senkronun bıraktığı hâliyle.
    ///
    /// <para>
    /// Zincir olmasının anlamı: hiçbir adımda elle yazılmış kural kimliği ya da
    /// sabit bir eşik yok. Senkron <c>Threshold</c> varsayılanını değiştirirse
    /// ya da zamanlayıcı <c>Status</c> yerine başka bir alana bakmaya başlarsa
    /// bu test düşer.
    /// </para>
    ///
    /// <para>
    /// Kaçırılabilecek kusur: senkron kuralı kapsamsız ya da eşiksiz indirir,
    /// kullanıcı açar, ve kural sessizce ya hiç koşmaz ya her turda tetiklenir.
    /// Birim testleri bunu göremez çünkü hiçbiri senkronun yazdığı satırı
    /// zamanlayıcıya vermiyor.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Senkronun_indirdigi_kural_acilinca_tetikleniyor()
    {
        // 1 · SENKRON — manifest kurala dönüşüyor.
        var sync = new SigmaRuleSyncService(_factory);

        var result = await sync.SyncAsync(
            ManifestWithOneWrittenRule(), "analyst.core", [Group], Token);

        Assert.Equal(1, result.Created);

        // 2 · KULLANICI AÇIYOR — girdisi senkronun yazdığı satır. Kimlik elle
        //     yazılmıyor; kayıttan okunuyor.
        Guid ruleId;

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var stored = await db.AlertRules.SingleAsync(r => r.SigmaRuleId == SigmaId, Token);

            // Senkronun bıraktığı hâl: PASİF. Derleme hattı kuralı üretir,
            // kullanıcı adına açmaz.
            Assert.Equal(AlertRuleStatus.Disabled, stored.Status);

            stored.Status = AlertRuleStatus.Enabled;
            await db.SaveChangesAsync(Token);

            ruleId = stored.Id;
        }

        // 3 · ZAMANLAYICI TURU — süreye değil, açık çağrıya bağlı.
        var time = new FakeTimeProvider(Now);
        var turn = await Worker(time).RunTurnAsync(Token);

        Assert.Equal(AlertTurn.Evaluated, turn);

        // 4 · TETİKLENME — girdisi turun çıktısı, kayıttaki kuralın kimliği.
        await using var check = await _factory.CreateDbContextAsync(Token);
        var trigger = await check.AlertTriggers.SingleAsync(t => t.RuleId == ruleId, Token);

        Assert.Equal(
            AlertRunState.Fired,
            (await check.AlertRules.SingleAsync(r => r.Id == ruleId, Token)).LastRunState);

        // Pencere kuralın kendi penceresi: elle yazılmış bir aralık değil.
        Assert.True(trigger.WindowTo > trigger.WindowFrom);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> senkronun indirdiği kural
    /// <b>açılmadıkça hiç sorgu üretmiyor</b> — zamanlayıcı turu boş geçiyor.
    ///
    /// <para>
    /// Birim testi bunu <c>Status</c> üzerinden sınıyor; burada ölçülen şey
    /// senkronun bıraktığı <b>gerçek</b> satırın o davranışı üretmesi. Senkron
    /// bir gün <c>Enabled</c> indirmeye başlarsa birim testi yeşil kalır ve bu
    /// düşer.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Senkronun_indirdigi_kural_ACILMADAN_sorgu_uretmiyor()
    {
        await new SigmaRuleSyncService(_factory)
            .SyncAsync(ManifestWithOneWrittenRule(), "analyst.core", [Group], Token);

        var turn = await Worker(new FakeTimeProvider(Now)).RunTurnAsync(Token);

        Assert.Equal(AlertTurn.Idle, turn);

        // İddia BU KURALIN tetiklenmesi üzerine — tablonun tamamı üzerine değil.
        //
        // Eski hâli `Assert.Empty(db.AlertTriggers)` idi ve sınıfla koşarken
        // düşüyordu, tek başına geçiyordu. Sebep "kararsız test" değil:
        // sınıfın önceki testi bir kural açıp tetiklenme üretiyor ve o
        // tetiklenme kayıtta kalıyordu.
        //
        // Asıl kusur sızıntı değil **iddianın kapsamı**: test "bu kural sorgu
        // üretmiyor" demek istiyordu, ama "hiçbir yerde tetiklenme yok"
        // diyordu. İkincisi testin sahip olmadığı bir şey hakkında ve ancak
        // yalıtım kazasıyla doğru.
        await using var db = await _factory.CreateDbContextAsync(Token);
        var rule = await db.AlertRules.SingleAsync(r => r.SigmaRuleId == SigmaId, Token);

        Assert.False(
            await db.AlertTriggers.AnyAsync(t => t.RuleId == rule.Id, Token),
            "senkronun indirdiği kural açılmadan tetiklenme üretti");
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> <c>gated</c> inen bir kural
    /// açılamıyor ve turda hiç görünmüyor — <b>ve sebebi kayıtta duruyor</b>.
    ///
    /// <para>
    /// İki iddia ayrı ayrı önemli: sorgu üretmemek <c>Disabled</c> ile ortak,
    /// sebebin taşınması ise <c>gated</c>'e özgü. İkincisi olmasaydı liste,
    /// kullanıcının neyin kapatacağını göremediği bir çöp kutusu olurdu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gated_inen_kural_turda_gorunmuyor_ve_sebebi_kayitta()
    {
        await new SigmaRuleSyncService(_factory)
            .SyncAsync(ManifestWithOneGatedRule(), "analyst.core", [Group], Token);

        var turn = await Worker(new FakeTimeProvider(Now)).RunTurnAsync(Token);

        Assert.Equal(AlertTurn.Idle, turn);

        await using var db = await _factory.CreateDbContextAsync(Token);
        var stored = await db.AlertRules.SingleAsync(r => r.SigmaRuleId == SigmaId, Token);

        Assert.Equal(AlertRuleStatus.Gated, stored.Status);
        Assert.Contains("dns_query_name", stored.GatedReason, StringComparison.Ordinal);
        Assert.Contains("[schema]", stored.GatedReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> senkronla inen kural
    /// <b>sahibinin kapsamıyla</b> koşuyor — başka grubun olaylarını saymıyor.
    ///
    /// <para>
    /// T33'ün kabul kriteri bunu ayrıca istiyor ve birim testi kanıtlayamıyor:
    /// kapsamın gerçekten uygulandığı yer <c>IScopedQuery</c> ve o canlı veri
    /// istiyor. Kapsam yalnızca kayda yazılıp sorguya geçmezse her şey yeşil
    /// kalır ve kural sessizce başka ekibin verisini sayar — bu deponun en
    /// pahalı hata sınıfı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Senkronla_inen_kural_baska_grubun_verisini_saymiyor()
    {
        // Başka bir grubun olayları — kuralın kümesine girmemeli.
        await new EventWriter(_context).WriteEventsAsync(
        [
            Sample("net-edge", "fw-edge-01", Now.AddMinutes(-3)),
            Sample("net-edge", "fw-edge-01", Now.AddMinutes(-1)),
        ], Token);

        await new SigmaRuleSyncService(_factory)
            .SyncAsync(ManifestWithOneWrittenRule(), "analyst.core", [Group], Token);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var stored = await db.AlertRules.SingleAsync(r => r.SigmaRuleId == SigmaId, Token);

            // Kapsam senkronun yazdığı hâliyle okunuyor, elle kurulmuyor.
            Assert.Equal(Group, stored.OwnerGroups);

            stored.Status = AlertRuleStatus.Enabled;
            await db.SaveChangesAsync(Token);
        }

        await Worker(new FakeTimeProvider(Now)).RunTurnAsync(Token);

        await using var check = await _factory.CreateDbContextAsync(Token);
        var rule = await check.AlertRules.SingleAsync(r => r.SigmaRuleId == SigmaId, Token);
        var trigger = await check.AlertTriggers.SingleAsync(t => t.RuleId == rule.Id, Token);

        // Tetiklenme kuralın kendi grubunda: başka grubun iki olayı sayıya
        // GİRMEMELİ. Girseydi değer 4 olurdu.
        Assert.Equal(Group, trigger.OwnerGroup);
        Assert.Equal(2, trigger.Value);
    }

    // ----------------------------------------------------------------- yardımcı

    private AlertSchedulerWorker Worker(TimeProvider time)
    {
        var options = new AlertingOptions();
        var stats = new AlertingStats();

        return new AlertSchedulerWorker(
            options,
            _factory,
            _source,
            new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time),
            stats,
            NullLogger<AlertSchedulerWorker>.Instance,
            time);
    }

    /// <summary>
    /// Tek <c>written</c> kurallı manifest.
    ///
    /// <para>
    /// Gerçek manifest dosyası okunmuyor ve bu bilinçli: o dosya derleme
    /// hattının çıktısı ve içeriği değiştikçe bu testin ne ölçtüğü kayar.
    /// Ölçülen şey <b>zincir</b>, korpusun bugünkü hâli değil. Biçimin
    /// gerçek manifestle aynı olduğunu <c>SigmaRuleSyncTests</c> ayrıca
    /// sınıyor.
    /// </para>
    /// </summary>
    private static SigmaManifest ManifestWithOneWrittenRule() => new()
    {
        Rules =
        [
            new SigmaManifestRule
            {
                RuleId = SigmaId,
                Title = "FortiGate: kimlik doğrulama hatası",
                SourcePath = "catalog/sigma/rules/fortigate_user_auth_fail.yml",
                SourceSha = "sha256:kaynak",
                Status = SigmaRuleSync.StatusWritten,
                OutputSha = "sha256:cikti",
            },
        ],
    };

    private static SigmaManifest ManifestWithOneGatedRule() => new()
    {
        Rules =
        [
            new SigmaManifestRule
            {
                RuleId = SigmaId,
                Title = "FortiGate: uzun DNS sorgusu",
                SourceSha = "sha256:kaynak",
                Status = SigmaRuleSync.StatusGated,
                Blockers =
                [
                    new SigmaManifestBlocker
                    {
                        Column = "dns_query_name",
                        Kind = "unknown_column",
                        Remedy = "schema",
                        Message = "`dns_query_name` bu şemada eşlenemiyor",
                    },
                ],
            },
        ],
    };

    private static LogEvent Sample(string ownerGroup, string sourceId, DateTimeOffset at) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Timestamp = at,

        // AÇIKÇA veriliyor — varsayılanı `DateTimeOffset.UtcNow` ve sahte
        // saatle çalışırken satırlar gelecekte damgalanır.
        IngestedAt = at,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        ParseStatus = ParseStatus.Ok,
        Action = "denied",
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

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (Bizigo.sln).")
            : Path.Combine(dir.FullName, relative);
    }
}

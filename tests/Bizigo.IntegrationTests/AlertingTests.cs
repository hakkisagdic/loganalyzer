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
/// Alarm motorunun <b>gerçek yığındaki</b> doğrulaması (T21).
///
/// <para>
/// Birim testleri kararları sınıyor — kim tetiklenir, ne bastırılır, kaç sorgu
/// atılır. Burada sınanan şey <b>SQL'in kendisi</b>: kapsam koşulunun
/// ClickHouse'a gerçekten inip inmediği, kaynak etkinliği sorgusunun gerçek
/// tabloda doğru gruplayıp gruplamadığı ve alarm göçünün Postgres'te ayakta
/// olup olmadığı. F1'in dersi bu ayrımı zorunlu kılıyor: doğrulanmamış her
/// katman kırıktı ve hiçbiri kendini belli etmedi.
/// </para>
///
/// <para>
/// <b>T21'in en zor kabul kriteri burada karşılanıyor:</b> "envanterdeki bir
/// kaynak susturulduğunda eşik sonrası tetikleniyor." Sahte sorguyla
/// gösterilebilecek en fazla şey karar mantığıydı; susmanın gerçek veride
/// görülmesi ancak burada mümkün.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class AlertingTests(DevStackFixture stack) : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

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

        // Alarm tabloları da göçle geldi mi — kural yazmadan önceki ilk kanıt.
        await db.AlertRuleChannels.ExecuteDeleteAsync(Token);
        await db.NotificationDeliveries.ExecuteDeleteAsync(Token);
        await db.AlertTriggers.ExecuteDeleteAsync(Token);
        await db.AlertRules.ExecuteDeleteAsync(Token);
        await db.MaintenanceWindows.ExecuteDeleteAsync(Token);
        await db.NotificationChannels.ExecuteDeleteAsync(Token);
        await db.Sources.ExecuteDeleteAsync(Token);

        db.Sources.AddRange(
            new SourceEntity
            {
                SourceId = "fw-core-01",
                OwnerGroup = "net-core",
                PeerAddress = "10.0.0.1",
                CreatedAt = Now.AddDays(-30),
            },
            new SourceEntity
            {
                SourceId = "fw-core-02",
                OwnerGroup = "net-core",
                PeerAddress = "10.0.0.2",
                CreatedAt = Now.AddDays(-30),
            },
            new SourceEntity
            {
                SourceId = "fw-edge-01",
                OwnerGroup = "net-edge",
                PeerAddress = "10.0.0.3",
                CreatedAt = Now.AddDays(-30),
            });

        await db.SaveChangesAsync(Token);

        var writer = new EventWriter(_context);

        await writer.WriteEventsAsync(
        [
            // fw-core-01 yarım saattir susuyor.
            Sample("net-core", "fw-core-01", Now.AddMinutes(-35), "deny"),
            Sample("net-core", "fw-core-01", Now.AddMinutes(-30), "deny"),

            // fw-core-02 iki dakika önce konuştu.
            Sample("net-core", "fw-core-02", Now.AddMinutes(-2), "accept"),

            // Başka ekibin verisi: hiçbir sayıma girmemeli.
            Sample("net-edge", "fw-edge-01", Now.AddMinutes(-1), "deny"),
            Sample("net-edge", "fw-edge-01", Now.AddMinutes(-1), "deny"),
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

    private static AccessScope CoreOnly() => AccessScope.ForGroups("analyst.core", ["net-core"]);

    private AlertEvaluationContext Context(TimeProvider time, AlertingStats stats) => new(
        _source, stats, Now, TimeSpan.FromHours(6), TimeSpan.FromSeconds(20), Token, time);

    /// <summary>
    /// Ortak sorgu yüzeyi gerçek tabloda kaynak başına tek satır üretiyor ve
    /// kapsam dışını hiç göstermiyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kaynak_etkinligi_kapsam_disini_gostermiyor()
    {
        var rows = await _query.GetSourceActivityAsync(
            new SourceActivityWindow { From = Now.AddHours(-6), To = Now },
            CoreOnly(),
            Token);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.SourceId == "fw-edge-01");

        var quiet = rows.Single(r => r.SourceId == "fw-core-01");
        Assert.Equal(Now.AddMinutes(-30), quiet.LastEventAt);
        Assert.Equal(2, quiet.EventCount);
    }

    /// <summary>T21 kabul kriteri: sessizlik alarmı gerçek veriyle çalışıyor.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sessizlik_alarmi_susan_kaynagi_gercek_veriyle_yakaliyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();
        var options = new AlertingOptions();

        var rule = new AlertRuleEntity
        {
            Name = "çekirdek susuyor",
            OwnerSubject = "analyst.core",
            OwnerGroups = "net-core",
            RuleType = AlertRuleType.Silence,
            SilenceSeconds = 900,
        };

        var evaluator = new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time);
        var outcome = await evaluator.EvaluateAsync(rule, Context(time, stats), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);

        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("fw-core-01", hit.SourceId);
        Assert.True(hit.Value >= 1800, $"beklenen ≥1800 sn susma, ölçülen {hit.Value}");
    }

    /// <summary>
    /// Kapsam koşulu ClickHouse'a gerçekten iniyor: eşik kuralı yalnızca kendi
    /// grubunun olaylarını sayıyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Esik_kurali_baska_ekibin_olaylarini_saymiyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();
        var options = new AlertingOptions();

        var rule = new AlertRuleEntity
        {
            Name = "deny sağanağı",
            OwnerSubject = "analyst.core",
            OwnerGroups = "net-core",
            RuleType = AlertRuleType.Threshold,
            Threshold = 0,
            WindowSeconds = 3600,
            SearchJson = AlertSearchCodec.Serialize(new AlertSearch
            {
                Filters = [FieldFilter.Eq("action", "deny")],
            }),
        };

        var evaluator = new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time);
        var outcome = await evaluator.EvaluateAsync(rule, Context(time, stats), Token);

        Assert.Equal(AlertRunState.Fired, outcome.State);

        // İki — dört değil. net-edge'in iki `deny` satırı sayıma girmedi.
        Assert.Equal(2, outcome.Hits[0].Value);
    }

    /// <summary>
    /// Zamanlayıcının tam turu gerçek Postgres üzerinde: kural okunuyor,
    /// değerlendiriliyor, tetiklenme ve teslim kaydı yazılıyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Zamanlayici_turu_gercek_semada_tetiklenme_ve_teslim_yaziyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();
        var options = new AlertingOptions();

        Guid ruleId;

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var channel = new NotificationChannelEntity
            {
                Name = "noc-slack",
                OwnerGroup = "net-core",
                ChannelType = NotificationChannelType.Slack,
                SecretCipher = "sifreli-yer-tutucu",
            };

            var rule = new AlertRuleEntity
            {
                Name = "çekirdek susuyor",
                OwnerSubject = "analyst.core",
                OwnerGroups = "net-core",
                RuleType = AlertRuleType.Silence,
                SilenceSeconds = 900,
                IntervalSeconds = 60,
            };

            ruleId = rule.Id;

            db.NotificationChannels.Add(channel);
            db.AlertRules.Add(rule);
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity { RuleId = rule.Id, ChannelId = channel.Id });
            await db.SaveChangesAsync(Token);
        }

        var worker = new AlertSchedulerWorker(
            options,
            _factory,
            _source,
            new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time),
            stats,
            NullLogger<AlertSchedulerWorker>.Instance,
            time);

        Assert.Equal(AlertTurn.Evaluated, await worker.RunTurnAsync(Token));

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var trigger = Assert.Single(await db.AlertTriggers.AsNoTracking().ToListAsync(Token));
            Assert.Equal("fw-core-01", trigger.SourceId);

            var delivery = Assert.Single(await db.NotificationDeliveries.AsNoTracking().ToListAsync(Token));
            Assert.Equal(DeliveryState.Pending, delivery.State);

            var stored = await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == ruleId, Token);
            Assert.Equal(AlertRunState.Fired, stored.LastRunState);
            Assert.Equal(Now.AddSeconds(60), stored.NextRunAt);
        }
    }

    /// <summary>
    /// Bakım penceresi gerçek şemada: pencere açıkken tetiklenme yok, pencere
    /// bitince var. Kolon adları (<c>starts_at</c>/<c>ends_at</c>) ve zaman
    /// karşılaştırmasının Postgres tarafında çalıştığı da burada sabitleniyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bakim_penceresi_gercek_semada_bastiriyor()
    {
        var time = new FakeTimeProvider(Now);
        var stats = new AlertingStats();
        var options = new AlertingOptions();

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            db.AlertRules.Add(new AlertRuleEntity
            {
                Name = "çekirdek susuyor",
                OwnerSubject = "analyst.core",
                OwnerGroups = "net-core",
                RuleType = AlertRuleType.Silence,
                SilenceSeconds = 900,
                IntervalSeconds = 60,
            });

            db.MaintenanceWindows.Add(new MaintenanceWindowEntity
            {
                OwnerGroup = "net-core",
                StartsAt = Now.AddMinutes(-10),
                EndsAt = Now.AddMinutes(30),
                Reason = "çekirdek anahtar yükseltmesi",
            });

            await db.SaveChangesAsync(Token);
        }

        var worker = new AlertSchedulerWorker(
            options,
            _factory,
            _source,
            new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time),
            stats,
            NullLogger<AlertSchedulerWorker>.Instance,
            time);

        await worker.RunTurnAsync(Token);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            Assert.Empty(await db.AlertTriggers.AsNoTracking().ToListAsync(Token));
            Assert.Equal(AlertRunState.Suppressed, (await db.AlertRules.AsNoTracking().SingleAsync(Token)).LastRunState);
        }

        time.SetUtcNow(Now.AddMinutes(31));
        await worker.RunTurnAsync(Token);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var triggers = await db.AlertTriggers.AsNoTracking().ToListAsync(Token);

            // Sayıya değil KİMLİĞE bakıyoruz. Sebebi bir ürün kararı:
            // **sessizlik kuralı kaynak başına tetikleniyor, kural başına değil.**
            //
            // Karar, "on cihazın dokuzu susmuşsa mesele bir kuralın tetiklenmesi
            // değil, hangi dokuz cihaz olduğu" cümlesinden çıkıyor ve tek başına
            // durmuyor: `AlertTriggerEntity.SourceId` bunun için var, T22'nin
            // göndericisi de tetiklenmeleri (kural, kanal) ikilisinde tek mesaja
            // topluyor — yani kaynak başına tetiklenme kanalı boğmuyor. Kural
            // başına tek tetiklenme üretseydik bildirim "bir şey sustu" derdi ve
            // hangi cihaz olduğu kaybolurdu.
            //
            // Sayıya bakan bir bekçi bu yüzden yanlış soruyu soruyordu ve
            // gerçekten de yanlış cevap verdi: saat 31 dakika ileri alındığında
            // fixture'daki İKİ kaynak da 15 dakikalık eşiği geçmiş oluyor
            // (fw-core-01 61 dk, fw-core-02 33 dk sessiz), yani `Assert.Single`
            // motorun kusurunu değil testin dar varsayımını yakalamıştı.
            Assert.NotEmpty(triggers);
            Assert.Contains(triggers, t => t.SourceId == "fw-core-01");

            // Asıl iddia geçiş: pencere kapanınca kural yeniden koşuyor.
            Assert.Equal(
                AlertRunState.Fired,
                (await db.AlertRules.AsNoTracking().SingleAsync(Token)).LastRunState);
        }
    }

    /// <summary>
    /// Yukarıdaki kararın kendi bekçisi: <b>susan her kaynak ayrı bir tetiklenme
    /// üretiyor</b>, hepsi tek kurala bağlı.
    ///
    /// <para>
    /// Ayrı bir test, çünkü bakım penceresi testinin iddiası bu değil ve iki
    /// iddiayı tek teste doldurmak, birinin bozulduğunda hangisinin bozulduğunu
    /// belirsiz bırakır — az önce tam olarak bu oldu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sessizlik_kurali_susan_her_kaynak_icin_ayri_tetiklenme_uretiyor()
    {
        var time = new FakeTimeProvider(Now.AddMinutes(31));
        var stats = new AlertingStats();
        var options = new AlertingOptions();

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            db.AlertRules.Add(new AlertRuleEntity
            {
                Name = "çekirdek susuyor",
                OwnerSubject = "analyst.core",
                OwnerGroups = "net-core",
                RuleType = AlertRuleType.Silence,
                SilenceSeconds = 900,
                IntervalSeconds = 60,
            });

            await db.SaveChangesAsync(Token);
        }

        var worker = new AlertSchedulerWorker(
            options,
            _factory,
            _source,
            new AlertEvaluator(options, stats, NullLogger<AlertEvaluator>.Instance, time),
            stats,
            NullLogger<AlertSchedulerWorker>.Instance,
            time);

        await worker.RunTurnAsync(Token);

        await using (var db = await _factory.CreateDbContextAsync(Token))
        {
            var triggers = await db.AlertTriggers.AsNoTracking().ToListAsync(Token);

            // İki net-core kaynağı da eşiği geçti; net-edge kapsam dışında.
            Assert.Equal(2, triggers.Count);
            Assert.Equal(
                ["fw-core-01", "fw-core-02"],
                triggers.Select(t => t.SourceId).Order(StringComparer.Ordinal));

            // Hepsi TEK kurala bağlı: "kaynak başına tetiklenme" demek "kaynak
            // başına kural" demek değil.
            Assert.Single(triggers.Select(t => t.RuleId).Distinct());
        }
    }

    private static LogEvent Sample(string ownerGroup, string sourceId, DateTimeOffset ts, string action) => new()
    {
        EventId = Guid.CreateVersion7(ts),
        Timestamp = ts,
        IngestedAt = ts,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Action = action,
        Body = $"{ownerGroup} grubuna ait satır",
        RawRef = $"raw/{ownerGroup}/2026/08/19/12/default/",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
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

    // `SingleQuerySource` T27'de `AlertQuerySources.cs`'ye taşındı:
    // `AlertChainTests` de aynı sahteye ihtiyaç duydu ve ikinci bir kopya
    // yazmak, biri değiştiğinde ötekinin sessizce ayrışması demekti (§9).
}

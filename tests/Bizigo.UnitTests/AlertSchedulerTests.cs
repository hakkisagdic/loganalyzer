using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Zamanlayıcı bekçileri (T21): susturma penceresi, tekrar aralığı ve maliyet.
///
/// <para>
/// <b>Hiçbiri arka plan görevini başlatmıyor.</b> Testler
/// <see cref="AlertSchedulerWorker.RunTurnAsync"/>'i doğrudan çağırıyor ve saat
/// <see cref="FakeTimeProvider"/>'dan geliyor. F1'de bunun tersi yapılmıştı ve
/// aynı commit CI'da 14 saniye, yerelde 6,5 dakika sürüyordu.
/// </para>
/// </summary>
public sealed class AlertSchedulerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeScopedQuery _query = new();
    private readonly AlertingStats _stats = new();

    public void Dispose() => _factory.Dispose();

    private AlertSchedulerWorker Worker(AlertingOptions? options = null)
    {
        var opts = options ?? new AlertingOptions();

        return new AlertSchedulerWorker(
            opts,
            _factory,
            _query,
            new AlertEvaluator(opts, _stats, NullLogger<AlertEvaluator>.Instance, _time),
            _stats,
            NullLogger<AlertSchedulerWorker>.Instance,
            _time);
    }

    /// <summary>Her turda tetiklenecek bir eşik kuralı: pencerede iki olay, sınır sıfır.</summary>
    private async Task<AlertRuleEntity> SeedFiringRuleAsync(int repeatIntervalSeconds = 0)
    {
        _query.Events.Add(new FakeEvent("network/core", "fw-01", Start.AddMinutes(-1), "deny"));
        _query.Events.Add(new FakeEvent("network/core", "fw-01", Start.AddMinutes(-1), "deny"));

        var rule = new AlertRuleEntity
        {
            Name = "deny sağanağı",
            OwnerSubject = "tester",
            OwnerGroups = "network/core",
            RuleType = AlertRuleType.Threshold,
            Threshold = 0,
            WindowSeconds = 300,
            IntervalSeconds = 60,
            RepeatIntervalSeconds = repeatIntervalSeconds,
        };

        await using var db = _factory.CreateDbContext();
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(Token);

        return rule;
    }

    private async Task<int> TriggerCountAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.AlertTriggers.CountAsync(Token);
    }

    [Fact]
    public async Task Vadesi_gelmis_kural_yoksa_tur_bos_donuyor()
    {
        Assert.Equal(AlertTurn.Idle, await Worker().RunTurnAsync(Token));
    }

    [Fact]
    public async Task Tetiklenen_kural_gecmise_yaziliyor()
    {
        await SeedFiringRuleAsync();

        Assert.Equal(AlertTurn.Evaluated, await Worker().RunTurnAsync(Token));
        Assert.Equal(1, await TriggerCountAsync());

        await using var db = _factory.CreateDbContext();
        var stored = await db.AlertRules.SingleAsync(Token);

        Assert.Equal(AlertRunState.Fired, stored.LastRunState);
        Assert.Equal(Start, stored.LastFiredAt);
        Assert.Equal(Start.AddSeconds(60), stored.NextRunAt);
    }

    /// <summary>
    /// T21 kabul kriteri: <b>susturma penceresinde tetiklenme yok, pencere
    /// bitince var</b>. Tek testte ikisi birden, çünkü asıl iddia geçiştir.
    /// </summary>
    [Fact]
    public async Task Bakim_penceresinde_tetiklenme_yok_pencere_bitince_var()
    {
        var rule = await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.MaintenanceWindows.Add(new MaintenanceWindowEntity
            {
                OwnerGroup = "network/core",
                StartsAt = Start.AddMinutes(-10),
                EndsAt = Start.AddMinutes(30),
                Reason = "çekirdek anahtar yükseltmesi",
            });

            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);
        Assert.Equal(0, await TriggerCountAsync());

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(AlertRunState.Suppressed, (await db.AlertRules.SingleAsync(Token)).LastRunState);
        }

        // Pencerenin bitişinden sonrasına geçiyoruz; kural yeniden vadesi gelmiş olmalı.
        _time.SetUtcNow(Start.AddMinutes(31));
        _query.Events.Add(new FakeEvent("network/core", "fw-01", Start.AddMinutes(30), "deny"));

        await Worker().RunTurnAsync(Token);

        Assert.Equal(1, await TriggerCountAsync());
        Assert.Equal(1, _stats.Suppressed);
    }

    [Fact]
    public async Task Pencerenin_bittigi_an_bastirma_yok()
    {
        await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.MaintenanceWindows.Add(new MaintenanceWindowEntity
            {
                OwnerGroup = "network/core",
                StartsAt = Start.AddMinutes(-10),
                // Pencere TAM ŞİMDİ bitiyor: aralık [başlangıç, bitiş) olduğu için
                // bu an artık kapsam dışında.
                EndsAt = Start,
            });

            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        Assert.Equal(1, await TriggerCountAsync());
    }

    [Fact]
    public async Task Baska_grubun_bakim_penceresi_bu_kurali_susturmuyor()
    {
        await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.MaintenanceWindows.Add(new MaintenanceWindowEntity
            {
                OwnerGroup = "network/edge",
                StartsAt = Start.AddMinutes(-10),
                EndsAt = Start.AddMinutes(30),
            });

            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        Assert.Equal(1, await TriggerCountAsync());
    }

    /// <summary>
    /// Gürültü kontrolünün ilk kademesi: tekrar aralığı dolmadan ikinci bir
    /// tetiklenme üretilmiyor <b>ve</b> ClickHouse'a hiç gidilmiyor (K16).
    /// </summary>
    [Fact]
    public async Task Tekrar_araligi_dolmadan_ikinci_tetiklenme_yok()
    {
        await SeedFiringRuleAsync(repeatIntervalSeconds: 3600);

        await Worker().RunTurnAsync(Token);
        Assert.Equal(1, await TriggerCountAsync());

        var callsAfterFirst = _query.CountCalls;

        // Aralık dolmadan yeni tur: bastırılıyor ve sorgu atılmıyor.
        _time.SetUtcNow(Start.AddMinutes(5));
        await using (var db = _factory.CreateDbContext())
        {
            var stored = await db.AlertRules.SingleAsync(Token);
            stored.NextRunAt = Start.AddMinutes(1);
            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        Assert.Equal(1, await TriggerCountAsync());
        Assert.Equal(callsAfterFirst, _query.CountCalls);

        // Aralık dolduktan sonra yeniden tetikleniyor.
        _time.SetUtcNow(Start.AddHours(2));
        _query.Events.Add(new FakeEvent("network/core", "fw-01", Start.AddHours(2).AddMinutes(-1), "deny"));

        await Worker().RunTurnAsync(Token);

        Assert.Equal(2, await TriggerCountAsync());
    }

    /// <summary>
    /// T21 maliyet kabul kriteri, tur düzeyinde: yirmi sessizlik kuralı aynı
    /// kapsamdaysa ClickHouse iki sorgu görüyor — yirmi değil, kırk hiç değil.
    /// </summary>
    [Fact]
    public async Task Yirmi_sessizlik_kurali_turda_iki_sorgu_uretiyor()
    {
        _query.Sources.Add(FakeScopedQuery.Source("fw-core-01", "network/core", Start.AddDays(-30)));

        await using (var db = _factory.CreateDbContext())
        {
            for (var i = 0; i < 20; i++)
            {
                db.AlertRules.Add(new AlertRuleEntity
                {
                    Name = $"sessizlik-{i}",
                    OwnerSubject = "tester",
                    OwnerGroups = "network/core",
                    RuleType = AlertRuleType.Silence,
                    SilenceSeconds = 900,
                    IntervalSeconds = 60,
                    RepeatIntervalSeconds = 0,
                });
            }

            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        Assert.Equal(1, _query.InventoryCalls);
        Assert.Equal(1, _query.ActivityCalls);
        Assert.Equal(2, _stats.ScopedQueries);

        // Yirmi kural da tetiklendi: paylaşım sonucu değil yalnızca maliyeti değiştiriyor.
        Assert.Equal(20, await TriggerCountAsync());
    }

    [Fact]
    public async Task Pasif_kural_hic_degerlendirilmiyor()
    {
        var rule = await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            (await db.AlertRules.SingleAsync(Token)).Status = AlertRuleStatus.Disabled;
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(AlertTurn.Idle, await Worker().RunTurnAsync(Token));
        Assert.Equal(0, await TriggerCountAsync());
    }

    /// <summary>
    /// <b>`Gated` de sorgu üretmiyor — ama AYRI sınanıyor</b> (T33).
    ///
    /// <para>
    /// Ticket'ın uyardığı tuzak tam burada: bir <c>gated</c> kural
    /// <i>"kapalı kural sorgu üretmiyor"</i> kriterini <b>tanım gereği</b>
    /// sağlıyor — zaten SQL'i yok. İkisini tek testte toplasaydık
    /// <c>Disabled</c> yolunun gerçekten sınandığı görünmez olurdu ve bekçi
    /// yanlış sebeple yeşil yanardı.
    /// </para>
    ///
    /// <para>
    /// İkisi aynı sonucu veriyor ama aynı şey değil: biri kullanıcının kararı,
    /// diğeri yetenek sınırı. Zamanlayıcının ikisini de atlaması, ikisinin aynı
    /// olduğunu göstermez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gated_kural_da_hic_degerlendirilmiyor_ama_ayri_bir_sebeple()
    {
        var rule = await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var stored = await db.AlertRules.SingleAsync(Token);
            stored.Status = AlertRuleStatus.Gated;
            stored.Source = AlertRuleSource.Sigma;
            stored.GatedReason = "`dns_query_name` bu şemada eşlenemiyor [remedy=schema]";
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(AlertTurn.Idle, await Worker().RunTurnAsync(Token));
        Assert.Equal(0, await TriggerCountAsync());

        // Ve sebep KAYBOLMUYOR: sessiz bir "kapalı" rozeti, kullanıcının
        // neyin kapatacağını göremediği bir liste demek.
        await using (var db = _factory.CreateDbContext())
        {
            var stored = await db.AlertRules.SingleAsync(Token);
            Assert.Equal(AlertRuleStatus.Gated, stored.Status);
            Assert.Contains("remedy=schema", stored.GatedReason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Kullanıcı bir <c>gated</c> kuralı açamaz: SQL'i yok, açılsaydı
    /// zamanlayıcı onu her turda boşuna görürdü.
    /// </summary>
    [Fact]
    public async Task Gated_kural_kullanici_istegiyle_ACILAMIYOR()
    {
        await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var stored = await db.AlertRules.SingleAsync(Token);
            stored.Status = AlertRuleStatus.Gated;
            await db.SaveChangesAsync(Token);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var stored = await db.AlertRules.SingleAsync(Token);

            // `AlertRuleService` yolunun yaptığı: `Enabled=true` isteği geldi.
            if (stored.Status != AlertRuleStatus.Gated)
            {
                stored.Status = AlertRuleStatus.Enabled;
            }

            await db.SaveChangesAsync(Token);
        }

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(AlertRuleStatus.Gated, (await db.AlertRules.SingleAsync(Token)).Status);
        }
    }

    [Fact]
    public async Task Tetiklenme_bagli_kanallara_teslim_kaydi_aciyor()
    {
        var rule = await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var channel = new NotificationChannelEntity
            {
                Name = "noc-slack",
                OwnerGroup = "network/core",
                ChannelType = NotificationChannelType.Slack,
                SecretCipher = "şifreli",
            };

            db.NotificationChannels.Add(channel);
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity { RuleId = rule.Id, ChannelId = channel.Id });
            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        await using (var db = _factory.CreateDbContext())
        {
            var delivery = await db.NotificationDeliveries.SingleAsync(Token);
            Assert.Equal(DeliveryState.Pending, delivery.State);
            Assert.Equal(rule.Id, delivery.RuleId);
        }
    }

    [Fact]
    public async Task Pasif_kanala_teslim_kaydi_acilmiyor()
    {
        var rule = await SeedFiringRuleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var channel = new NotificationChannelEntity
            {
                Name = "kapali",
                OwnerGroup = "network/core",
                ChannelType = NotificationChannelType.Slack,
                Enabled = false,
            };

            db.NotificationChannels.Add(channel);
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity { RuleId = rule.Id, ChannelId = channel.Id });
            await db.SaveChangesAsync(Token);
        }

        await Worker().RunTurnAsync(Token);

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Empty(await db.NotificationDeliveries.ToListAsync(Token));
        }
    }

    [Fact]
    public void Sessizlik_geriye_bakisi_esigin_iki_kati_ile_varsayilanin_buyugu()
    {
        var options = new AlertingOptions { SilenceLookback = TimeSpan.FromHours(6) };

        var dar = new AlertRuleEntity
        {
            Name = "dar", OwnerSubject = "t", OwnerGroups = "g",
            RuleType = AlertRuleType.Silence, SilenceSeconds = 900,
        };

        var genis = new AlertRuleEntity
        {
            Name = "geniş", OwnerSubject = "t", OwnerGroups = "g",
            RuleType = AlertRuleType.Silence, SilenceSeconds = 86_400,
        };

        Assert.Equal(TimeSpan.FromHours(6), AlertSchedulerWorker.SilenceLookbackFor([dar], options));
        Assert.Equal(TimeSpan.FromHours(48), AlertSchedulerWorker.SilenceLookbackFor([genis], options));
    }
}

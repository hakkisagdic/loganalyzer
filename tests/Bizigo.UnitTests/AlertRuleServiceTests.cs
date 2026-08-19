using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Kural yazma kapısının bekçileri (T21).
///
/// <para>
/// Buradaki asıl iddia tek cümle: <b>bir kullanıcı kendi kapsamı dışında bir
/// grup için kural yazamaz.</b> Yazabilseydi kapsam ayrımı tek bir POST
/// isteğiyle delinirdi ve kural arka planda koştuğu için sonucu da kimse
/// görmezdi.
/// </para>
/// </summary>
public sealed class AlertRuleServiceTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly AlertingOptions _options = new();

    public void Dispose() => _factory.Dispose();

    private AlertRuleService Service() => new(_factory, _options, _time);

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);

    private static AlertRuleInput Input(
        IReadOnlyList<string>? groups = null,
        AlertRuleType type = AlertRuleType.Threshold,
        int windowSeconds = 300,
        int intervalSeconds = 60,
        IReadOnlyList<Guid>? channels = null) => new()
        {
            Name = "deny sağanağı",
            OwnerGroups = groups ?? ["network/core"],
            RuleType = type,
            WindowSeconds = windowSeconds,
            IntervalSeconds = intervalSeconds,
            Threshold = 100,
            ChannelIds = channels ?? [],
        };

    [Fact]
    public async Task Kendi_kapsamindaki_grup_icin_kural_yazilabiliyor()
    {
        var result = await Service().SaveAsync(null, Input(), Core, Token);

        Assert.True(result.Ok);
        Assert.Equal("network/core", result.Rule!.OwnerGroups);
        Assert.Equal("analyst.core", result.Rule.OwnerSubject);

        // Kaydedilen kural bir sonraki turda koşmalı: değişikliğin etkisini
        // görmek için aralık kadar beklemek kural yazanı kör bırakırdı.
        Assert.Null(result.Rule.NextRunAt);
    }

    [Fact]
    public async Task Kapsam_disindaki_grup_icin_kural_yazilamiyor()
    {
        var result = await Service().SaveAsync(null, Input(["network/edge"]), Core, Token);

        Assert.False(result.Ok);
        Assert.Contains("network/edge", result.Error, StringComparison.Ordinal);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.AlertRules.ToListAsync(Token));
    }

    [Fact]
    public async Task Kismen_kapsam_disi_grup_listesi_de_reddediliyor()
    {
        var result = await Service().SaveAsync(
            null, Input(["network/core", "network/edge"]), Core, Token);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Bos_grup_listesi_her_sey_anlamina_gelmiyor()
    {
        Assert.NotNull(AlertRuleService.ValidateGroups([], Core));
        Assert.NotNull(AlertRuleService.ValidateGroups([], AccessScope.System("admin")));
    }

    [Fact]
    public void Sinirsiz_kapsam_bile_gruplari_acikca_saymak_zorunda()
    {
        var admin = AccessScope.System("admin");

        Assert.Null(AlertRuleService.ValidateGroups(["network/edge"], admin));
        Assert.NotNull(AlertRuleService.ValidateGroups([], admin));
    }

    [Fact]
    public void Virgullu_grup_adi_reddediliyor()
    {
        // Gruplar tek kolonda virgülle saklanıyor; virgüllü bir ad kaydı böler
        // ve kural sessizce başka bir grubu da kapsardı.
        Assert.NotNull(AlertRuleService.ValidateGroups(
            ["network/core,network/edge"], AccessScope.System("admin")));
    }

    /// <summary>K16: tek kötü kural ClickHouse'u doyurur — kapı kaydetme anında.</summary>
    [Fact]
    public async Task Cok_genis_pencere_kaydetme_aninda_reddediliyor()
    {
        var result = await Service().SaveAsync(
            null, Input(windowSeconds: 30 * 86_400), Core, Token);

        Assert.False(result.Ok);
        Assert.Contains("pencere", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oran_kuralinda_sinir_taban_penceresini_de_sayiyor()
    {
        // 24 saatlik sınır, oran tipinde fiilen 48 saat olmamalı: iki pencere
        // okunuyor ve maliyet toplamlarının.
        var options = new AlertingOptions { MaxWindowSeconds = 86_400 };
        var service = new AlertRuleService(_factory, options, _time);

        var esik = await service.SaveAsync(null, Input(windowSeconds: 86_400), Core, Token);
        Assert.True(esik.Ok);

        var oran = await service.SaveAsync(
            null, Input(type: AlertRuleType.Ratio, windowSeconds: 86_400), Core, Token);
        Assert.False(oran.Ok);
    }

    [Fact]
    public async Task Cok_sik_aralik_reddediliyor()
    {
        var result = await Service().SaveAsync(null, Input(intervalSeconds: 1), Core, Token);

        Assert.False(result.Ok);
        Assert.Contains("aralığı", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kapsam_disindaki_kural_listede_gorunmuyor()
    {
        await Service().SaveAsync(null, Input(), Core, Token);
        await Service().SaveAsync(
            null, Input(["network/edge"]), AccessScope.ForGroups("analyst.edge", ["network/edge"]), Token);

        var core = await Service().ListAsync(Core, Token);
        var edge = await Service().ListAsync(AccessScope.ForGroups("analyst.edge", ["network/edge"]), Token);

        Assert.Equal("network/core", Assert.Single(core).OwnerGroups);
        Assert.Equal("network/edge", Assert.Single(edge).OwnerGroups);
    }

    [Fact]
    public async Task Kapsam_disindaki_kural_guncellenemiyor()
    {
        var edge = AccessScope.ForGroups("analyst.edge", ["network/edge"]);
        var created = await Service().SaveAsync(null, Input(["network/edge"]), edge, Token);

        var result = await Service().SaveAsync(created.Rule!.Id, Input(), Core, Token);

        // "Yetkiniz yok" demek, o kuralın var olduğunu söylerdi.
        Assert.False(result.Ok);
        Assert.Equal("Kural bulunamadı.", result.Error);
    }

    [Fact]
    public async Task Kapsam_disindaki_kural_silinemiyor()
    {
        var edge = AccessScope.ForGroups("analyst.edge", ["network/edge"]);
        var created = await Service().SaveAsync(null, Input(["network/edge"]), edge, Token);

        Assert.False(await Service().DeleteAsync(created.Rule!.Id, Core, Token));
        Assert.True(await Service().DeleteAsync(created.Rule.Id, edge, Token));
    }

    [Fact]
    public async Task Kapsam_disindaki_kanala_baglanamiyor()
    {
        Guid channelId;

        await using (var db = _factory.CreateDbContext())
        {
            var channel = new NotificationChannelEntity
            {
                Name = "edge-slack",
                OwnerGroup = "network/edge",
                ChannelType = NotificationChannelType.Slack,
            };

            db.NotificationChannels.Add(channel);
            await db.SaveChangesAsync(Token);
            channelId = channel.Id;
        }

        var result = await Service().SaveAsync(null, Input(channels: [channelId]), Core, Token);

        Assert.False(result.Ok);
        Assert.Contains("edge-slack", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kural_silinince_tetiklenme_gecmisi_kaliyor()
    {
        var created = await Service().SaveAsync(null, Input(), Core, Token);

        await using (var db = _factory.CreateDbContext())
        {
            db.AlertTriggers.Add(new AlertTriggerEntity
            {
                RuleId = created.Rule!.Id,
                Summary = "5 dk içinde 120 olay",
            });

            await db.SaveChangesAsync(Token);
        }

        Assert.True(await Service().DeleteAsync(created.Rule!.Id, Core, Token));

        await using (var db = _factory.CreateDbContext())
        {
            // Olay incelemesi çoğunlukla kural silindikten SONRA yapılıyor.
            Assert.Single(await db.AlertTriggers.ToListAsync(Token));
        }
    }

    [Fact]
    public void Kaydedilmis_arama_gidip_geliyor()
    {
        var search = new AlertSearch
        {
            FullText = "kullanıcı",
            Filters = [FieldFilter.Eq("action", "deny"), FieldFilter.In("outcome", "failure", "error")],
            SourceIds = ["fw-core-01"],
        };

        var round = AlertSearchCodec.Deserialize(AlertSearchCodec.Serialize(search));

        Assert.Equal(search.FullText, round.FullText);
        Assert.Equal(2, round.Filters.Count);
        Assert.Equal(FilterOperator.In, round.Filters[1].Operator);
        Assert.Equal(["fw-core-01"], round.SourceIds);
    }

    [Fact]
    public void Operator_adlari_veritabaninda_metin_olarak_duruyor()
    {
        // Sayı olarak yazılsaydı enum'a değer eklendiği gün eski satırlar
        // sessizce başka bir operatöre kayardı.
        var json = AlertSearchCodec.Serialize(new AlertSearch
        {
            Filters = [FieldFilter.Eq("action", "deny")],
        });

        Assert.Contains("Equals", json, StringComparison.Ordinal);
    }
}

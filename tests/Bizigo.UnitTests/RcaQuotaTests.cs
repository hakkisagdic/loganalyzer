using Bizigo.ControlPlane;
using Bizigo.Contracts;
using Bizigo.Rca;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Kota kapısının bekçileri (T46, F4 kota kararı §5, §6.1, §9).
///
/// <para>
/// Saat sahte: pencere hesabı duvar saatine bağlı olsaydı test gece yarısına
/// yakın koştuğunda kendiliğinden düşerdi — bu depoda zamana bağlı testlerin
/// bedeli birkaç kez ödendi.
/// </para>
/// </summary>
public sealed class RcaQuotaTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Noon);

    public void Dispose() => _factory.Dispose();

    private RcaQuotaGate Gate(RcaQuotaOptions options) => new(_factory, options, _time);

    private static RcaTriggerRequest Request(
        RcaTriggerSource source = RcaTriggerSource.Alert,
        string ownerGroup = "network/core") => new()
        {
            Source = source,
            Identity = "fw-core-01",
            OwnerGroup = ownerGroup,
            WindowFrom = Noon.AddMinutes(-15),
            WindowTo = Noon,
        };

    /// <summary>Kotadan düşülen ya da düşülmeyen bir koşum satırı yazar.</summary>
    private async Task SeedAsync(int count, RcaTriggerSource source, bool counts, string group = "network/core")
    {
        await using var db = _factory.CreateDbContext();

        for (var i = 0; i < count; i++)
        {
            db.RcaRuns.Add(new RcaRunEntity
            {
                OwnerGroup = group,
                Source = source,
                RequestedAt = Noon.AddMinutes(-i),
                CountsAgainstQuota = counts,
                State = counts ? RcaRunState.Complete : RcaRunState.Rejected,
                Accepted = counts,
            });
        }

        await db.SaveChangesAsync(Token);
    }

    [Fact]
    public void Takvim_gunu_penceresi_UTC_gece_yarisina_hizali()
    {
        // Yerel saate hizalamak, aynı grubun kotasının sunucunun bulunduğu yere
        // göre farklı anda sıfırlanması demekti.
        var start = RcaQuotaGate.WindowStart(Noon, RcaQuotaWindow.CalendarDay);

        Assert.Equal(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void Kayan_pencere_yirmi_dort_saat_geriye_bakiyor()
    {
        Assert.Equal(Noon.AddHours(-24), RcaQuotaGate.WindowStart(Noon, RcaQuotaWindow.Rolling24Hours));
    }

    [Fact]
    public async Task Kota_sifirsa_hicbir_sey_reddedilmiyor()
    {
        // Varsayılan sınırsız: ölçülmemiş bir kota ölçülmüş gibi davranırdı (§5).
        await SeedAsync(1_000, RcaTriggerSource.Alert, counts: true);

        var reason = await Gate(new RcaQuotaOptions()).CheckAsync(Request(), Token);

        Assert.Equal(RcaRejectionReason.None, reason);
    }

    [Fact]
    public async Task Kota_dolunca_QuotaExceeded_donuyor()
    {
        await SeedAsync(5, RcaTriggerSource.Alert, counts: true);

        var reason = await Gate(new RcaQuotaOptions { DailyPerGroup = 5 }).CheckAsync(Request(), Token);

        Assert.Equal(RcaRejectionReason.QuotaExceeded, reason);
    }

    /// <summary>
    /// <b>§9 birinci bulgu.</b> Girişte reddedilen satırlar sayaca girmiyor;
    /// girseydi kota bir kez dolduktan sonra kendini besleyerek asla açılmazdı.
    /// </summary>
    [Fact]
    public async Task Kapida_reddedilen_satirlar_sayaca_girmiyor()
    {
        await SeedAsync(20, RcaTriggerSource.Alert, counts: false);

        var usage = await Gate(new RcaQuotaOptions { DailyPerGroup = 5 }).UsageAsync("network/core", Token);

        Assert.Equal(0, usage.Used);
        Assert.False(usage.Exhausted);
    }

    [Fact]
    public async Task Baska_grubun_kosumlari_sayaca_girmiyor()
    {
        await SeedAsync(10, RcaTriggerSource.Alert, counts: true, group: "network/edge");

        var usage = await Gate(new RcaQuotaOptions { DailyPerGroup = 5 }).UsageAsync("network/core", Token);

        Assert.Equal(0, usage.Used);
    }

    [Fact]
    public async Task Onceki_pencerenin_kosumlari_sayaca_girmiyor()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.RcaRuns.Add(new RcaRunEntity
            {
                OwnerGroup = "network/core",
                Source = RcaTriggerSource.Alert,
                // Dün: takvim günü penceresinin dışında.
                RequestedAt = Noon.AddDays(-1),
                CountsAgainstQuota = true,
                State = RcaRunState.Complete,
                Accepted = true,
            });

            await db.SaveChangesAsync(Token);
        }

        var usage = await Gate(new RcaQuotaOptions { DailyPerGroup = 1 }).UsageAsync("network/core", Token);

        Assert.Equal(0, usage.Used);
    }

    /// <summary>
    /// <b>§6.1'in kararı.</b> Takvimli senaryolar kotayı öngörülebilir biçimde ve
    /// baştan tüketebilir; rezervasyon olay tetikli işin aç kalmamasını sağlıyor.
    /// </summary>
    [Fact]
    public void Rezervasyon_yalnizca_takvim_kaynagini_daraltiyor()
    {
        const int daily = 100;
        const int reserve = 25;

        // Olay tetikli üç kaynak tam havuzu görüyor.
        foreach (var source in new[] { RcaTriggerSource.Alert, RcaTriggerSource.Manual, RcaTriggerSource.External })
        {
            Assert.Equal(daily, RcaQuotaGate.EffectiveLimit(daily, reserve, source));
        }

        // Takvim, rezervasyon düşülmüş hâlini görüyor.
        Assert.Equal(75, RcaQuotaGate.EffectiveLimit(daily, reserve, RcaTriggerSource.Schedule));
    }

    [Fact]
    public void Rezervasyon_kapaliyken_tek_havuz()
    {
        // Varsayılan davranış: §5 sayı önermiyor, mekanizma hazır duruyor.
        Assert.Equal(100, RcaQuotaGate.EffectiveLimit(100, 0, RcaTriggerSource.Schedule));
    }

    [Fact]
    public async Task Takvim_rezervasyonu_asinca_reddediliyor_ama_alarm_gecebiliyor()
    {
        // 75 takvim koşumu: takvimin tavanı dolu, havuzda 25 yer var.
        await SeedAsync(75, RcaTriggerSource.Schedule, counts: true);

        var gate = Gate(new RcaQuotaOptions { DailyPerGroup = 100, EventReservePercent = 25 });

        Assert.Equal(
            RcaRejectionReason.QuotaExceeded,
            await gate.CheckAsync(Request(RcaTriggerSource.Schedule), Token));

        // Asıl kazanç bu satır: olay tetikli iş aç kalmadı.
        Assert.Equal(
            RcaRejectionReason.None,
            await gate.CheckAsync(Request(RcaTriggerSource.Alert), Token));
    }

    /// <summary>
    /// Kaynak başına sayaç <b>rezervasyon kapalıyken de</b> doluyor — §6.1'in
    /// riskini görünür kılan tek şey bu. Operatör rezervasyonun gerekli olduğunu
    /// ancak veriden görebilir.
    /// </summary>
    [Fact]
    public async Task Kaynak_basina_tuketim_rezervasyon_kapaliyken_de_sayiliyor()
    {
        await SeedAsync(7, RcaTriggerSource.Schedule, counts: true);
        await SeedAsync(3, RcaTriggerSource.Alert, counts: true);

        var usage = await Gate(new RcaQuotaOptions { DailyPerGroup = 100 }).UsageAsync("network/core", Token);

        Assert.Equal(10, usage.Used);
        Assert.Equal(7, usage.BySource[RcaTriggerSource.Schedule]);
        Assert.Equal(3, usage.BySource[RcaTriggerSource.Alert]);
    }

    [Fact]
    public async Task Sinirsiz_kotada_kalan_sonsuz()
    {
        await SeedAsync(4, RcaTriggerSource.Alert, counts: true);

        var usage = await Gate(new RcaQuotaOptions()).UsageAsync("network/core", Token);

        Assert.Equal(int.MaxValue, usage.Remaining);
        Assert.False(usage.Exhausted);
    }

    [Fact]
    public async Task Kalan_hak_dogru_hesaplaniyor()
    {
        await SeedAsync(3, RcaTriggerSource.Alert, counts: true);

        var usage = await Gate(new RcaQuotaOptions { DailyPerGroup = 10 }).UsageAsync("network/core", Token);

        Assert.Equal(7, usage.Remaining);
    }
}

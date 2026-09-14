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
    /// <b>Rezerv KAPIDAN geçiyor: ajan daraltılmış sınırda reddedilirken alarm
    /// aynı havuzda hâlâ geçiyor</b> (M15).
    ///
    /// <para>
    /// Saf fonksiyon testi ekseni ölçüyor; bu test <b>bağı</b> ölçüyor —
    /// <see cref="RcaQuotaGate.CheckAsync"/> gerçekten <c>EffectiveLimit</c>'i
    /// çağırıyor mu. İkisi ayrı, çünkü ayrı kaybediliyor: saf fonksiyon doğru
    /// olup kapının onu hiç çağırmadığı bir hâl mümkün, ve o hâlde rezerv
    /// açılmasına rağmen hiçbir şey değişmez.
    /// </para>
    ///
    /// <h3>⚠️ Bu testin TOHUMLAMASI bir kırmızı ölçümünde düzeltildi</h3>
    ///
    /// <para>
    /// İlk hâli <c>DailyPerGroup = 4</c> ile <b>dört</b> koşum yazıyordu, yani
    /// <b>tam havuz da doluydu</b> (4/4). O kurulumda ajanın <i>ve</i> alarmın
    /// reddedilmesi <b>rezervden bağımsız</b> olarak doğruydu — test iki reddi
    /// iddia ediyordu ve ikisi rezerv hiç çalışmasa da tutuyordu.
    /// </para>
    ///
    /// <para>
    /// §6 ölçümü bunu yakaladı: <b>üç ayrı kusurda YEŞİL KALDI</b> (eksen eski
    /// hâline döndüğünde, kapı <c>EffectiveLimit</c>'i çağırmadığında, ve rezerv
    /// hesabı sıfırlandığında). Yani yeşilliği hiçbir şey ifade etmiyordu — bu
    /// deponun adını koyduğu sınıf, ve bu kez benim yazdığım bekçide.
    /// </para>
    ///
    /// <para>
    /// Düzeltilmiş kurulum <b>rezerve duyarlı</b>: iki koşum, ajanın sınırı iki
    /// (%50 rezerv), alarmın sınırı dört. Ajan reddediliyor <b>yalnızca rezerv
    /// yüzünden</b> — rezerv olmasa sınırı dört olurdu ve geçerdi. Alarm ise
    /// geçiyor, ve o satır rezervin var olma sebebi.
    /// </para>
    ///
    /// <para>
    /// İki yön <b>tek testte</b> ve bilerek: ayrı testlere bölünseydi ikisi de
    /// aynı tohumlamayı kurar ve aynı şeyi iki kez ölçerdi (§9). Ölçülen şey bir
    /// çift — <i>daraltılan</i> ve <i>korunan</i>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rezerv_kapidan_geciyor_ajani_daraltip_alarmi_koruyor()
    {
        var options = new RcaQuotaOptions { DailyPerGroup = 4, EventReservePercent = 50 };

        // İKİ koşum: ajanın daraltılmış sınırı (2) dolu, tam havuz (4) DEĞİL.
        // Sayı bu yüzden iki — dört olsaydı ret rezervden bağımsız doğru olurdu.
        await SeedAsync(2, RcaTriggerSource.Alert, counts: true);

        var gate = Gate(options);

        // AJAN reddediliyor — YALNIZCA rezerv yüzünden.
        Assert.Equal(
            RcaRejectionReason.QuotaExceeded,
            await gate.CheckAsync(Request(RcaTriggerSource.Agent), Token));

        // ALARM GEÇİYOR: sınırı tam havuz. Rezervin bütün amacı bu satır, ve
        // olmadan yukarıdaki ret "her şeyi reddet" hâliyle de yeşil kalırdı.
        Assert.Equal(
            RcaRejectionReason.None,
            await gate.CheckAsync(Request(RcaTriggerSource.Alert), Token));
    }

    /// kalmamasını sağlıyor: <see cref="RcaTriggerSource.Alert"/> tam havuzu
    /// görüyor, <b>istek tetikli her kaynak</b> rezerv düşülmüş hâlini.
    ///
    /// <para>
    /// <b>Bu test bir kusuru tuttuğu için yeniden yazıldı (M15).</b> Eski hâli
    /// <c>Rezervasyon_yalnizca_takvim_kaynagini_daraltiyor</c> adıyla duruyordu ve
    /// <c>Manual</c> ile <c>External</c>'ın tam havuzu gördüğünü <b>iddia
    /// ediyordu</b> — ikisini "olay tetikli" diye adlandırarak. Aynı yanlış
    /// eksen üç yerde birden yazılıydı: uygulamada
    /// (<c>source != Schedule</c>), <c>EffectiveLimit</c>'in belgesinde
    /// (<c>Alert</c>, <c>User</c>, <c>Api</c> — son ikisi enum'da bile yok) ve
    /// burada. <b>Bekçi vardı ve YANLIŞ EKSENDE yeşildi</b>; kusuru hiçbir şeyin
    /// yakalamamasının sebebi buydu.
    /// </para>
    ///
    /// <para>
    /// <b>Küme artık enum'dan TÜRETİLİYOR</b>, elle yazılmıyor. Eski hâl üç
    /// kaynağı elle sayıyordu ve <c>Agent</c> eklendiğinde onu <b>hiç
    /// görmüyordu</b> — yani bekçi sessizce eksik bir kümeyi denetlemeye başladı.
    /// Türetilmiş küme altıncı bir kaynağı kendiliğinden kapsıyor: eklemeyi yapan
    /// kişi bu satırı hiç görmese de kaynağı ölçülüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Rezervasyon_istek_tetikli_her_kaynagi_daraltiyor()
    {
        const int daily = 100;
        const int reserve = 25;

        // OLAY TETİKLİ tam havuzu görüyor — rezerv onun İÇİN ayrılıyor.
        Assert.Equal(daily, RcaQuotaGate.EffectiveLimit(daily, reserve, RcaTriggerSource.Alert));

        // İSTEK TETİKLİ olanların hepsi rezerv düşülmüş hâlini görüyor. Küme
        // enum'dan türüyor, yani yeni bir kaynak kendiliğinden kapsama giriyor.
        var requestTriggered = Enum.GetValues<RcaTriggerSource>()
            .Where(static source => source is not RcaTriggerSource.Alert)
            .ToArray();

        // Ölçüm aracının kendisi: küme boşalırsa aşağıdaki döngü hiçbir şey
        // ölçmez ve test her zaman yeşil kalır (§6).
        Assert.NotEmpty(requestTriggered);

        foreach (var source in requestTriggered)
        {
            Assert.Equal(75, RcaQuotaGate.EffectiveLimit(daily, reserve, source));
        }

        // AGENT AÇIKÇA ANILIYOR — M14'ün getirdiği kaynak ve M15'in öznesi.
        // Türetilmiş küme onu zaten kapsıyor; bu satır kapsadığını okunur
        // kılıyor, yoksa bir sonraki kişi "ajan korunuyor mu" sorusunu
        // enum'dan çıkarmak zorunda kalır.
        Assert.Contains(RcaTriggerSource.Agent, requestTriggered);

        // KARŞI-KANIT: rezerv sıfırsa hiçbir kaynak daraltılmıyor — tek havuz,
        // varsayılan davranış. Olmadan yukarıdaki iddialar "her zaman daralt"
        // diyen bir uygulamayla da geçerdi.
        foreach (var source in Enum.GetValues<RcaTriggerSource>())
        {
            Assert.Equal(daily, RcaQuotaGate.EffectiveLimit(daily, reservePercent: 0, source));
        }
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

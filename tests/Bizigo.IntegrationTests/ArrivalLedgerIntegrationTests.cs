using System.Globalization;
using Bizigo.Capacity;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>B02 — varış defterinin ürün katmanı, gerçek yığına karşı.</b>
///
/// <para>
/// <b>YAZILDI, KOŞTURULMADI</b> (CLAUDE.md §2 — ClickHouse ve ayakta bir
/// collector gerektiriyor). Birim testleri defterin <i>hükmünü</i> ve
/// <i>ayrıştırıcılarını</i> kapsıyor; bu dosya onların <b>göremediği</b> üç şeyi
/// kanıtlıyor:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Ürün katmanının sondası gerçek okuyucularla bağlanıyor.</b> Defterin
/// hiçbir proje referansı yok ve bu bilinçli; bedeli, sondanın gerçekten
/// bağlanabildiğinin <b>ölçülmemiş</b> kalması. Bu test o bedeli ödüyor:
/// <see cref="EventReader.CountAsync"/> ile <c>events</c> sayımı ve kapsam
/// yüklemi defterin beklediği şekle <b>oturuyor mu</b>.
/// </item>
/// <item>
/// <b>Collector'ın metrik ucu GERÇEKTEN erişilebilir.</b> Bu, bu ticket'ın en
/// somut ölçüm kalemi: uç bu dalda yapılandırıldı
/// (<c>deploy/otel/collector.yaml</c> · <c>readers:</c>) ve
/// <c>docker-compose.yml</c> 8888'i yayınlıyor. İkisi birlikte anlamlı; biri
/// eksikse <b>sessizce</b> çalışmıyor. Yalnızca ayakta bir yığın bunu
/// gösterebilir — ve gösterene kadar defterin ikinci katmanı
/// <c>LEDGER-LIMITED</c>.
/// </item>
/// <item>
/// <b>Metrik ADI beklenen ad.</b> Uç elle yapılandırıldığı için sayaç
/// <c>..._total</c> ekiyle yayılabiliyor; okuyucu iki adı da kabul ediyor, ama
/// hangisinin geldiği <b>ölçülmedi</b>. Bu testin ikinci iddiası tam olarak
/// bu — ve düşerse cevap "defter bozuk" değil "yapılandırma bayrağı işlemedi".
/// </item>
/// </list>
///
/// <para>
/// <b>Bu test bir yük koşumu DEĞİL.</b> Yük üreteci B01'in işi; burada
/// beklenen sayı elle basılan küçük bir kümeden geliyor. Kapasite sayısı
/// üretmeye çalışmak, ölçülmemiş bir EPS iddiası doğururdu — ve o iddia
/// makineye bağlı (kapasite belgesi §6, cevaplanmamış soru).
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ArrivalLedgerIntegrationTests(DevStackFixture stack)
{
    /// <summary>
    /// Collector'ın metrik ucu — <c>deploy/docker-compose.yml</c>'de yayınlanan
    /// port. Sabit <b>burada</b> duruyor ve testin adı onu söylüyor: uç
    /// taşınırsa kırmızı yanan şey bir bekçi olmalı, sessiz bir
    /// <c>LEDGER-LIMITED</c> değil.
    /// </summary>
    private const string MetricsEndpoint = "http://localhost:8888/metrics";

    /// <summary>
    /// Ürün katmanının sondası: <c>events</c> sayımı, koşum penceresi ve
    /// <c>owner_group</c> ile.
    ///
    /// <para>
    /// <b>Kapsam kapısından geçiyor</b> — <see cref="ScopePredicate"/> ile.
    /// Defterin kendi sorgusunu yazması, kapsam zorlamasının ikinci bir
    /// kopyasını doğurmak olurdu (§9); sayım ürünün okuyucusundan geliyor,
    /// defter yalnızca sayıyı biliyor.
    /// </para>
    /// </summary>
    private static async Task<LedgerReading> SearchableAsync(
        EventReader reader,
        string ownerGroup,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = await reader.CountAsync(
                new EventQuery { From = from, To = to },
                ScopePredicate.From(AccessScope.ForGroups("b02-ledger", [ownerGroup])),
                cancellationToken);

            return LedgerReading.Measured(LedgerLayer.Product, "events", count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // KAYIP DEĞİL, ÖLÇEMEDİM. Bir sorgu hatasını sıfır satıra çevirmek,
            // defterin bütün mantığını yalan söyletirdi: hüküm "ürün her şeyi
            // kaybetti" olurdu.
            return LedgerReading.Limited(LedgerLayer.Product, "events", $"sorgu düştü: {ex.GetType().Name}");
        }
    }

    private static async Task<LedgerReading> ScrapeAsync(
        string metric,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var text = await client.GetStringAsync(new Uri(MetricsEndpoint), cancellationToken);

            return CollectorMetricsReader.Parse(text, metric);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LedgerReading.Limited(
                LedgerLayer.Collector,
                metric,
                $"{MetricsEndpoint} okunamadı: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> collector'ın metrik ucu ayakta ve
    /// <c>otelcol_receiver_accepted_log_records</c> ailesi <b>okunabiliyor</b>.
    ///
    /// <para>
    /// Düşerse iki ayrı sebep olabilir ve ikisi ayrı düzeltme istiyor:
    /// (a) uç erişilemiyor → <c>readers:</c> bloğu ya da yayınlanan port
    /// eksik; (b) uç ayakta ama metrik yok → collector o alıcıdan hiç kayıt
    /// görmemiş. Kısıt gerekçesi ikisini <b>ayırıyor</b>, o yüzden iddia
    /// gerekçeyi de basıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Collector_metrik_ucu_okunabiliyor()
    {
        var accepted = await ScrapeAsync(
            CollectorMetricsReader.AcceptedMetric, TestContext.Current.CancellationToken);

        Assert.True(
            accepted.IsMeasured,
            $"Collector metrik ucu okunamadı — defterin ikinci katmanı LEDGER-LIMITED. " +
            $"Gerekçe: {accepted.LimitReason}");

        // Reddedilen sayacı da AYRI okunuyor: sıfır olması bir DEĞER, yokluğu
        // ise bir kısıt — ikisini karıştıran bir defter reddi görmezden gelir.
        var refused = await ScrapeAsync(
            CollectorMetricsReader.RefusedMetric, TestContext.Current.CancellationToken);

        Assert.True(refused.IsMeasured, refused.LimitReason);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> beş okumanın hepsi gerçek
    /// kaynaklardan geliyor ve defter tek bir rapor üretiyor.
    ///
    /// <para>
    /// <b>Hükmün kendisi burada iddia EDİLMİYOR</b> ve bu bilinçli: bu test bir
    /// yük koşumu değil, dolayısıyla <c>events</c>'te kaç satır olduğu
    /// yığının o anki hâline bağlı. Hüküm mantığı birim testlerinde çivili;
    /// burada sınanan şey <b>bağlantı</b> — sondaların gerçek okuyucularla
    /// çalıştığı ve raporun beş okumayı da taşıdığı.
    /// </para>
    ///
    /// <para>
    /// Tel katmanı bu koşumda büyük olasılıkla <c>LEDGER-LIMITED</c> dönecek ve
    /// <b>doğru davranış bu</b>: collector container'da koşuyor, yani host'un
    /// <c>/proc/net/udp</c>'si (Linux'ta) o soketi görmüyor — macOS'ta ise
    /// okuyucu hiç yok. Defter bunu "tel temiz" diye raporlamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Defter_bes_okumayi_gercek_kaynaklardan_topluyor()
    {
        var token = TestContext.Current.CancellationToken;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-15);

        using var context = stack.CreateClickHouseContext();
        var reader = new EventReader(context);

        var ledger = new ArrivalLedger(
            RunId: "b02-integration",
            Expected: 0,
            WireDrops: WireDropReader.Read(
                5141,
                () => File.Exists(WireDropReader.ProcNetUdp)
                    ? File.ReadAllText(WireDropReader.ProcNetUdp)
                    : null,
                () => null),
            CollectorAccepted: await ScrapeAsync(CollectorMetricsReader.AcceptedMetric, token),
            CollectorRefused: await ScrapeAsync(CollectorMetricsReader.RefusedMetric, token),
            ProductArchived: LedgerReading.Measured(LedgerLayer.Product, "raw_manifest", 0),
            ProductSearchable: await SearchableAsync(reader, "golden", from, to, token));

        var report = ledger.Report();

        // Rapor tek metin ve BEŞ okumayı da taşıyor — bitti ölçütünün birinci
        // yarısı, gerçek kaynaklarla.
        Assert.Contains("varış defteri", report, StringComparison.Ordinal);
        Assert.Contains("events", report, StringComparison.Ordinal);
        Assert.Contains(CollectorMetricsReader.AcceptedMetric, report, StringComparison.Ordinal);

        // Ürün katmanı okunabilmiş olmalı: sonuç kolonu o, ve okunamıyorsa
        // defterin söyleyecek hiçbir şeyi yok.
        Assert.True(
            ledger.ProductSearchable.IsMeasured,
            $"`events` sayımı okunamadı: {ledger.ProductSearchable.LimitReason}");

        Assert.Equal(
            ledger.LimitedReadings.Count == 0 ? LedgerVerdict.Consistent : ledger.Verdict,
            ledger.Verdict);

        // Ve kısıtlar rapora yazılı: hangi katmanın ölçülemediği görünmüyorsa
        // bir sonraki koşum aynı körlükle koşar.
        foreach (var limited in ledger.LimitedReadings)
        {
            Assert.Contains(limited.Source, report, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(limited.LimitReason));
        }
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> kümülatif sayaçların farkı bir
    /// koşumu ölçüyor.
    ///
    /// <para>
    /// İki kazıma arasında yığına dokunulmuyor, yani fark <b>sıfır</b> olmalı.
    /// Sıfırdan farklı çıkması iki şey anlatır ve ikisi de bilgi: ya başka bir
    /// üretici aynı collector'a basıyor (ölçüm yalnız değil), ya da sayaç
    /// sıfırlandı — ikincisini defter zaten <c>LEDGER-LIMITED</c> ile
    /// yakalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kumulatif_sayac_farki_bos_pencerede_sifir()
    {
        var token = TestContext.Current.CancellationToken;

        var before = await ScrapeAsync(CollectorMetricsReader.AcceptedMetric, token);
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        var after = await ScrapeAsync(CollectorMetricsReader.AcceptedMetric, token);

        var delta = CollectorMetricsReader.Delta(before, after);

        Assert.True(delta.IsMeasured, delta.LimitReason);

        Assert.Equal(
            0,
            delta.Value ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"fark okunamadı: {delta.LimitReason}")));
    }
}

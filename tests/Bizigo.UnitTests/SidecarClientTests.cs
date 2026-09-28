using System.Net;
using System.Text;
using Bizigo.Ingest.Discovery;

namespace Bizigo.UnitTests;

/// <summary>
/// Sidecar HTTP istemcisi (F1 §9).
///
/// <para>
/// Tek bir davranış hepsinin altında: <b>istemci istisna fırlatmaz</b>. Fırlatsa
/// keşif işçisi düşer, keşif sessizce ölür ve <c>template_id</c>'nin neden boş
/// kaldığını kimse aramaz. Hata bu yüzden veri olarak dönüyor.
/// </para>
/// </summary>
public sealed class SidecarClientTests
{
    private static readonly MineRequest Request =
        new("firewall", [new MineMessage("0", "deny tcp 10.0.0.1")]);

    /// <summary>
    /// Sonda istemci. <b>Bütçe <see cref="Timeout.InfiniteTimeSpan"/></b> ve bu
    /// bir ihmal değil, ölçülmüş bir düzeltme.
    ///
    /// <para>
    /// İlk hâli <c>TimeSpan.FromSeconds(2)</c> idi ve <b>duvar saatini bu
    /// sınıfın ölçmediği yere sokuyordu</b>. Ölçüldü: 41 dakika süren bir tam
    /// paket koşumunda (üç <c>dotnet test</c> yan yana, koordinatör paralel
    /// merge yapıyordu) <c>Surum_uyusmazligi_isaretleniyor</c> düştü — sonda
    /// işleyicisi <c>Task.FromResult</c> ile <b>anında</b> dönüyor, ama CPU
    /// açlığında sürekliliğin zamanlanması 2 saniyeyi aşıyor ve istemci
    /// <i>sürüm uyuşmazlığı</i> yerine <b>zaman aşımı</b> raporluyor. Aynı
    /// testler boş makinede <b>5/5 yeşil, 458 ms</b>.
    /// </para>
    ///
    /// <para>
    /// §6'nın reçetesi: <i>test neyi ölçmek istiyor? Duvar saati değilse süreyi
    /// denklemden çıkar.</i> Bu sınıfın beş testi <b>ayrıştırmayı, sürüm
    /// karşılaştırmasını ve hata sözleşmesini</b> ölçüyor; hiçbiri süreyi
    /// ölçmüyor. Süreyi ölçen tek test kendi bütçesini kendisi kuruyor
    /// (<see cref="Zaman_asimi_iptal_ediliyor_ve_isaretleniyor"/>) ve
    /// <b>dokunulmadı</b> — yoksa zaman aşımı davranışı hiç sınanmamış olurdu.
    /// </para>
    ///
    /// <para>
    /// Bu, deponun ikinci sidecar örneği: <c>DiscoveryWorkerTests</c> 200 ms'lik
    /// bir sidecar zaman aşımıyla ThreadPool doygunluğunu ölçüyordu ve
    /// *"kararsız test"* diye raporlanmıştı. Değildi.
    /// </para>
    /// </summary>
    private static SidecarClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        SidecarOptions? options = null)
    {
        options ??= new SidecarOptions { Timeout = Timeout.InfiniteTimeSpan };
        var http = new HttpClient(new StubHandler(handler))
        {
            BaseAddress = new Uri("http://sidecar.test/"),
        };

        return new SidecarClient(options, http);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Basarili_yanit_ayristiriliyor()
    {
        using var client = Client((_, _) => Task.FromResult(Json(
            """
            {"api_version":"v1","masks_version":1,"cluster_count":3,
             "results":[{"id":"0","template_id":"firewall:2","template":"deny tcp <IPV4>",
                         "is_new":true,"masked":"deny tcp <IPV4>"}]}
            """)));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Response);
        Assert.Equal(3, outcome.Response.ClusterCount);

        var result = Assert.Single(outcome.Response.Results);
        Assert.Equal("firewall:2", result.TemplateId);
        Assert.True(result.IsNew);
        Assert.Equal("deny tcp <IPV4>", result.Masked);
    }

    [Fact]
    public async Task Surum_uyusmazligi_isaretleniyor()
    {
        using var client = Client((_, _) => Task.FromResult(Json(
            """{"api_version":"v2","masks_version":1,"cluster_count":0,"results":[]}""")));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.VersionMismatch);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task Maske_surumu_uyusmazligi_isaretleniyor()
    {
        // Farklı maske sürümü = farklı imza = yanlış `template_id`.
        //
        // Bütçe `InfiniteTimeSpan`: bu seçenek nesnesi `MasksVersion` için var,
        // süre için değil. İlk hâli 2 sn yazıyordu ve yardımcının varsayılanını
        // ELLE tekrar ediyordu — yani düzeltme yardımcıda yapılsa burası
        // hastalığı taşımaya devam ederdi.
        var options = new SidecarOptions { MasksVersion = 1, Timeout = Timeout.InfiniteTimeSpan };
        using var client = Client(
            (_, _) => Task.FromResult(Json(
                """{"api_version":"v1","masks_version":9,"cluster_count":0,"results":[]}""")),
            options);

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.VersionMismatch);
    }

    [Fact]
    public async Task HTTP_hatasi_istisna_yerine_sonuc_donuyor()
    {
        using var client = Client((_, _) =>
            Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Response);
        Assert.Contains("500", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baglanti_hatasi_istisna_yerine_sonuc_donuyor()
    {
        using var client = Client((_, _) =>
            throw new HttpRequestException("Connection refused"));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Response);
        Assert.False(outcome.TimedOut);
        Assert.Contains("Connection refused", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zaman_asimi_iptal_ediliyor_ve_isaretleniyor()
    {
        // F1 §9: 2 sn; aşan istek iptal edilir. Testte 100 ms.
        var options = new SidecarOptions { Timeout = TimeSpan.FromMilliseconds(100) };
        using var client = Client(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return Json("{}");
            },
            options);

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.Response);
    }

    /// <summary>
    /// <b>Duvar saati bu sınıfa geri giremiyor</b> — ve muafiyet adıyla yazılı.
    ///
    /// <para>
    /// Yukarıdaki düzeltme tek başına <b>yetmez</b>: bir sonraki test
    /// <c>Timeout = TimeSpan.FromSeconds(2)</c> yazarak aynı hastalığı geri
    /// getirebilir, ve belirtisi yine <b>başka bir turda, yüklü bir makinede,
    /// ilgisiz görünen bir kırmızı</b> olur. Bu bekçi o yolu kapatıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Muafiyet bir tane ve gerekçeli:</b> süreyi <i>ölçen</i> testin bir
    /// bütçesi olmak zorunda. Onu da kapatmak, zaman aşımı davranışını hiç
    /// sınanmamış hâle getirirdi — yani bir kusuru düzeltirken ölçümü silmek.
    /// Sayı sabitle çivili: ikinci bir bütçe eklemek <b>iki bilinçli hareket</b>
    /// gerektiriyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bu bekçinin TUTAMADIĞI:</b> metin okuyor. Bütçe bir sabite ya da
    /// yardımcı metoda taşınırsa (<c>Timeout = KisaBir</c>) görmüyor. Kapsam
    /// beyanı olarak yazılı — ve kapatmaya çalışmak, testin ne ölçtüğünü
    /// anlamaya çalışan bir sezgi yazmak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Duvar_saati_yalnizca_zaman_asimini_olcen_testte()
    {
        var source = File.ReadAllLines(
            Path.Combine(
                RepositoryLayout.Root, "tests", "Bizigo.UnitTests", "SidecarClientTests.cs"));

        // `TimeSpan.From…` yalnızca KOD satırlarında aranıyor: yorumlar bu
        // sınıfın gerekçesini anlatıyor ve içlerinde `TimeSpan.FromSeconds(2)`
        // geçiyor. Bekçinin kendi açıklamasını ihlal sayması, T62'de ölçülmüş
        // bir kusurdu — orada bir bekçi kendi yorumunu okuyup YEŞİL kalmıştı.
        // Aranan dizge PARÇALI kuruluyor ve bu bir üslup tercihi değil: ilk
        // hâlinde arama satırı `"Timeout = TimeSpan.From"` dizgesini birebir
        // taşıyordu ve bekçi KENDİNİ ihlal saydı (ölçüldü — 2 bütçe buldu,
        // ikincisi bu satırın kendisiydi). Bu deponun üçüncü örneği: bekçiyi
        // körleştiren ya da yanlış suçlayan şey, bekçinin kendi metni.
        var needle = "Timeout = TimeSpan" + ".From";

        var budgets = source
            .Select(static line => line.Trim())
            .Where(static line => !line.StartsWith("//", StringComparison.Ordinal)
                && !line.StartsWith("///", StringComparison.Ordinal))
            .Where(line => line.Contains(needle, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            budgets.Length == ExpectedWallClockBudgets,
            $"Bu sınıfta {budgets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + $"duvar saati bütçesi var, beklenen {ExpectedWallClockBudgets.ToString(System.Globalization.CultureInfo.InvariantCulture)}:\n  "
            + string.Join("\n  ", budgets)
            + "\n\nBu testlerin ölçtüğü şey SÜRE DEĞİL — ayrıştırma, sürüm karşılaştırması, hata "
            + "sözleşmesi. Sonlu bir bütçe, yüklü bir makinede o iddiaları zaman aşımına çevirip "
            + "düşürüyor (ölçüldü: 41 dakikalık bir koşumda düştü, boş makinede 5/5 yeşil). "
            + "Süreyi ölçmüyorsanız `Timeout.InfiniteTimeSpan` kullanın; ölçüyorsanız bu sayıyı "
            + "artırın ve testin adını buraya yazın.");

        // Ve muafiyetin SAHİBİ adıyla sabit: bütçe süreyi ölçen testin içinde
        // olmalı, başka bir yerde değil.
        Assert.Contains("FromMilliseconds(100)", budgets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Duvar saati bütçesi taşımasına <b>izin verilen</b> test sayısı: bir, ve o
    /// <see cref="Zaman_asimi_iptal_ediliyor_ve_isaretleniyor"/>.
    /// </summary>
    private const int ExpectedWallClockBudgets = 1;

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}

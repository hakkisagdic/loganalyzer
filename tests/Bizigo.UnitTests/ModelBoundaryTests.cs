using System.Net;
using System.Reflection;
using Bizigo.Contracts.Security;
using Bizigo.Rca.Models;

namespace Bizigo.UnitTests;

/// <summary>
/// K6'nın kapısı — <i>log verisi kurum dışına çıkmaz</i> (T42).
///
/// <para>
/// Docker yok, ağ yok: adres çözümü <see cref="SahteCozucu"/> ile
/// değiştiriliyor (§2). Bir bekçinin kırmızı yanabildiğini göstermek için ona
/// yönlendirilebilir bir adres verebilmek gerekiyor ve gerçek DNS bunu
/// tekrarlanabilir yapamazdı.
/// </para>
///
/// <para>
/// <b>Bu paketin taşıdığı asıl iddia:</b> K6 bugüne kadar bir <b>cümleydi</b>.
/// Mimari karar tablosunda yazılıydı ve onu tutan hiçbir mekanizma yoktu.
/// Aşağıdaki testler o cümlenin artık bir kapı olduğunu ve <b>yanlış tarafa
/// esnemediğini</b> tutuyor.
/// </para>
/// </summary>
public sealed class ModelBoundaryTests
{
    private sealed class SahteCozucu(params string[] addresses) : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [.. addresses.Select(IPAddress.Parse)]);
    }

    private static ModelEndpointOptions Secenek(
        ModelDataBoundary boundary = ModelDataBoundary.Internal,
        string baseUrl = "http://gpu-kume.kurum.local:8000/v1",
        bool allowRaw = false,
        string? overrideReason = null) => new()
        {
            Name = "kurum-ici-gpu",
            BaseUrl = baseUrl,
            Model = "qwen3-32b",
            DataBoundary = boundary,
            AllowRawContentLevel = allowRaw,
            BoundaryOverrideReason = overrideReason,
        };

    private static ModelBoundaryGate Kapi(params string[] addresses) =>
        new(new SahteCozucu(addresses.Length > 0 ? addresses : ["10.20.30.40"]));

    // ------------------------------------------------------- kural 1

    /// <summary>
    /// <b>Beyansız uç reddediliyor.</b>
    ///
    /// <para>
    /// Bu testin tuttuğu şey bir varsayılan: <c>Unspecified = 0</c> ve sıfırın
    /// "iç ağ" sayılması bu deponun en pahalı hata sınıfı olurdu. Yapılandırmayı
    /// yazan kişi alanı hiç görmemiş olur, ürün çalışır, ve kurumun en büyük
    /// sözü <b>kimse karar vermeden</b> boşa çıkar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Beyansiz_uc_reddediliyor()
    {
        var karar = await Kapi().VerifyAsync(
            Secenek(ModelDataBoundary.Unspecified),
            TestContext.Current.CancellationToken);

        Assert.False(karar.Allowed);
        Assert.Null(karar.Endpoint);
        Assert.Contains("DataBoundary", karar.Rejection, StringComparison.Ordinal);
    }

    /// <summary>
    /// Varsayılanın <b>sayısal olarak</b> sıfır olduğu da sınanıyor: bir gün
    /// biri <c>Internal</c>'ı sıfıra alırsa yukarıdaki test hâlâ geçer ama kapı
    /// açılmış olur.
    /// </summary>
    [Fact]
    public void Beyanin_varsayilani_reddedilen_deger()
    {
        Assert.Equal(ModelDataBoundary.Unspecified, new ModelEndpointOptions().DataBoundary);
        Assert.Equal(0, (int)ModelDataBoundary.Unspecified);
        Assert.NotEqual(0, (int)ModelDataBoundary.Internal);
    }

    // ------------------------------------------------------- kural 2

    /// <summary>
    /// <b>Kurum dışı uçta HİÇBİR düzey geçmiyor — <c>summary</c> dahil.</b>
    ///
    /// <para>
    /// Kapının düzey ekseninde durmamasının sınavı bu. Özet de müşteri verisi:
    /// <i>"edge-rtr-07 14:02'de sustu"</i> cümlesi host adı, sahiplik grubu ve
    /// topoloji taşıyor. K6 "ham log çıkmaz" demiyor, <b>"log verisi çıkmaz"</b>
    /// diyor. Kapıyı düzey eksenine kurmak, K6'ya düzeylerden birini istisna
    /// yazmak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kurum_disi_ucta_hicbir_duzey_gecmiyor()
    {
        var karar = await Kapi("203.0.113.10").VerifyAsync(
            Secenek(ModelDataBoundary.External, "https://model.ornek.com/v1"),
            TestContext.Current.CancellationToken);

        Assert.False(karar.Allowed);
        Assert.Contains("summary", karar.Rejection, StringComparison.Ordinal);

        // Ve uç üretilmediği için hiçbir düzey için istek KURULAMIYOR:
        // reddin düzey başına tekrar sorulması gerekmiyor, tip zaten yok.
        Assert.Null(karar.Endpoint);
    }

    // ------------------------------------------------------- kural 3

    /// <summary>
    /// <b>Beyan ile gerçek çelişiyorsa reddediliyor.</b> `internal` yazılmış
    /// ama ad genel bir adrese çözülüyor — kazara yapılandırmanın en olası
    /// hâli: base URL'e bir bulut sağlayıcısının adresi yapıştırılıyor ve
    /// beyan olduğu gibi kalıyor.
    /// </summary>
    [Fact]
    public async Task Ic_beyan_genel_adrese_cozulurse_reddediliyor()
    {
        var karar = await Kapi("203.0.113.10").VerifyAsync(
            Secenek(),
            TestContext.Current.CancellationToken);

        Assert.False(karar.Allowed);
        Assert.Contains("203.0.113.10", karar.Rejection, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adreslerden biri bile genelse yetiyor.</b> Çift yığınlı bir ad
    /// içeriden ve dışarıdan birden çözülebiliyor; "biri iç ağ, geçsin" demek
    /// dışarıdaki yolu görünmez yapardı.
    /// </summary>
    [Fact]
    public async Task Adreslerden_biri_genelse_ret()
    {
        var karar = await Kapi("10.1.2.3", "203.0.113.10").VerifyAsync(
            Secenek(),
            TestContext.Current.CancellationToken);

        Assert.False(karar.Allowed);
    }

    /// <summary>
    /// <b>Çözülemeyen ad iç ağ sayılmıyor.</b> "Bakamadım" ile "temiz" aynı
    /// çıktıya inerse bekçi bir gürültü bastırıcıya dönüşür.
    /// </summary>
    [Fact]
    public async Task Cozulemeyen_ad_ic_ag_sayilmiyor()
    {
        var karar = await new ModelBoundaryGate(new SahteCozucu()).VerifyAsync(
            Secenek(),
            TestContext.Current.CancellationToken);

        Assert.False(karar.Allowed);
        Assert.Contains("çözülmedi", karar.Rejection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("172.16.5.9")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.10.10")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void Yonlendirilemez_adres_siniflari(string address) =>
        Assert.True(ModelBoundaryGate.YonlendirilemezMi(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]   // 172.16/12'nin DIŞI — sınırın doğru yerde olduğu
    [InlineData("172.15.255.255")]
    [InlineData("192.169.0.1")]
    [InlineData("2001:4860:4860::8888")]
    public void Yonlendirilebilir_adres_siniflari(string address) =>
        Assert.False(ModelBoundaryGate.YonlendirilemezMi(IPAddress.Parse(address)), address);

    /// <summary>Kurum içi uç geçiyor — kapının yanlış yöne de esnememesi.</summary>
    [Fact]
    public async Task Kurum_ici_uc_geciyor()
    {
        var karar = await Kapi("10.20.30.40").VerifyAsync(
            Secenek(),
            TestContext.Current.CancellationToken);

        Assert.True(karar.Allowed, karar.Rejection);
        Assert.False(karar.Endpoint!.BoundaryOverridden);
        Assert.Single(karar.Endpoint.VerifiedAddresses);
    }

    // ------------------------------------------------------- muafiyet

    /// <summary>
    /// <b>Muafiyet var ama bedava değil.</b> Kurumun kendi AS'inde
    /// yönlendirilebilir adres kullanan bir GPU kümesi, adres sınıfına bakan
    /// bir kapıda yanlış yere düşer — bilinen bir yanlış pozitif. Çözümü
    /// muafiyet, ama <b>gerekçe yazılmadan açılmıyor</b> ve gerekçe koşum
    /// kaydına giriyor: sessiz bir muafiyet, muafiyetin olmamasından tehlikeli.
    /// </summary>
    [Fact]
    public async Task Muafiyet_gerekce_istiyor_ve_gerekce_kayda_giriyor()
    {
        var gerekcesiz = await Kapi("203.0.113.10").VerifyAsync(
            Secenek(overrideReason: "   "),
            TestContext.Current.CancellationToken);

        Assert.False(gerekcesiz.Allowed);

        var gerekceli = await Kapi("203.0.113.10").VerifyAsync(
            Secenek(overrideReason: "AS64500 kurumun kendi bloğu, VRF içinde"),
            TestContext.Current.CancellationToken);

        Assert.True(gerekceli.Allowed, gerekceli.Rejection);
        Assert.True(gerekceli.Endpoint!.BoundaryOverridden);
        Assert.Equal(
            "AS64500 kurumun kendi bloğu, VRF içinde",
            gerekceli.Endpoint.AuditFields()["model_boundary_override_reason"]);

        // Muafiyet kullanıldığında doğrulanmış adres YOK — ve boş olması
        // "doğrulanmadı" demek, "adres yok" değil.
        Assert.Empty(gerekceli.Endpoint.VerifiedAddresses);
    }

    // ------------------------------------------------------- T41'in şartı

    /// <summary>
    /// <b>Modele giden metin redaksiyon kapısından geçmiş olmak zorunda</b> —
    /// T41'in T42'ye koyduğu şart.
    ///
    /// <para>
    /// İddia bir çalışma zamanı kontrolü değil bir <b>tip</b>:
    /// <see cref="ModelRequest"/> ve <see cref="IModelProvider"/> girdi olarak
    /// <see cref="RedactedPrompt"/> istiyor, dolayısıyla <c>string</c> gönderen
    /// bir çağrı <b>derlenmiyor</b>. Test bunu yansımayla tutuyor: sağlayıcı
    /// arayüzünde <c>string</c> alan bir giriş belirirse düşer.
    /// </para>
    /// </summary>
    [Fact]
    public void Modele_string_gonderen_bir_yol_yok()
    {
        var girisler = typeof(IModelProvider)
            .GetMethods()
            .Where(m => !m.IsSpecialName)
            .SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(string))
            .Select(p => p.Name ?? "?")
            .ToArray();

        Assert.True(
            girisler.Length == 0,
            "IModelProvider `string` alıyor: " + string.Join(", ", girisler) +
            ". Redaksiyon kapısı bir tipe bağlıydı; `string` girişi onu bir çağrı " +
            "alışkanlığına çevirir ve unutulduğu gün hiçbir şey kırılmaz.");

        // Ve istek tipinin tek üreticisi kendi fabrikası: yapıcı private.
        var yapicilar = typeof(ModelRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotEmpty(yapicilar);
        Assert.All(yapicilar, c => Assert.True(c.IsPrivate, $"Yapıcı private değil: {c}"));

        // Aynı şey doğrulanmış uç için: onu ancak kapı üretebiliyor.
        Assert.All(
            typeof(ModelEndpoint).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => Assert.True(c.IsPrivate, $"ModelEndpoint yapıcısı private değil: {c}"));

        Assert.Empty(typeof(ModelEndpoint).GetConstructors());
    }

    // ------------------------------------------------------- düzey ekseni

    /// <summary>
    /// <b>Düzey sağlayıcı türüne bağlı değil</b> — T42 kararı. Aynı uçta
    /// <c>summary</c> ve <c>masked</c> geçiyor; <c>raw</c> ise ayrı bir
    /// bilinçli hareket istiyor ve o hareket <b>kurum</b> hakkında, sağlayıcı
    /// hakkında değil.
    /// </summary>
    [Fact]
    public async Task Duzey_kurumun_karari_saglayicinin_degil()
    {
        var kapali = (await Kapi("10.1.2.3").VerifyAsync(Secenek(), TestContext.Current.CancellationToken)).Endpoint;
        var acik = (await Kapi("10.1.2.3").VerifyAsync(Secenek(allowRaw: true), TestContext.Current.CancellationToken)).Endpoint;

        Assert.NotNull(kapali);
        Assert.NotNull(acik);

        var sistem = RedactedPrompt.Redact("Sen bir RCA yardımcısısın.");
        var kullanici = RedactedPrompt.Redact("EV-01: edge-rtr-07 sustu.");

        foreach (var duzey in new[] { PromptContentLevel.Summary, PromptContentLevel.Masked })
        {
            Assert.True(
                ModelRequest.Create(kapali, duzey, sistem, kullanici).Allowed,
                $"{duzey} düzeyi kapalı çıktı — düzey ekseni sağlayıcıya bağlanmış olabilir.");
        }

        var ret = ModelRequest.Create(kapali, PromptContentLevel.Raw, sistem, kullanici);
        Assert.False(ret.Allowed);
        Assert.Contains("AllowRawContentLevel", ret.Rejection, StringComparison.Ordinal);

        Assert.True(ModelRequest.Create(acik, PromptContentLevel.Raw, sistem, kullanici).Allowed);
    }

    /// <summary>
    /// İsteğin kanıt alanları: uç, düzey ve <b>T41'in gölge sayıları</b> bir
    /// arada — ve gölge sayıları <b>sıfırken de</b> yazılıyor.
    /// </summary>
    [Fact]
    public async Task Istek_kanit_alanlarini_yayiyor()
    {
        var uc = (await Kapi("10.1.2.3").VerifyAsync(Secenek(), TestContext.Current.CancellationToken)).Endpoint!;

        var istek = ModelRequest.Create(
            uc,
            PromptContentLevel.Masked,
            RedactedPrompt.Redact("sistem"),
            RedactedPrompt.Redact("EV-01: edge-rtr-07 sustu.")).Request!;

        var alanlar = istek.AuditFields();

        Assert.Equal("masked", alanlar["prompt_content_level"]);
        Assert.Equal("kurum-ici-gpu", alanlar["model_endpoint"]);
        Assert.Equal("Internal", alanlar["model_boundary"]);
        Assert.Equal(false, alanlar["model_boundary_overridden"]);
        Assert.Equal(0, alanlar["redaction_shadow_candidates"]);
        Assert.True(alanlar.ContainsKey("redaction_shadow_entropy_threshold"));
    }

    /// <summary>
    /// <b>Sır taşıyan bir kanıt satırı modele giderken maskeleniyor</b> — iki
    /// ticket'ın birleştiği yerin uçtan uca sınavı. Sahte sır T41'in fixture
    /// konvansiyonundan okunuyor, teste yazılmıyor.
    /// </summary>
    [Fact]
    public async Task Sir_tasiyan_kanit_satiri_modele_maskelenmis_gidiyor()
    {
        var fixtur = Path.Combine(
            RepositoryLayout.Root, "catalog", "simulators", "profiller", "asa-dc-01", "sir-tasiyan.log");

        var satirlar = File.ReadAllLines(fixtur);
        var beklenen = satirlar
            .Where(l => l.StartsWith("# BEKLENEN:", StringComparison.Ordinal))
            .Select(l => l["# BEKLENEN:".Length..].Trim())
            .ToArray();

        var govde = string.Join('\n', satirlar.Where(l => !l.StartsWith('#')));

        Assert.NotEmpty(beklenen);

        var uc = (await Kapi("10.1.2.3").VerifyAsync(Secenek(allowRaw: true), TestContext.Current.CancellationToken)).Endpoint!;

        var istek = ModelRequest.Create(
            uc,
            PromptContentLevel.Raw,
            RedactedPrompt.Redact("sistem"),
            RedactedPrompt.Redact(govde)).Request!;

        foreach (var sir in beklenen)
        {
            Assert.DoesNotContain(sir, istek.User.Text, StringComparison.Ordinal);
        }

        Assert.True(istek.User.MaskedValues > 0);
    }
}

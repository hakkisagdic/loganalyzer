using System.Text;
using Bizigo.Api.Webhooks;

namespace Bizigo.UnitTests;

/// <summary>
/// T24'ün iki kabul kriteri: <b>doğrulanmamış webhook kayıt oluşturmaz</b> ve
/// <b>gizli anahtar hiçbir çıktıda görünmez</b>.
///
/// <para>
/// İkincisi bir bekçi testi ve kırmızı yanabildiği önemli: doğrulayıcıya
/// "beklenen imza şuydu" diyen tek bir yardımsever hata mesajı eklendiği gün
/// <see cref="Gizli_anahtar_hicbir_ciktida_gorunmuyor"/> düşer.
/// </para>
/// </summary>
public sealed class ChangeWebhookSignatureTests
{
    private const string Secret = "s3cr3t-webhook-anahtari-2f8c";

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"action":"completed","repository":{"full_name":"bizigo/network-config"}}""");

    private static ChangeWebhookEndpoint Endpoint(string provider) => new()
    {
        Id = "ci",
        Provider = provider,
        OwnerGroup = "network/core",
        Secret = Secret,
    };

    private static Func<string, string?> Headers(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void Github_dogru_hmac_ile_geciyor()
    {
        var signature = WebhookSignature.Compute(Secret, Body);

        var verdict = WebhookSignature.Verify(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-Hub-Signature-256", signature)),
            Body);

        Assert.Equal(SignatureVerdict.Valid, verdict);
        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
    }

    [Fact]
    public void Govdenin_tek_bayti_degisince_imza_tutmuyor()
    {
        // İmzanın gövdeye bağlı olduğunun kanıtı: yalnızca anahtara bağlı olsaydı
        // ele geçirilen bir imza her gövdeyi geçirirdi.
        var signature = WebhookSignature.Compute(Secret, Body);
        var tampered = Body.ToArray();
        tampered[^2] ^= 0x01;

        Assert.Equal(
            SignatureVerdict.Invalid,
            WebhookSignature.Verify(
                Endpoint(ChangeWebhookProviders.GitHub),
                Headers(("X-Hub-Signature-256", signature)),
                tampered));
    }

    [Fact]
    public void Imza_basligi_yoksa_reddediliyor()
    {
        Assert.Equal(
            SignatureVerdict.Missing,
            WebhookSignature.Verify(Endpoint(ChangeWebhookProviders.GitHub), Headers(), Body));
    }

    [Fact]
    public void Baska_anahtarla_hesaplanan_imza_reddediliyor()
    {
        var signature = WebhookSignature.Compute("baska-anahtar", Body);

        Assert.Equal(
            SignatureVerdict.Invalid,
            WebhookSignature.Verify(
                Endpoint(ChangeWebhookProviders.GitHub),
                Headers(("X-Hub-Signature-256", signature)),
                Body));
    }

    [Theory]
    [InlineData("sha256=deadbeef")]      // doğru biçim, yanlış uzunluk
    [InlineData("sha256=zzzz")]          // hex değil
    [InlineData("sha1=abcdef")]          // yanlış algoritma öneki
    [InlineData("   ")]                  // boş
    public void Bozuk_imza_bicimi_kabul_edilmiyor(string presented)
    {
        var verdict = WebhookSignature.Verify(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-Hub-Signature-256", presented)),
            Body);

        Assert.NotEqual(SignatureVerdict.Valid, verdict);
    }

    [Fact]
    public void Gitlab_jetonu_duz_metin_karsilastiriliyor()
    {
        // GitLab HMAC üretmiyor; sağlayıcının verdiği tek şey paylaşılan jeton.
        var endpoint = Endpoint(ChangeWebhookProviders.GitLab);

        Assert.Equal(
            SignatureVerdict.Valid,
            WebhookSignature.Verify(endpoint, Headers(("X-Gitlab-Token", Secret)), Body));

        Assert.Equal(
            SignatureVerdict.Invalid,
            WebhookSignature.Verify(endpoint, Headers(("X-Gitlab-Token", Secret + "x")), Body));
    }

    [Fact]
    public void Jenkins_ve_genel_saglayici_kendi_basligimizi_kullaniyor()
    {
        // İkisinin de standart bir imza başlığı yok.
        Assert.Equal(
            WebhookSignature.DefaultHeader,
            WebhookSignature.HeaderFor(Endpoint(ChangeWebhookProviders.Jenkins)));

        Assert.Equal(
            WebhookSignature.DefaultHeader,
            WebhookSignature.HeaderFor(Endpoint(ChangeWebhookProviders.Generic)));
    }

    [Fact]
    public void Baslik_adi_uctan_gecersiz_kilinabiliyor()
    {
        var endpoint = Endpoint(ChangeWebhookProviders.Generic);
        endpoint.SignatureHeader = "X-Ozel-Imza";

        Assert.Equal("X-Ozel-Imza", WebhookSignature.HeaderFor(endpoint));

        Assert.Equal(
            SignatureVerdict.Valid,
            WebhookSignature.Verify(
                endpoint,
                Headers(("X-Ozel-Imza", WebhookSignature.Compute(Secret, Body))),
                Body));
    }

    [Fact]
    public void Anahtarsiz_uc_dogrulamayi_atlamiyor()
    {
        // "Gizli anahtar yoksa imza arama" varsayılanı, eksik tek bir
        // yapılandırma satırını sessiz bir açığa çevirirdi.
        var endpoint = Endpoint(ChangeWebhookProviders.GitHub);
        endpoint.Secret = string.Empty;

        Assert.Equal(
            SignatureVerdict.NotConfigured,
            WebhookSignature.Verify(
                endpoint,
                Headers(("X-Hub-Signature-256", WebhookSignature.Compute("herhangi", Body))),
                Body));
    }

    [Fact]
    public void Gizli_anahtar_hicbir_ciktida_gorunmuyor()
    {
        var endpoint = Endpoint(ChangeWebhookProviders.GitHub);

        // 1) Uç nesnesinin metin gösterimi — bir log satırına en kolay bu yoldan
        //    düşer. `record` olsaydı üretilmiş ToString() anahtarı basardı.
        Assert.DoesNotContain(Secret, endpoint.ToString(), StringComparison.Ordinal);
        Assert.Equal("webhook:ci", endpoint.ToString());

        // 2) Doğrulayıcının döndürdüğü her yargı — mesaj taşımıyor, yalnızca enum.
        foreach (var presented in new[] { "sha256=deadbeef", string.Empty, WebhookSignature.Compute("x", Body) })
        {
            var verdict = WebhookSignature.Verify(
                endpoint, Headers(("X-Hub-Signature-256", presented)), Body);

            Assert.DoesNotContain(Secret, verdict.ToString(), StringComparison.Ordinal);
        }

        // 3) Yapılandırma doğrulamasının hata mesajı — anahtarın YOKLUĞUNU
        //    söylüyor, değerini değil.
        var options = new ChangeWebhookOptions();
        options.Endpoints.Add(new ChangeWebhookEndpoint
        {
            Id = "eksik",
            Provider = ChangeWebhookProviders.GitHub,
            OwnerGroup = string.Empty,
            Secret = Secret,
        });

        var error = Assert.Throws<InvalidOperationException>(() => new ChangeWebhookRegistry(options));
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eksik_yapilandirma_acilista_patliyor()
    {
        // Sessizce pasif kalmak yerine gürültülü ölmek: anahtarsız ya da gruptan
        // yoksun bir uç "çalışıyor" gibi görünen bir açıktır.
        foreach (var broken in new[]
        {
            new ChangeWebhookEndpoint { Id = "a", Provider = ChangeWebhookProviders.GitHub, OwnerGroup = "g" },
            new ChangeWebhookEndpoint { Id = "b", Provider = ChangeWebhookProviders.GitHub, Secret = Secret },
            new ChangeWebhookEndpoint { Id = "c", Provider = "bitbucket", OwnerGroup = "g", Secret = Secret },
            new ChangeWebhookEndpoint { Provider = ChangeWebhookProviders.GitHub, OwnerGroup = "g", Secret = Secret },
        })
        {
            var options = new ChangeWebhookOptions();
            options.Endpoints.Add(broken);

            Assert.Throws<InvalidOperationException>(() => new ChangeWebhookRegistry(options));
        }
    }

    [Fact]
    public void Pasif_uc_bulunamiyor()
    {
        var options = new ChangeWebhookOptions();
        options.Endpoints.Add(new ChangeWebhookEndpoint
        {
            Id = "kapali",
            Provider = ChangeWebhookProviders.GitHub,
            OwnerGroup = "network/core",
            Secret = Secret,
            Enabled = false,
        });

        // "Var ama kapalı" ayrımını dışarı vermek, uç kimliklerini deneyerek
        // keşfetmeye kapı açardı.
        Assert.Null(new ChangeWebhookRegistry(options).Find("kapali"));
    }
}

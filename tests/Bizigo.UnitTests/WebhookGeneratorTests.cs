using System.Text;
using System.Text.Json;
using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Bizigo.Simulators;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>İmzalı webhook üreteci</b> (S07) — T24'ün alıcısını <b>gönderen
/// tarafından</b> sınayan testler.
///
/// <para>
/// <b>Bu sınıfın var olma sebebi:</b> T24'ün testleri kendi kurdukları gövdeyi
/// kendi imzalıyor, yani imza doğrulamasının <i>doğru</i> olduğunu değil
/// <b>kendi kendiyle tutarlı</b> olduğunu kanıtlıyorlar. Buradaki gövdeyi ürün
/// kurmuyor: simülatör kuruyor, sağlayıcının belgelediği biçimde imzalıyor, ve
/// alıcı onu tanımak zorunda kalıyor.
/// </para>
///
/// <para>
/// <b>Kendi kendini doğrulama tuzağına düşmemek için iki ayrı çapa var</b> —
/// biri olmadan diğeri yeterli değil:
/// </para>
/// <list type="number">
///   <item>
///     İmza <b>yeniden yazıldı</b> (<see cref="WebhookDeliveryFactory"/>),
///     <see cref="WebhookSignature.Compute"/> çağrılmadı. Aynı koddan beslenen
///     bir üreteç ile doğrulayıcı, biçim hatasını ikisinde birden taşırdı.
///   </item>
///   <item>
///     Gövdenin şekli <b>sağlayıcıların yayımladığı yüke</b> karşı sınanıyor
///     (<c>Fixtures/webhooks</c>). Üretecin uydurduğu bir alan kırmızı yanıyor.
///   </item>
/// </list>
///
/// <para>
/// <b>Bu paketin sınamadığı şey</b> ve bilerek: bir kaydın gerçekten
/// oluştuğu. O bir veritabanı kısıtı ve konteyner istiyor —
/// <c>Bizigo.IntegrationTests.WebhookGeneratorDeliveryTests</c> (§2).
/// </para>
/// </summary>
public sealed class WebhookGeneratorTests
{
    /// <summary>
    /// Sabit an: üretecin belirlenimciliği ölçülebilsin diye. Duvar saatinden
    /// beslenen bir üreteç, "aynı teslimat" kavramını imkânsız kılardı (§6).
    /// </summary>
    private static readonly DateTimeOffset Moment = new(2026, 8, 18, 9, 19, 47, TimeSpan.Zero);

    /// <summary>
    /// Alınma anı, üretecin yazdığı andan <b>bilerek farklı</b>.
    ///
    /// <para>
    /// İkisi aynı olsaydı "sağlayıcının zaman biçimi ayrıştırıldı" ile
    /// "ayrıştırılamadı ve şimdiye düşüldü" <b>aynı değeri</b> üretirdi — yani
    /// zamanı sınayan iddia hiçbir şey ifade etmezdi. Bu, deponun §6'da
    /// tarif ettiği "ölçüm sabit bir girdiyle kusursuz hâli iki kez ölçüyor"
    /// tuzağının bu testteki hâli; ilk yazımında tam olarak buna düştüm.
    /// </para>
    /// </summary>
    private static readonly DateTimeOffset Received = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly FakeTimeProvider Clock = new(Received);

    private const string Secret = "paylasilan-anahtar";

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(RepositoryLayout.WebhookFixtureDirectory, name));

    /// <summary>Her sağlayıcı için: üretecin biçimi ↔ o sağlayıcının gerçek yükü.</summary>
    public static TheoryData<string, string> ProviderFixtures => new()
    {
        { WebhookProviders.GitHub, "github-workflow-run-completed.json" },
        { WebhookProviders.GitLab, "gitlab-pipeline-success.json" },
        { WebhookProviders.Jenkins, "jenkins-completed.json" },
        { WebhookProviders.Generic, "generic-netbox.json" },
    };

    private static WebhookDelivery Delivery(
        string provider,
        string deliveryId = "teslimat-1",
        int run = 184) =>
        WebhookDeliveryFactory.Create(new WebhookDeliveryRequest
        {
            Provider = provider,
            Secret = Secret,
            DeliveryId = deliveryId,
            TargetId = "fw-ankara-01",
            Actor = "esra.yildiz",
            Summary = "fw-ankara-01: dış ACL'e 10.20.0.0/16 eklendi",
            Timestamp = Moment,
            Run = run,
        });

    private static ChangeWebhookEndpoint Endpoint(string provider)
    {
        var endpoint = new ChangeWebhookEndpoint
        {
            Id = "ci",
            Provider = provider,
            OwnerGroup = "network/core",
            Secret = Secret,
            TargetKind = ChangeTargetKind.Config,
            DefaultChangeKind = "deploy",
        };

        if (provider == WebhookProviders.Generic)
        {
            // Genel sağlayıcıda gövdenin şekline ALICI karar vermiyor, ucun
            // yapılandırması karar veriyor. Yollar `ChangeWebhookMappingTests`
            // ile aynı — ikinci bir eşleme yazmak, üretecin o eşlemeye göre
            // ayarlandığını gizlerdi.
            endpoint.Mapping.TargetId = "$.data.name";
            endpoint.Mapping.ChangeKind = "$.event";
            endpoint.Mapping.Actor = "$.username";
            endpoint.Mapping.Summary = "$.data.status.label";
            endpoint.Mapping.Timestamp = "$.timestamp";
            endpoint.Mapping.ExternalRef = "$.data.url";
            endpoint.Mapping.DeliveryId = "$.request_id";
            endpoint.Mapping.Details["site"] = "$.data.site.name";
            endpoint.Mapping.Details["vendor"] = "$.data.device_type.manufacturer";
            endpoint.Mapping.Details["onceki"] = "$.snapshots.prechange.status";
        }

        return endpoint;
    }

    private static Func<string, string?> Headers(WebhookDelivery delivery) =>
        name => delivery.Headers.TryGetValue(name, out var value) ? value : null;

    // --------------------------------------------------------------- sözleşme

    /// <summary>
    /// <b>Üretecin sağlayıcı listesi ürünle aynı.</b>
    ///
    /// <para>
    /// İki liste var çünkü referans tek yönlü: simülatör ürünü görüyor, ürün
    /// simülatörü görmüyor. İki liste ayrışabilir — bu test o ayrışmayı
    /// <b>okuma disiplinine değil mekanizmaya</b> bağlıyor. Test projesi
    /// ikisini birden gören tek yer.
    /// </para>
    /// </summary>
    [Fact]
    public void Uretecin_saglayici_listesi_urunle_ayni()
    {
        Assert.Equal(
            ChangeWebhookProviders.All.OrderBy(p => p, StringComparer.Ordinal),
            WebhookProviders.All.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>Üretecin kullandığı başlık adları, alıcının aradığı adlar.</b>
    ///
    /// <para>
    /// Ticket'ın sorusu buydu: <i>"GitHub'ın gerçekte hangi başlığı gönderdiği
    /// T24'ün testlerinden okunamaz."</i> Burada okunuyor — ve iki taraf
    /// ayrıştığı gün kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub, "X-Hub-Signature-256")]
    [InlineData(WebhookProviders.GitLab, "X-Gitlab-Token")]
    [InlineData(WebhookProviders.Jenkins, "X-Bizigo-Signature")]
    [InlineData(WebhookProviders.Generic, "X-Bizigo-Signature")]
    public void Imza_basligi_alicinin_aradigi_ad(string provider, string expected)
    {
        var delivery = Delivery(provider);

        // Alıcının aradığı ad.
        Assert.Equal(expected, WebhookSignature.HeaderFor(Endpoint(provider)));

        // Üretecin gönderdiği ad.
        Assert.Contains(expected, delivery.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------- imza

    /// <summary>
    /// <b>İmza doğrulaması dört biçimde de geçiyor</b> (S07 kabul kriteri).
    ///
    /// <para>
    /// Dördü aynı şeyi yapmıyor: GitHub HMAC-SHA256'yı <c>sha256=</c> önekiyle
    /// hex yazıyor, GitLab <b>hiç HMAC yapmıyor</b> ve jetonu düz metin
    /// yolluyor, Jenkins'in standardı olmadığı için bizim başlığımız
    /// kullanılıyor. Tek bir "imza" kavramı sanıp hepsini aynı yoldan
    /// üretseydik üçü de geçerdi — ve hiçbiri gerçek sağlayıcıyı temsil etmezdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [InlineData(WebhookProviders.Generic)]
    public void Imza_dort_bicimde_de_dogrulaniyor(string provider)
    {
        var delivery = Delivery(provider);

        var verdict = WebhookSignature.Verify(Endpoint(provider), Headers(delivery), delivery.Body);

        Assert.Equal(SignatureVerdict.Valid, verdict);
    }

    /// <summary>
    /// <b>Bozuk imza reddediliyor — ve reddin sebebi "imza tutmadı"</b>, genel
    /// bir ayrıştırma hatası değil (S07 kabul kriteri).
    ///
    /// <para>
    /// Gövdenin <b>tek bir baytı</b> değiştiriliyor: JSON hâlâ geçerli, alanlar
    /// hâlâ yerinde, yani reddin tek sebebi imza olabilir. Gövdeyi bozup
    /// "reddedildi" görmek, ayrıştırma hatasını imza hatası sanmak olurdu.
    /// </para>
    ///
    /// <para>
    /// GitLab <b>dışarıda</b> ve bu bir eksiklik değil bir olgu: jeton gövdeye
    /// bakmıyor, yani GitLab'da gövde değişikliği imzayı düşürmüyor. Sağlayıcının
    /// zayıflığı burada <b>görünür</b> hâle geliyor — ürünün TLS zorunluluğu
    /// tam olarak bunun için var.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.Jenkins)]
    [InlineData(WebhookProviders.Generic)]
    public void Govde_degisirse_imza_dusuyor(string provider)
    {
        var delivery = Delivery(provider);

        // `esra.yildiz` → `esra.yildiy`: aynı uzunluk, geçerli JSON.
        var tampered = Encoding.UTF8.GetBytes(
            delivery.BodyText.Replace("esra.yildiz", "esra.yildiy", StringComparison.Ordinal));

        Assert.NotEqual(delivery.Body, tampered);

        // Gövde hâlâ ayrıştırılabilir: ret imzadan, biçimden değil.
        using var _ = JsonDocument.Parse(tampered);

        var verdict = WebhookSignature.Verify(Endpoint(provider), Headers(delivery), tampered);

        Assert.Equal(SignatureVerdict.Invalid, verdict);
    }

    /// <summary>
    /// <b>GitLab jetonu HMAC değil</b> — ve bunun yazılı olması gerekiyor.
    ///
    /// <para>
    /// Üreteç GitLab'a da HMAC yapsaydı alıcının <c>VerifyToken</c> dalı hiç
    /// koşmazdı ve o dal bozulduğunda hiçbir test kırmızı yanmazdı. Bu test
    /// dalın gerçekten koştuğunu sabitliyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Gitlab_jetonu_hmac_degil()
    {
        var delivery = Delivery(WebhookProviders.GitLab);

        Assert.Equal(Secret, delivery.Headers[WebhookDeliveryFactory.GitLabTokenHeader]);
        Assert.DoesNotContain("sha256=", delivery.Headers[WebhookDeliveryFactory.GitLabTokenHeader], StringComparison.Ordinal);
    }

    /// <summary>
    /// GitHub imzası <c>sha256=</c> önekli ve <b>küçük harf</b> hex.
    ///
    /// <para>
    /// Büyük harf yazsaydık <c>Convert.FromHexString</c> yine çözerdi — yani
    /// hata <b>affedilir</b> olurdu ve gerçek GitHub'ın ne gönderdiği hiçbir
    /// zaman ölçülmezdi. Affedilen biçim hataları en uzun yaşayanlar.
    /// </para>
    /// </summary>
    [Fact]
    public void Github_imzasi_kucuk_harf_hex_ve_onekli()
    {
        var signature = Delivery(WebhookProviders.GitHub)
            .Headers[WebhookDeliveryFactory.GitHubSignatureHeader];

        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);

        var hex = signature["sha256=".Length..];

        Assert.Equal(64, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
        Assert.All(hex, c => Assert.Contains(c, "0123456789abcdef"));
    }

    // ------------------------------------------ gerçek gövdeye karşı sınama

    /// <summary>
    /// <b>Üretecin bastığı her alan, sağlayıcının gerçek yükünde de var</b>
    /// (S07'nin taşıyıcı kabul kriteri).
    ///
    /// <para>
    /// Bu maddenin ticket'ta durma sebebi: kendi ürettiğini doğrulayan bir
    /// üreteç, T24'ün testlerinin yaptığı şeyi ikinci kez yapardı. Karşılaştırma
    /// yönü <b>üreteç ⊆ gerçek</b>: üretecin uydurduğu bir alan kırmızı yanıyor.
    /// </para>
    ///
    /// <para>
    /// Ters yön (gerçek ⊆ üreteç) <b>bilerek sınanmıyor</b>: gerçek yükler
    /// üretecin hiç ihtiyaç duymadığı onlarca alan taşıyor (<c>node_id</c>,
    /// <c>queue_id</c>, <c>artifacts</c>) ve hepsini basmak, taklidi
    /// zenginleştirmeden bakımı pahalılaştırırdı. Eşlemenin okuduğu alanların
    /// eksiksizliği ayrı bir testin işi
    /// (<see cref="Uretilen_govde_gercek_govdeyle_ayni_alanlari_esliyor"/>).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderFixtures))]
    public void Uretecin_bastigi_her_yol_gercek_govdede_de_var(string provider, string fixture)
    {
        using var real = JsonDocument.Parse(Fixture(fixture));
        using var generated = JsonDocument.Parse(Delivery(provider).Body);

        var realPaths = Paths(real.RootElement).ToHashSet(StringComparer.Ordinal);
        var generatedPaths = Paths(generated.RootElement).ToArray();

        Assert.NotEmpty(generatedPaths);

        var invented = generatedPaths.Where(p => !realPaths.Contains(p)).ToArray();

        Assert.True(
            invented.Length == 0,
            $"{provider} üreteci sağlayıcının yükünde olmayan alan basıyor: {string.Join(", ", invented)}. " +
            $"Karşılaştırılan gerçek yük: Fixtures/webhooks/{fixture}");
    }

    /// <summary>
    /// <b>Üretilen gövde, gerçek gövdeyle aynı alanları eşliyor.</b>
    ///
    /// <para>
    /// Yukarıdaki test üretecin <i>fazlasını</i> yakalıyor; bu test
    /// <b>eksiğini</b>. İkisi ayrı sorular: eksiksiz olmayan bir üreteç hiçbir
    /// uydurma alan basmaz ve ilk testten temiz geçer — ama eşleme onun
    /// gövdesinden yarım bir <c>ChangeEvent</c> çıkarır ve <c>details</c>
    /// haritası sessizce boşalır.
    /// </para>
    ///
    /// <para>
    /// Karşılaştırma <b>alan adları</b> üzerinden, değerler üzerinden değil:
    /// değerler zaten farklı (üreteç filodaki cihazı yazıyor, gerçek yük
    /// GitHub'ın örneğini).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderFixtures))]
    public void Uretilen_govde_gercek_govdeyle_ayni_alanlari_esliyor(string provider, string fixture)
    {
        var endpoint = Endpoint(provider);
        var delivery = Delivery(provider);

        var fromReal = ChangeWebhookMapper.Map(endpoint, Headers(delivery), Fixture(fixture), Clock);
        var fromGenerated = ChangeWebhookMapper.Map(endpoint, Headers(delivery), delivery.Body, Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, fromReal.Outcome);
        Assert.Equal(WebhookMapOutcome.Mapped, fromGenerated.Outcome);

        // `details` boş değerleri taşımıyor, yani anahtar kümesi doğrudan
        // "hangi alanlar çıkarılabildi" sorusunun cevabı.
        Assert.Equal(
            fromReal.Change!.Details.Keys.Order(StringComparer.Ordinal),
            fromGenerated.Change!.Details.Keys.Order(StringComparer.Ordinal));

        Assert.NotEmpty(fromGenerated.Change.TargetId);
        Assert.NotEmpty(fromGenerated.Change.Actor);
        Assert.NotEmpty(fromGenerated.Change.Summary);
        Assert.NotEmpty(fromGenerated.Change.ExternalRef);

        // ZAMAN "ŞİMDİ"YE DÜŞMEDİ: sağlayıcının kendi biçimi gerçekten
        // ayrıştırıldı. Üç biçim üç ayrı dal — GitHub ISO-8601, GitLab
        // `"2026-08-18 09:19:47 UTC"`, Jenkins epoch MİLİSANİYE — ve üreteç
        // hepsini ISO yazsaydı iki dal hiç koşmazdı.
        Assert.Equal(Moment, fromGenerated.Change.Timestamp);
        Assert.NotEqual(Received, fromGenerated.Change.Timestamp);
    }

    // ------------------------------------------------------------ idempotans

    /// <summary>
    /// <b>Aynı istek aynı baytları üretiyor.</b>
    ///
    /// <para>
    /// İdempotansın ön koşulu. Üreteç gövdeye bir <c>Guid</c> ya da
    /// <c>UtcNow</c> koysaydı "aynı teslimat iki kez" hiç kurulamazdı — ve
    /// alıcı iki kayıt oluşturduğunda bu <b>doğru davranış</b> olurdu. Yani
    /// idempotans testi, ölçtüğünü sandığı şeyi ölçmezdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [InlineData(WebhookProviders.Generic)]
    public void Ayni_istek_ayni_baytlari_uretiyor(string provider)
    {
        Assert.Equal(Delivery(provider).Body, Delivery(provider).Body);
    }

    /// <summary>
    /// <b>Aynı teslimat aynı idempotans kimliğini veriyor; farklı olay
    /// farklı.</b>
    ///
    /// <para>
    /// Alıcının teslimat anahtarı <c>{uç}:{teslimat kimliği}</c>, ve o kimlik
    /// eşlemeden geliyor. Kaydın gerçekten tekilleştiği bir <b>veritabanı
    /// kısıtı</b> ve konteyner istiyor (§2) — burada sınanan şey ona giden
    /// girdinin kararlı olduğu.
    /// </para>
    ///
    /// <para>
    /// <b>"Farklı olay"ın ne olduğu sağlayıcıya göre değişiyor</b> ve bu testin
    /// ilk hâli bunu yanlış varsaymıştı: Jenkins'te kimlik teslimat
    /// başlığından değil <b>yapı kimliğinden</b> (<c>{iş}#{numara}:{durum}</c>)
    /// kuruluyor, çünkü eklenti aynı yapıyı üç fazda üç kez gönderiyor. Yani
    /// yalnızca teslimat kimliğini değiştirmek Jenkins'te aynı anahtarı
    /// üretiyor — <b>ürün doğru davranıyordu, test yanlış varsaymıştı.</b>
    /// Ayrım aşağıda ikinci teste taşındı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [InlineData(WebhookProviders.Generic)]
    public void Teslimat_kimligi_ayni_teslimatta_ayni_farkli_olayda_farkli(string provider)
    {
        var endpoint = Endpoint(provider);

        var one = Delivery(provider, "teslimat-1");
        var again = Delivery(provider, "teslimat-1");

        // Farklı olay: hem sağlayıcının teslimat kimliği hem de sağlayıcının
        // KENDİ sayacı (koşu/pipeline/yapı numarası) değişiyor. İkisini birden
        // değiştirmek gerekiyor çünkü dört sağlayıcı kimliği dört ayrı yerden
        // türetiyor.
        var other = Delivery(provider, "teslimat-2", run: 185);

        string Id(WebhookDelivery d) =>
            ChangeWebhookMapper.Map(endpoint, Headers(d), d.Body, Clock).DeliveryId;

        Assert.Equal(Id(one), Id(again));
        Assert.NotEmpty(Id(one));
        Assert.NotEqual(Id(one), Id(other));
    }

    /// <summary>
    /// <b>Jenkins aynı yapıyı ikinci kez bildirdiğinde anahtar değişmiyor</b> —
    /// teslimat başlığı farklı olsa bile.
    ///
    /// <para>
    /// Notification Plugin aynı yapıyı <c>COMPLETED</c> ve <c>FINALIZED</c>
    /// fazlarında iki kez gönderiyor ve alıcı mükerrerliği <b>faz üzerinden
    /// değil yapı kimliği üzerinden</b> çözüyor. Üreteç bunu sınayabilen tek
    /// yer: T24'ün testleri gövdeyi kendi kurduğu için "gerçek Jenkins iki kez
    /// gönderdiğinde ne olur" sorusu oradan okunamıyordu.
    /// </para>
    ///
    /// <para>
    /// Bu testin ayrıca bir kaydı var: yukarıdaki testin ilk hâli bunu bir
    /// <i>kusur</i> sanmıştı. Kusur değil, <b>tasarım</b> — ve yazılı olmasaydı
    /// bir sonraki kişi de aynı yanılgıya düşerdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Jenkins_ayni_yapiyi_ikinci_bildirimde_tekilleştiriyor()
    {
        var endpoint = Endpoint(WebhookProviders.Jenkins);

        var completed = Delivery(WebhookProviders.Jenkins, "jenkins-completed");
        var finalized = Delivery(WebhookProviders.Jenkins, "jenkins-finalized");

        string Id(WebhookDelivery d) =>
            ChangeWebhookMapper.Map(endpoint, Headers(d), d.Body, Clock).DeliveryId;

        Assert.Equal(Id(completed), Id(finalized));
    }

    // -------------------------------------------------------------- yardımcı

    /// <summary>
    /// Bir JSON gövdesindeki tüm yaprak yolları — <c>workflow_run.actor.login</c>
    /// biçiminde.
    ///
    /// <para>
    /// Dizi indisleri <c>[]</c>'e indirgeniyor: üretecin bir diziye kaç eleman
    /// koyduğu bir biçim kararı değil, ve gerçek yükle eleman sayısı üzerinden
    /// karşılaştırmak testi ilgisiz bir sebeple kırardı.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Paths(JsonElement element, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

                    foreach (var nested in Paths(property.Value, path))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Paths(item, prefix + "[]"))
                    {
                        yield return nested;
                    }
                }

                break;

            default:
                if (prefix.Length > 0)
                {
                    yield return prefix;
                }

                break;
        }
    }
}

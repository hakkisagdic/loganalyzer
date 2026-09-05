using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bizigo.Simulators;

/// <summary>
/// Üretecin tanıdığı sağlayıcılar.
///
/// <para>
/// <b>Neden ürünün <c>ChangeWebhookProviders</c>'ı kullanılmıyor:</b> referans
/// yönü tek yönlü kalmalı — simülatör ürünü görüyor, ürün simülatörü hiç
/// görmüyor (<c>Bizigo.Simulators.csproj</c>). Üreteç <c>Bizigo.Api</c>'ye
/// referans verseydi, cihaz simülatörü ürünün web katmanına bağlanırdı.
/// </para>
///
/// <para>
/// İki listenin ayrışması bir bekçiye bağlı
/// (<c>WebhookGeneratorTests.Uretecin_saglayici_listesi_urunle_ayni</c>): test
/// projesi ikisini de görüyor, yani ayrışma <b>okuma disiplinine değil
/// mekanizmaya</b> bağlı.
/// </para>
/// </summary>
public static class WebhookProviders
{
    public const string GitHub = "github";
    public const string GitLab = "gitlab";
    public const string Jenkins = "jenkins";
    public const string Generic = "generic";

    public static readonly string[] All = [GitHub, Jenkins, GitLab, Generic];
}

/// <summary>
/// Gönderilmeye hazır tek bir webhook teslimatı.
///
/// <para>
/// <b><see cref="Body"/> ham bayt ve öyle kalmak zorunda.</b> İmza baytların
/// üzerinde hesaplanıyor; gövde bir nesneye çevrilip yeniden serileştirilirse
/// bir boşluk farkı imzayı geçersiz kılar ve arıza <i>"imza doğrulaması
/// bozuk"</i> diye okunur — oysa imza doğru, gönderen değişmiş olur.
/// </para>
/// </summary>
public sealed record WebhookDelivery(
    string Provider,
    string DeliveryId,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Üretilecek teslimatın içeriği.
///
/// <para>
/// Zaman damgası <b>çağırandan</b> geliyor, <c>UtcNow</c>'dan değil: aynı istek
/// aynı baytları üretmezse idempotans sınaması anlamını kaybeder — gövde
/// hash'ine düşen yol (sağlayıcı teslimat kimliği vermediğinde) her çağrıda
/// farklı bir anahtar üretirdi ve "aynı teslimat iki kez" hiç kurulamazdı.
/// </para>
/// </summary>
public sealed record WebhookDeliveryRequest
{
    public required string Provider { get; init; }

    /// <summary>Paylaşılan gizli anahtar. İmzayı bu üretiyor.</summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Sağlayıcının teslimat kimliği. Aynı değerle iki kez gönderilen teslimat
    /// alıcı tarafında <b>tek kayıt</b> oluşturmalı (S07 kabul kriteri).
    /// </summary>
    public required string DeliveryId { get; init; }

    /// <summary>Değişen şey — filoda bir cihazın adı ya da bir depo yolu.</summary>
    public required string TargetId { get; init; }

    public required string Actor { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string Status { get; init; } = "success";

    public string Branch { get; init; } = "main";

    public string Commit { get; init; } = "9b3f2c1a7d4e8f60b25c93a1de07f4c8b6a2e5d3";

    /// <summary>Sağlayıcının kendi sayacı: koşu numarası, pipeline id, yapı numarası.</summary>
    public int Run { get; init; } = 184;

    /// <summary>
    /// Cihaz profilinden bir istek kurar — S07'nin S01'e bağımlılığı burada.
    ///
    /// <para>
    /// Hedef, filodaki cihazın <b>kendi adı</b>: değişiklik olayı
    /// <c>fw-ankara-01</c>'e düşüyor ve RCA o cihazın olay penceresinde
    /// <i>"öncesinde şu değişti"</i> diyebiliyor. Uydurma bir hedef adı
    /// yazılsaydı üretilen olay hiçbir cihaza bağlanmaz ve F4'ün değişiklik
    /// tetikleyicisi sınanamazdı.
    /// </para>
    /// </summary>
    public static WebhookDeliveryRequest FromProfile(
        SimulatorProfile profile,
        string provider,
        string secret,
        string deliveryId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new WebhookDeliveryRequest
        {
            Provider = provider,
            Secret = secret,
            DeliveryId = deliveryId,
            TargetId = profile.Hostname,
            Actor = "esra.yildiz",
            Summary = $"{profile.Hostname} config dağıtımı",
            Timestamp = timestamp,
        };
    }
}

/// <summary>
/// <b>İmzalı webhook üreteci</b> (S07) — T24'ün alıcısını <b>gönderen
/// tarafından</b> sınayan taraf.
///
/// <para>
/// <b>Neden bu var, alıcı zaten test edilmişken:</b> T24'ün testleri kendi
/// kurdukları gövdeyi kendi imzalıyor. Yani imza doğrulamasının <i>doğru</i>
/// olduğunu değil, <b>kendi kendiyle tutarlı</b> olduğunu kanıtlıyorlar.
/// GitHub'ın gerçekte hangi başlığı hangi biçimde gönderdiği o testlerden
/// okunamaz.
/// </para>
///
/// <para>
/// <b>İmza burada YENİDEN yazılıyor, <c>WebhookSignature.Compute</c>
/// çağrılmıyor.</b> Çağrılsaydı üreteç ile doğrulayıcı aynı koddan beslenir ve
/// biçim kararlarındaki bir hata ikisinde birden bulunurdu — yani test yine
/// kendi kendisiyle tutarlılığı ölçerdi. Bağımsız olan şey algoritma değil
/// (HMAC-SHA256 tek bir şey), <b>biçim</b>: hangi başlık, hangi önek, hex mi
/// base64 mü, büyük harf mi küçük harf mi. Sağlayıcıların ayrıştığı yer de tam
/// olarak orası.
/// </para>
///
/// <para>
/// <b>Gövdenin şekli uydurma değil:</b> alan adları ve iç içe geçmeler
/// sağlayıcıların yayımladığı yükten geliyor ve bir bekçi üretecin bastığı her
/// yolun <c>tests/Bizigo.UnitTests/Fixtures/webhooks</c> altındaki gerçek
/// örnekte de var olduğunu sınıyor. Üretecin uydurduğu bir alan kırmızı yanar.
/// </para>
/// </summary>
public static class WebhookDeliveryFactory
{
    /// <summary>GitHub: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>.</summary>
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>
    /// GitLab HMAC <b>yapmıyor</b>: paylaşılan jetonu düz metin başlıkta
    /// yolluyor. Zayıf, ama sağlayıcının verdiği tek şey bu — ve üretecin işi
    /// sağlayıcıyı düzeltmek değil, ne yaptığını göstermek.
    /// </summary>
    public const string GitLabTokenHeader = "X-Gitlab-Token";

    /// <summary>
    /// Jenkins ve genel sağlayıcılar için: <b>standart yok</b>, başlık bizim.
    /// Ürünün <c>WebhookSignature.DefaultHeader</c>'ı ile aynı olmak zorunda ve
    /// bir bekçi bunu sınıyor.
    /// </summary>
    public const string DefaultSignatureHeader = "X-Bizigo-Signature";

    public static WebhookDelivery Create(WebhookDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Provider switch
        {
            WebhookProviders.GitHub => GitHubBody(request),
            WebhookProviders.GitLab => GitLabBody(request),
            WebhookProviders.Jenkins => JenkinsBody(request),
            WebhookProviders.Generic => GenericBody(request),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Bilinmeyen sağlayıcı '{request.Provider}'. Geçerli: {string.Join(", ", WebhookProviders.All)}."),
        };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };

        switch (request.Provider)
        {
            case WebhookProviders.GitHub:
                headers["X-GitHub-Event"] = "workflow_run";
                headers["X-GitHub-Delivery"] = request.DeliveryId;
                headers[GitHubSignatureHeader] = HmacHeader(request.Secret, body);
                headers["User-Agent"] = "GitHub-Hookshot/bizigo-sim";
                break;

            case WebhookProviders.GitLab:
                headers["X-Gitlab-Event"] = "Pipeline Hook";
                headers["X-Gitlab-Event-UUID"] = request.DeliveryId;

                // Jeton OLDUĞU GİBİ gidiyor — HMAC değil. Üretecin sağlayıcıyı
                // taklit ettiği en aykırı yer burası ve alıcının bunu bilmesi
                // gerekiyor; bilmeseydi GitLab'ın isteği imzasız sayılırdı.
                headers[GitLabTokenHeader] = request.Secret;
                break;

            case WebhookProviders.Jenkins:
            case WebhookProviders.Generic:
                headers[DefaultSignatureHeader] = HmacHeader(request.Secret, body);
                break;
        }

        return new WebhookDelivery(request.Provider, request.DeliveryId, headers, body);
    }

    /// <summary>
    /// <c>sha256=</c> + <b>küçük harf</b> hex.
    ///
    /// <para>
    /// Üç biçim kararı da burada ve üçü de sessizce yanlış olabilir: önek
    /// unutulursa alıcı hex'i çözemez, base64 yazılırsa da çözemez, büyük harf
    /// yazılırsa <c>Convert.FromHexString</c> yine de çözer — yani <b>o üçüncüsü
    /// bir testin yakalayamayacağı kadar affedilir</b> ve gerçek GitHub küçük
    /// harf gönderir.
    /// </para>
    /// </summary>
    private static string HmacHeader(string secret, byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        var hex = new StringBuilder(hash.Length * 2);

        foreach (var b in hash)
        {
            hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return "sha256=" + hex;
    }

    // ---------------------------------------------------------------- GitHub

    private static byte[] GitHubBody(WebhookDeliveryRequest request) => Json(writer =>
    {
        // Sadece `completed` yazılıyor: eşleme diğer aşamaları ATIYOR ve
        // üretecin varsayılanı kaydedilmeyen bir olay olsaydı, "gönderdim ama
        // kayıt yok" hâli normal görünürdü.
        writer.WriteString("action", "completed");

        writer.WriteStartObject("workflow_run");
        writer.WriteNumber("id", 12103904188);
        writer.WriteString("name", "deploy-firewall-config");
        writer.WriteString("head_branch", request.Branch);
        writer.WriteString("head_sha", request.Commit);
        writer.WriteNumber("run_number", request.Run);
        writer.WriteString("event", "push");
        writer.WriteString("status", "completed");
        writer.WriteString("conclusion", request.Status);
        writer.WriteString("html_url", $"https://github.com/bizigo/{request.TargetId}/actions/runs/12103904188");
        writer.WriteString("created_at", Iso(request.Timestamp));
        writer.WriteString("updated_at", Iso(request.Timestamp));

        writer.WriteStartObject("actor");
        writer.WriteString("login", request.Actor);
        writer.WriteEndObject();

        writer.WriteStartObject("head_commit");
        writer.WriteString("id", request.Commit);
        writer.WriteString("message", request.Summary);
        writer.WriteString("timestamp", Iso(request.Timestamp));
        writer.WriteEndObject();

        writer.WriteEndObject();

        writer.WriteStartObject("repository");
        writer.WriteString("name", request.TargetId);

        // Eşlemenin hedefi BURADAN okuyor (`$.repository.full_name`). Eksik
        // olsaydı istek 400 alırdı ve sebebi "hedef kimliği çıkarılamadı"
        // olurdu — üretecin en kolay sessiz hatası bu.
        writer.WriteString("full_name", $"bizigo/{request.TargetId}");
        writer.WriteEndObject();

        writer.WriteStartObject("sender");
        writer.WriteString("login", request.Actor);
        writer.WriteEndObject();
    });

    // ---------------------------------------------------------------- GitLab

    private static byte[] GitLabBody(WebhookDeliveryRequest request) => Json(writer =>
    {
        writer.WriteString("object_kind", "pipeline");

        writer.WriteStartObject("object_attributes");
        writer.WriteNumber("id", request.Run);
        writer.WriteString("ref", request.Branch);
        writer.WriteString("sha", request.Commit);
        writer.WriteString("status", request.Status);

        // GitLab'ın kendi zaman biçimi: `"2026-08-18 09:21:33 UTC"`. Hiçbir
        // standart ayrıştırıcının tanımadığı bu biçim, ürünün
        // `ParseTimestamp`'inde ayrı bir dal olarak duruyor — ve üreteç ISO
        // yazsaydı o dal hiç koşmazdı.
        writer.WriteString("created_at", GitLabTime(request.Timestamp));
        writer.WriteString("finished_at", GitLabTime(request.Timestamp));
        writer.WriteString("url", $"https://gitlab.bizigo.example/net/{request.TargetId}/-/pipelines/{request.Run}");
        writer.WriteEndObject();

        writer.WriteStartObject("user");
        writer.WriteString("username", request.Actor);
        writer.WriteEndObject();

        writer.WriteStartObject("project");
        writer.WriteString("name", request.TargetId);
        writer.WriteString("web_url", $"https://gitlab.bizigo.example/net/{request.TargetId}");
        writer.WriteString("path_with_namespace", $"net/{request.TargetId}");
        writer.WriteEndObject();

        writer.WriteStartObject("commit");
        writer.WriteString("id", request.Commit);
        writer.WriteString("message", request.Summary);
        writer.WriteEndObject();
    });

    // --------------------------------------------------------------- Jenkins

    private static byte[] JenkinsBody(WebhookDeliveryRequest request) => Json(writer =>
    {
        writer.WriteString("name", $"deploy-{request.TargetId}");
        writer.WriteString("display_name", $"deploy-{request.TargetId}");
        writer.WriteString("url", $"job/deploy-{request.TargetId}/");

        writer.WriteStartObject("build");
        writer.WriteString("full_url", $"https://jenkins.bizigo.example/job/deploy-{request.TargetId}/{request.Run}/");
        writer.WriteNumber("number", request.Run);

        // Notification Plugin epoch MİLİSANİYE gönderiyor — saniye değil.
        // Ürünün ayrıştırıcısı aralık kontrolüyle ikisini ayırt ediyor ve
        // üreteç saniye yazsaydı zaman sessizce "şimdi"ye düşerdi.
        writer.WriteNumber("timestamp", request.Timestamp.ToUnixTimeMilliseconds());

        writer.WriteString("phase", "COMPLETED");
        writer.WriteString("status", request.Status.ToUpperInvariant());
        writer.WriteString("url", $"job/deploy-{request.TargetId}/{request.Run}/");

        writer.WriteStartObject("scm");
        writer.WriteString("branch", $"origin/{request.Branch}");
        writer.WriteString("commit", request.Commit);
        writer.WriteEndObject();

        writer.WriteStartObject("parameters");
        writer.WriteString("TARGET", request.TargetId);
        writer.WriteString("BUILD_USER_ID", request.Actor);
        writer.WriteEndObject();

        writer.WriteEndObject();
    });

    // --------------------------------------------------------------- Generic

    /// <summary>
    /// NetBox biçimi — genel yol eşlemesinin sınandığı yer.
    ///
    /// <para>
    /// Genel sağlayıcıda gövdenin şekline <b>alıcı karar vermiyor</b>, ucun
    /// yapılandırması karar veriyor. Üreteç bu yüzden gerçek bir üçüncü taraf
    /// biçimi basıyor: kendi uydurduğu düz bir nesne bassaydı, yapılandırılabilir
    /// eşlemenin iç içe geçmiş bir gövdeyi okuyabildiği hiç sınanmazdı.
    /// </para>
    /// </summary>
    private static byte[] GenericBody(WebhookDeliveryRequest request) => Json(writer =>
    {
        writer.WriteString("event", "updated");
        writer.WriteString("timestamp", Iso(request.Timestamp));
        writer.WriteString("model", "device");
        writer.WriteString("username", request.Actor);
        writer.WriteString("request_id", request.DeliveryId);

        writer.WriteStartObject("data");
        writer.WriteNumber("id", 4471);
        writer.WriteString("name", request.TargetId);

        // İÇ İÇE GEÇMELER BİLEREK KORUNUYOR. Genel eşleme bu alanları
        // `$.data.site.name`, `$.data.device_type.manufacturer`,
        // `$.data.status.label` yollarıyla okuyor; üreteç onları düzleştirseydi
        // yol okuyucunun derinliğe inebildiği hiç sınanmazdı ve eşleme boş
        // özetle "eşlendi" derdi.
        writer.WriteStartObject("device_type");
        writer.WriteString("manufacturer", "MikroTik");
        writer.WriteString("model", "CRS326");
        writer.WriteEndObject();

        writer.WriteStartObject("site");
        writer.WriteString("name", "İstanbul-DC1");
        writer.WriteEndObject();

        writer.WriteStartObject("status");
        writer.WriteString("value", "active");
        writer.WriteString("label", request.Summary);
        writer.WriteEndObject();

        writer.WriteString("url", "https://netbox.bizigo.example/dcim/devices/4471/");
        writer.WriteEndObject();

        writer.WriteStartObject("snapshots");

        writer.WriteStartObject("prechange");
        writer.WriteString("status", "staged");
        writer.WriteEndObject();

        writer.WriteStartObject("postchange");
        writer.WriteString("status", "active");
        writer.WriteEndObject();

        writer.WriteEndObject();
    });

    // -------------------------------------------------------------- yardımcı

    /// <summary>
    /// <b>Girintisiz</b> ve deterministik. Girintili yazılsaydı gövde okunaklı
    /// olurdu ama sağlayıcılar girintisiz gönderiyor — ve imza <b>baytların</b>
    /// üzerinde, yani okunaklılık burada bir biçim kararı değil, imzanın
    /// kendisi.
    /// </summary>
    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string GitLabTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
}

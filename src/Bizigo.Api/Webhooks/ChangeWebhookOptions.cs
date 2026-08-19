using Bizigo.Contracts;

namespace Bizigo.Api.Webhooks;

/// <summary>Desteklenen sağlayıcılar (T24, K34).</summary>
public static class ChangeWebhookProviders
{
    public const string GitHub = "github";
    public const string Jenkins = "jenkins";
    public const string GitLab = "gitlab";

    /// <summary>Bilinmeyen sağlayıcı: eşleme JSON yol ifadeleriyle yapılandırılıyor.</summary>
    public const string Generic = "generic";

    public static readonly string[] All = [GitHub, Jenkins, GitLab, Generic];
}

/// <summary>
/// Webhook alıcısının yapılandırması (T24).
///
/// <para>
/// <b>Neden yapılandırmadan, tablodan değil:</b> connector yönetim ekranı ve onun
/// arkasındaki CRUD T25'in işi. Bu ticket'ın amacı tablonun <b>bugünden</b> gerçek
/// veriyle dolmaya başlaması; araya bir yönetim alt sistemi koymak o başlangıcı
/// geciktirirdi. <see cref="ChangeWebhookRegistry"/> arayüz olarak duruyor, T25
/// kontrol düzlemi destekli bir uygulamayla yerine geçebilir.
/// </para>
/// </summary>
public sealed class ChangeWebhookOptions
{
    public const string SectionName = "Changes:Webhooks";

    /// <summary>
    /// Gövde sınırı. İmza <b>ham baytlar</b> üzerinde hesaplandığı için gövdenin
    /// tamamı belleğe alınıyor; sınırsız bırakmak tek istekle belleği tüketmek
    /// demekti.
    /// </summary>
    public long MaxBodyBytes { get; set; } = 1 * 1024 * 1024;

    public IList<ChangeWebhookEndpoint> Endpoints { get; } = [];
}

/// <summary>
/// Tek bir webhook ucu.
///
/// <para>
/// <b>Bu tip bilerek <c>record</c> değil.</b> Kayıt tipinin üretilmiş
/// <c>ToString()</c>'i bütün özellikleri basar; bir <c>record</c> olsaydı
/// yapılandırmayı loglayan ya da bir hata mesajına iliştiren ilk satır gizli
/// anahtarı sızdırırdı. <see cref="ToString"/> aşağıda açıkça yalnızca kimliği
/// döndürüyor ve bir birim testi bunu sabitliyor.
/// </para>
/// </summary>
public sealed class ChangeWebhookEndpoint
{
    /// <summary>URL'de görünen kimlik: <c>POST /v1/changes/webhooks/{id}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary><see cref="ChangeWebhookProviders"/> değerlerinden biri.</summary>
    public string Provider { get; set; } = ChangeWebhookProviders.Generic;

    /// <summary>
    /// Bu ucun yazabileceği <b>tek</b> grup. Webhook'un kapsamı buradan geliyor —
    /// token'dan değil, çünkü webhook'un token'ı yok.
    /// </summary>
    public string OwnerGroup { get; set; } = string.Empty;

    /// <summary>
    /// Paylaşılan gizli anahtar. Yapılandırmadan okunur; ortam değişkeni ya da
    /// secret store'dan gelmesi beklenir. <b>Hiçbir log satırına, hata mesajına
    /// ya da API cevabına girmez.</b>
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// İmzanın taşındığı başlık. Boşsa sağlayıcının varsayılanı kullanılır
    /// (<see cref="WebhookSignature.DefaultHeader"/>).
    /// </summary>
    public string SignatureHeader { get; set; } = string.Empty;

    public ChangeTargetKind TargetKind { get; set; } = ChangeTargetKind.Service;

    /// <summary>Sağlayıcı eşlemesi bir tür üretemezse kullanılan değer.</summary>
    public string DefaultChangeKind { get; set; } = "deploy";

    public bool Enabled { get; set; } = true;

    /// <summary>Yalnızca <see cref="ChangeWebhookProviders.Generic"/> için.</summary>
    public GenericWebhookMapping Mapping { get; set; } = new();

    /// <summary>Gizli anahtarı asla basmaz — sınıf yorumundaki gerekçeye bakın.</summary>
    public override string ToString() => $"webhook:{Id}";
}

/// <summary>
/// Bilinmeyen sağlayıcı için JSON yol eşlemesi (T24 kapsamı).
///
/// <para>
/// Her alan bir yol ifadesi: <c>$.build.parameters.TARGET</c>,
/// <c>$.commits[0].id</c>. Sözdizimi <see cref="JsonPathReader"/>'da.
/// Boş bırakılan alan atlanır.
/// </para>
/// </summary>
public sealed class GenericWebhookMapping
{
    public string TargetId { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string ExternalRef { get; set; } = string.Empty;

    /// <summary>
    /// Teslimat kimliği yolu. Verilmezse gövdenin sha256'sı kullanılır — yani
    /// idempotans yapılandırma olmadan da çalışıyor, sadece "aynı gövde" düzeyinde.
    /// </summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary><c>details</c> haritasına düşecek ek alanlar: anahtar → yol.</summary>
    public IDictionary<string, string> Details { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Uç kimliğinden yapılandırmaya. T25 bunu kontrol düzlemine taşıyabilir.</summary>
public interface IChangeWebhookRegistry
{
    ChangeWebhookEndpoint? Find(string endpointId);
}

public sealed class ChangeWebhookRegistry : IChangeWebhookRegistry
{
    private readonly Dictionary<string, ChangeWebhookEndpoint> _byId;

    /// <exception cref="InvalidOperationException">
    /// Yapılandırma eksikse. <b>Açılışta patlamak bilinçli:</b> gizli anahtarı
    /// ya da <c>OwnerGroup</c>'u eksik bir uç, sessizce çalışan bir güvenlik
    /// açığıdır. Anahtarsız uç imzasız kabul eder; gruptan yoksun uç kapsam
    /// kapısını boş bir gruba yazarak fiilen atlar. İkisi de "çalışıyor" gibi
    /// görünür — F1'in en pahalı dersi tam olarak buydu.
    /// </exception>
    public ChangeWebhookRegistry(ChangeWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        foreach (var (endpoint, index) in options.Endpoints.Select((e, i) => (e, i)))
        {
            var name = string.IsNullOrWhiteSpace(endpoint.Id) ? $"#{index}" : endpoint.Id;

            if (string.IsNullOrWhiteSpace(endpoint.Id))
            {
                problems.Add($"{name}: 'Id' zorunlu.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.OwnerGroup))
            {
                problems.Add($"{name}: 'OwnerGroup' zorunlu — webhook'un kapsamı buradan geliyor.");
            }

            // Mesaj anahtarın kendisini DEĞİL, yokluğunu söylüyor.
            if (string.IsNullOrEmpty(endpoint.Secret))
            {
                problems.Add($"{name}: 'Secret' zorunlu — imzasız uç kabul edilmiyor.");
            }

            if (!ChangeWebhookProviders.All.Contains(endpoint.Provider, StringComparer.Ordinal))
            {
                problems.Add(
                    $"{name}: bilinmeyen sağlayıcı '{endpoint.Provider}'. " +
                    $"Geçerli: {string.Join(", ", ChangeWebhookProviders.All)}.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ChangeWebhookOptions.SectionName} yapılandırması geçersiz: {string.Join(" ", problems)}");
        }

        _byId = options.Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Pasif uç <see langword="null"/> dönüyor: çağıran onu "yok" gibi görüyor.
    /// "Var ama kapalı" ayrımını dışarı vermek, geçerli uç kimliklerini
    /// deneyerek keşfetmeye kapı açardı.
    /// </summary>
    public ChangeWebhookEndpoint? Find(string endpointId) =>
        !string.IsNullOrEmpty(endpointId)
            && _byId.TryGetValue(endpointId, out var endpoint)
            && endpoint.Enabled
                ? endpoint
                : null;
}

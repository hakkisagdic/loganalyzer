using System.Net;
using Bizigo.Contracts.Security;

namespace Bizigo.Rca.Models;

/// <summary>
/// <b>K6'yı geçmiş</b> bir model ucu. Var olması, kapının onayından geçmiş
/// olması demek.
///
/// <para>
/// <b>Yapıcısı <c>private</c> ve tek fabrikası <c>internal</c>:</b> bir
/// <see cref="ModelEndpoint"/> ancak <see cref="ModelBoundaryGate"/>'ten
/// çıkabiliyor. Sağlayıcı da bu tipi istiyor, dolayısıyla <b>kapıyı atlayan
/// bir çağrı derlenmiyor</b>.
/// </para>
///
/// <para>
/// Alternatifi sağlayıcıya <see cref="ModelEndpointOptions"/> vermek ve kapıyı
/// çağırmayı hatırlamaktı. O zaman K6 bir çağrı alışkanlığı olurdu ve
/// unutulduğu gün hiçbir şey kırılmazdı — T41'in kapısında aynı karar aynı
/// gerekçeyle verildi.
/// </para>
/// </summary>
public sealed class ModelEndpoint
{
    private ModelEndpoint(
        string name,
        Uri baseUri,
        string model,
        bool allowRawContentLevel,
        int timeoutSeconds,
        string? apiKeyEnvironmentVariable,
        IReadOnlyList<IPAddress> verifiedAddresses,
        string? overrideReason)
    {
        Name = name;
        BaseUri = baseUri;
        Model = model;
        AllowRawContentLevel = allowRawContentLevel;
        TimeoutSeconds = timeoutSeconds;
        ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
        VerifiedAddresses = verifiedAddresses;
        BoundaryOverrideReason = overrideReason;
    }

    public string Name { get; }

    public Uri BaseUri { get; }

    public string Model { get; }

    /// <summary>
    /// <c>raw</c> düzeyi bu kurumda açık mı. <b>Sağlayıcı türüne bağlı değil</b>
    /// — düzey ekseninin kararı kurumun.
    /// </summary>
    public bool AllowRawContentLevel { get; }

    public int TimeoutSeconds { get; }

    public string? ApiKeyEnvironmentVariable { get; }

    /// <summary>
    /// Doğrulama anında çözülen adresler. Muafiyet kullanıldıysa <b>boş</b> —
    /// ve boş olması "doğrulanmadı" demek, "adres yok" değil.
    /// </summary>
    public IReadOnlyList<IPAddress> VerifiedAddresses { get; }

    /// <summary>
    /// Adres doğrulaması atlandıysa gerekçesi, aksi hâlde <see langword="null"/>.
    /// Koşum kaydına yazılıyor: muafiyetin sessiz olanı, muafiyetin
    /// olmamasından tehlikeli.
    /// </summary>
    public string? BoundaryOverrideReason { get; }

    public bool BoundaryOverridden => BoundaryOverrideReason is not null;

    internal static ModelEndpoint Verified(
        ModelEndpointOptions options,
        Uri baseUri,
        IReadOnlyList<IPAddress> addresses,
        string? overrideReason) =>
        new(
            options.Name,
            baseUri,
            options.Model,
            options.AllowRawContentLevel,
            options.TimeoutSeconds,
            options.ApiKeyEnvironmentVariable,
            addresses,
            overrideReason);

    /// <summary>Kayda ve rapora giden tek satır — muafiyet gizlenmiyor.</summary>
    public IReadOnlyDictionary<string, object> AuditFields() =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model_endpoint"] = Name,
            ["model_name"] = Model,
            ["model_boundary"] = nameof(DataBoundary.Internal),
            ["model_boundary_overridden"] = BoundaryOverridden,
            ["model_boundary_override_reason"] = BoundaryOverrideReason ?? string.Empty,
            ["model_raw_level_allowed"] = AllowRawContentLevel,
        };
}

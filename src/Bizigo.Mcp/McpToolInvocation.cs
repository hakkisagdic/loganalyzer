using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Mcp;

/// <summary>
/// Bir araç çağrısının girdisi. SDK'nın <c>RequestContext</c>'ini sarıyor ki
/// araçlar protokol tiplerine değil <b>kendi sözleşmelerine</b> baksın.
///
/// <para>
/// <b>Kapsam argümanların yanında duruyor, ve bu M08'in kapısının kendisi.</b>
/// <see cref="Scope"/> zorunlu; onu dolduran <b>tek</b> yer
/// <c>BizigoMcpTool.InvokeAsync</c> ve o metot <c>sealed</c>. Yani kapsamsız bir
/// araç çağrısı <b>yazılamıyor</b> — <c>IScopedQuery</c>'nin kütüphane
/// tarafındaki kapısıyla aynı fikir, MCP yüzeyine taşınmış hâli.
/// </para>
///
/// <para>
/// <b>Neden <c>record struct</c> değil.</b> İlk hâli öyleydi ve bir deliği
/// vardı: her <c>struct</c>'ın parametresiz bir <c>default</c>'u var, yani
/// <c>new McpToolInvocation()</c> <b>derleniyor</b> ve <see cref="Scope"/>'u
/// <see langword="null"/> bırakıyordu. Kapsamı zorunlu kılmanın anlamı, onu
/// atlamanın <b>derlenmemesi</b>; sınıfa çevirmek o cümleyi doğru yapıyor.
/// </para>
/// </summary>
/// <param name="Arguments">
/// Çağrının argümanları. Aracın <c>inputSchema</c>'sı bunları tarif ediyor.
/// </param>
/// <param name="Scope">
/// Çağıranın veri kapsamı (K17). <c>RequestContext.User</c>'dan,
/// <see cref="IAccessScopeResolver"/> üzerinden — yani REST uçlarının geçtiği
/// <b>aynı</b> kapıdan geliyor.
///
/// <para>
/// Kimlik istemeyen araçlarda (<c>server.info</c>, simülatör yüzeyi)
/// <see cref="AccessScope.Denied"/>: <b>hiçbir satır göremeyen</b> kapsam.
/// Varsayılanın "kapalı" olması, kapsamı hiç kullanmayan bir aracın bir gün
/// kullanmaya başladığında sessizce her şeyi görmemesi demek.
/// </para>
/// </param>
public sealed record McpToolInvocation(
    IReadOnlyDictionary<string, JsonElement> Arguments,
    AccessScope Scope)
{
    /// <summary>Argüman var mı ve <c>null</c> değil mi.</summary>
    public bool Has(string name) =>
        Arguments.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null;

    /// <summary>
    /// Zorunlu argümanı okur. Yoksa <see cref="McpToolArgumentException"/> —
    /// <b>protokol istisnası değil</b>: çağıran onu yakalayıp
    /// <see cref="McpToolResult.Failure"/> ile araç hatasına çeviriyor.
    /// </summary>
    public T Required<T>(string name)
    {
        if (!Has(name))
        {
            throw new McpToolArgumentException(name, "zorunlu argüman verilmedi");
        }

        return Read<T>(name)!;
    }

    /// <summary>İsteğe bağlı argüman; yoksa <paramref name="fallback"/>.</summary>
    public T? Optional<T>(string name, T? fallback = default) =>
        Has(name) ? Read<T>(name) : fallback;

    private T? Read<T>(string name)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Arguments[name], McpJson.PayloadOptions);
        }
        catch (JsonException error)
        {
            throw new McpToolArgumentException(name, error.Message);
        }
    }
}

/// <summary>
/// Argüman okunamadı. <b>Araç hatasına</b> çevriliyor, protokol hatasına değil:
/// istemcinin yanlış argüman göndermesi bir iş hatası, bağlantı arızası değil.
/// </summary>
public sealed class McpToolArgumentException : Exception
{
    /// <summary>Sorunlu argümanın adı.</summary>
    public string ArgumentName { get; }

    /// <summary>Yeni bir örnek.</summary>
    public McpToolArgumentException(string argumentName, string reason)
        : base($"`{argumentName}`: {reason}") => ArgumentName = argumentName;

    /// <summary>Yeni bir örnek.</summary>
    public McpToolArgumentException()
        : this("?", "bilinmeyen") { }

    /// <summary>Yeni bir örnek.</summary>
    public McpToolArgumentException(string message)
        : base(message) => ArgumentName = "?";

    /// <summary>Yeni bir örnek.</summary>
    public McpToolArgumentException(string message, Exception innerException)
        : base(message, innerException) => ArgumentName = "?";
}

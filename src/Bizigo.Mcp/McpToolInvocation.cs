using System.Text.Json;

namespace Bizigo.Mcp;

/// <summary>
/// Bir araç çağrısının girdisi. SDK'nın <c>RequestContext</c>'ini sarıyor ki
/// araçlar protokol tiplerine değil <b>kendi sözleşmelerine</b> baksın.
/// </summary>
/// <param name="Arguments">
/// Çağrının argümanları. Aracın <c>inputSchema</c>'sı bunları tarif ediyor.
/// </param>
public readonly record struct McpToolInvocation(IReadOnlyDictionary<string, JsonElement> Arguments)
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

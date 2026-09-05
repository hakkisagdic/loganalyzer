using System.Text.Json;

namespace Bizigo.Mcp;

/// <summary>
/// Şema sabitlerini bir kez ayrıştırıp saklar.
///
/// <para>
/// Her <c>tools/list</c> çağrısında yeniden ayrıştırmak boşuna iş; ama asıl
/// sebep başka: <see cref="JsonDocument"/> bir kez ayrıştırılıp saklanmazsa
/// döndürülen <see cref="JsonElement"/> sahibi atıldığında geçersiz oluyor ve
/// hata <b>rastgele bir sonraki okumada</b> çıkıyor.
/// </para>
/// </summary>
public static class McpSchema
{
    /// <summary>
    /// JSON Schema metnini ayrıştırır ve ömrünü sürece bağlar.
    /// </summary>
    /// <exception cref="ArgumentException">Metin geçerli JSON değilse.</exception>
    public static JsonElement Parse(string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);

        try
        {
            // `JsonDocument` bilerek `Dispose` edilmiyor: `RootElement.Clone()`
            // kopyayı belgeden bağımsız hâle getiriyor, dolayısıyla belge
            // atılabilir. Klonlamadan döndürmek, kullanım anında patlayan bir
            // `ObjectDisposedException` demek olurdu.
            using var document = JsonDocument.Parse(schemaJson);

            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ArgumentException(
                $"Şema geçerli JSON değil: {error.Message}", nameof(schemaJson), error);
        }
    }
}

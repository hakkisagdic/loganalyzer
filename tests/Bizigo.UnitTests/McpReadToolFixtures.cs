using System.Text.Json;
using Bizigo.Mcp;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// M04 araç testlerinin ortak kurulum yardımcıları.
///
/// <para>
/// <b>Tek yerde olmalarının sebebi bir birleştirme.</b> M08
/// <c>McpToolInvocation</c>'ı <c>record struct</c>'tan <b>zorunlu</b>
/// <see cref="Bizigo.Contracts.AccessScope"/> taşıyan bir <c>sealed record</c>'a
/// çeviriyor. Çağrı nesnesi her testte elle kurulsaydı o merge onlarca yerde
/// çakışırdı; burada <b>bir</b> metotta çakışıyor.
/// </para>
/// </summary>
internal static class McpReadToolFixtures
{
    /// <summary>
    /// Argüman sözlüğünden bir araç çağrısı.
    ///
    /// <para>
    /// Değerler <b>dize</b> olarak alınıp JSON'a çevriliyor: aracın gerçekten
    /// gördüğü şey de tel üzerinden gelmiş bir <see cref="JsonElement"/>.
    /// Doğrudan tipli değer vermek, aracın <c>Optional&lt;T&gt;</c>
    /// dönüşümlerini atlayan bir test olurdu.
    /// </para>
    /// </summary>
    public static McpToolInvocation Invocation(params (string Name, string Value)[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        return new McpToolInvocation(map);
    }

    /// <summary>Sayısal ya da tipli argüman gerektiğinde.</summary>
    public static McpToolInvocation InvocationOf(params (string Name, object? Value)[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        return new McpToolInvocation(map);
    }

    /// <summary>
    /// <c>IScopedQuery</c>'yi <b>scoped</b> kaydeden bir kapsam fabrikası.
    ///
    /// <para>
    /// Üretimdeki kaydın ömrüyle aynı. Singleton kaydetmek, araçların çağrı
    /// başına kapsam açtığını ölçülmez kılardı: esir bağımlılık testte
    /// görünmez, üretimde patlar (gerekçe <c>ProductReadTool</c> belgesinde).
    /// </para>
    /// </summary>
    public static IServiceScopeFactory Scopes(IScopedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new ServiceCollection()
            .AddScoped(_ => query)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }
}

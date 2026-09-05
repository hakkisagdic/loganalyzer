using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp;

/// <summary>
/// Araçları <b>bulur</b> — elle yazılmış bir listeden okumaz.
///
/// <para>
/// <b>Bu deponun dördüncü kez ödediği ders.</b> <c>Produces&lt;T&gt;</c> kapısı
/// uçları elle yazılmış bir <c>Map*</c> listesinden topluyordu; T21/T22/T24
/// indiğinde <b>16 uç kapıya hiç görünmedi ve üç test de yeşil yandı.</b> Aynı
/// delik <c>Add*</c> kayıtlarında, sonra <c>EvidenceEndpoints</c>'te, sonra
/// T45'in kabul kapısında tekrarlandı. Elle tutulan liste er ya da geç bekçiyi
/// kör ediyor.
/// </para>
///
/// <para>
/// <b>Derlemeler de elle sayılmıyor:</b> kompozisyon kökünden başlanıp
/// <c>Bizigo.*</c> referansları geçişli olarak yükleniyor.
/// <c>AppDomain.GetAssemblies()</c> yeterli değil — bir derlemeye henüz hiç
/// dokunulmadıysa yüklü olmuyor ve keşif onu sessizce atlıyor, yani kapatmaya
/// çalıştığımız deliğin ta kendisi.
/// </para>
/// </summary>
public static class McpToolDiscovery
{
    /// <summary>
    /// <paramref name="assemblies"/> içindeki somut <see cref="BizigoMcpTool"/>
    /// türleri, ada göre sıralı.
    /// </summary>
    public static IReadOnlyList<Type> ToolTypes(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return [.. assemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(BizigoMcpTool).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Kompozisyon kökünden geçişli olarak yüklenen <c>Bizigo.*</c> derlemeleri.
    /// </summary>
    public static IReadOnlyList<Assembly> ProductAssemblies(Assembly root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([root]);

        while (pending.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name?.StartsWith("Bizigo.", StringComparison.Ordinal) == true
                    && !found.ContainsKey(reference.Name))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Keşfedilen türleri örnekleyip <paramref name="surface"/>'e ait olanları
    /// döndürür.
    ///
    /// <para>
    /// <b>Örnekleme atlamıyor, patlıyor.</b> Kurulamayan bir araç sessizce
    /// listeden düşseydi, bağımlılığı eksik bir araç <b>kapıya hiç
    /// görünmezdi</b> — kapatılan deliğin aynısı, başka kılıkta.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BizigoMcpTool> Instantiate(
        IEnumerable<Type> toolTypes,
        McpSurface surface,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(toolTypes);
        ArgumentNullException.ThrowIfNull(services);

        var tools = new List<BizigoMcpTool>();

        foreach (var type in toolTypes)
        {
            BizigoMcpTool tool;

            try
            {
                // `surface` fazladan argüman olarak veriliyor: yüzeyini
                // yapıcıdan alan araçlar (bkz. `ServerInfoTool`) böylece iki
                // yüzeyde de tek sınıfla duruyor. Yüzeyi sabit olan araçlar
                // argümanı hiç kullanmıyor ve aşağıdaki filtre onları eliyor.
                tool = (BizigoMcpTool)ActivatorUtilities.CreateInstance(services, type, surface);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"MCP aracı `{type.FullName}` kurulamadı: {error.Message}. "
                    + "Bağımlılığı DI'ya kaydedilmemiş olabilir. ATLANMIYOR — atlanan bir araç "
                    + "uyum kapısına hiç görünmez ve kapı yanlış sebeple yeşil yanar.",
                    error);
            }

            if (tool.Surface == surface)
            {
                tools.Add(tool);
            }
        }

        return tools;
    }
}

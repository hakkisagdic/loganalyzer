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

    // KÖKTEN REFERANS İZLEME KALDIRILDI — ve sebebi ölçüldü.
    //
    // Buradaki `ProductAssemblies(Assembly root)`, kompozisyon kökünden
    // `GetReferencedAssemblies()` ile geçişli kapanışı çıkarıyordu. Sessizce
    // eksik çalışıyordu: **derleyici, kodunda hiçbir tipine dokunulmayan bir
    // `ProjectReference`'ı meta veriden BUDUYOR.** Araç taşıyan bir derleme,
    // tam da araç sınıflarından başka bir şey içermediği için, kökün referans
    // listesinde HİÇ görünmüyordu.
    //
    // Kusurun şekli, kapatmaya çalıştığı deliğin aynısı: M04 beş araç yazdı,
    // referansları ekledi, çözüm 0 uyarıyla derlendi, uyum kapısı YEŞİL kaldı
    // — ve sunucu hâlâ tek araç ilan ediyordu (`strings bizigo.dll |
    // grep -c Bizigo.Mcp.Product` → 0). M03 aynı mekanizmayı kendi kolunda
    // bağımsız olarak ölçtü: `bizigo.dll`'de `Bizigo.Query` referansı
    // `.csproj`'da var, ikilide yok.
    //
    // Yerine geçen şey daha az zarif ama ölçülebilir: araç derlemeleri
    // ÇAĞIRAN tarafından, tip adıyla veriliyor. `typeof(X).Assembly` yazmak
    // referansı GERÇEK yapıyor, yani budama sorunu tanım gereği doğmuyor.
    // Geriye kalan tek risk "çağıran unuttu" ve onu bir bekçi tutuyor
    // (`McpToolAssemblyTests`): derlenmiş çıktıda araç taşıyan her derleme,
    // üretimin beyan ettiği listede olmak zorunda.

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
                // `surface` YALNIZCA onu isteyen yapıcıya veriliyor — ve bu
                // koşul ÖLÇÜLEREK eklendi.
                //
                // İlk hâli argümanı koşulsuz veriyordu. Ölçüm:
                // `ActivatorUtilities` fazladan argümanı olan bir çağrıyı
                // eşleştirmiyor ve "A suitable constructor ... could not be
                // located. ... Also ensure no extraneous arguments are
                // provided." diyerek düşüyor. Yani YÜZEYİNİ YAPICIDAN ALMAYAN
                // HİÇBİR ARAÇ KURULAMIYORDU — parametresiz bir yapıcı da,
                // yalnızca `IScopedQuery` isteyen bir M04 aracı da.
                //
                // Kusurun bedeli yalnızca "çalışmıyor" değildi: aşağıdaki
                // `catch` bunu "Bağımlılığı DI'ya kaydedilmemiş olabilir" diye
                // raporluyor, yani mesaj sebebi OLMAYAN bir yere işaret ediyor
                // ve arayan kişi DI kayıtlarında saatlerce dolaşıyor. Bu
                // deponun §7'de tarif ettiği sınıf: hata var ama söylediği şey
                // yanlış.
                //
                // Yüzeyini yapıcıdan alan araçlar (bkz. `ServerInfoTool`) iki
                // yüzeyde de tek sınıfla durmaya devam ediyor; yüzeyi sabit
                // olanlar argümanı hiç görmüyor ve aşağıdaki filtre onları
                // eliyor.
                tool = (BizigoMcpTool)(WantsSurface(type)
                    ? ActivatorUtilities.CreateInstance(services, type, surface)
                    : ActivatorUtilities.CreateInstance(services, type));
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

    /// <summary>
    /// Aracın <b>herhangi bir</b> genel yapıcısı yüzeyi istiyor mu.
    ///
    /// <para>
    /// Bütün yapıcılara bakılıyor, yalnızca birine değil: hangi yapıcının
    /// seçileceğine <c>ActivatorUtilities</c> karar veriyor ve karar DI'da
    /// kayıtlı servislere bağlı. Tek bir yapıcıya bakmak, kararı burada ikinci
    /// kez ve <b>farklı bilgiyle</b> vermek olurdu.
    /// </para>
    /// </summary>
    private static bool WantsSurface(Type type) =>
        type.GetConstructors()
            .Any(static constructor => constructor.GetParameters()
                .Any(static parameter => parameter.ParameterType == typeof(McpSurface)));
}

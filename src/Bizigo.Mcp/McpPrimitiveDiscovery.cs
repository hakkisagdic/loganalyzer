using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp;

/// <summary>
/// MCP <b>ilkellerini</b> (araç, kaynak) bulan ve kuran tek mekanizma.
///
/// <para>
/// <b>Neden jenerik, neden ikinci bir keşif yazılmadı.</b> M07'de kaynak kanalı
/// açılırken keşfin ikinci bir kopyası yazılabilirdi ve şekli neredeyse aynı
/// olurdu. Bu depoda ikinci gösterimin bedeli ölçülü: iki liste er ya da geç
/// ayrışıyor (§9), ve buradaki ayrışma <b>sessiz</b> olurdu — araç keşfi bir
/// hatayı yakalarken kaynak keşfi aynı hatayı atlardı.
/// </para>
///
/// <para>
/// <c>McpToolDiscovery</c> bu mekanizmanın araç tarafındaki yüzü olarak duruyor
/// ve <b>ölçülmüş gerekçeleri</b> orada: kompozisyon kökünden referans izlemenin
/// neden kaldırıldığı (derleyici dokunulmamış <c>ProjectReference</c>'ı buduyor),
/// <c>AppDomain</c> taramasının neden yetmediği, <c>surface</c> argümanının
/// neden koşullu verildiği. Aynı gerekçeler kaynak tarafında da geçerli ve
/// <b>ikinci kez yazılmadı</b>.
/// </para>
/// </summary>
public static class McpPrimitiveDiscovery
{
    /// <summary>
    /// <paramref name="assemblies"/> içindeki somut <typeparamref name="TPrimitive"/>
    /// türleri, ada göre sıralı.
    /// </summary>
    /// <typeparam name="TPrimitive">Aranan ilkel tabanı.</typeparam>
    /// <param name="assemblies">Taranacak derlemeler.</param>
    public static IReadOnlyList<Type> Types<TPrimitive>(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return [.. assemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(TPrimitive).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Keşfedilen türleri örnekleyip <paramref name="surface"/>'e ait olanları
    /// döndürür.
    ///
    /// <para>
    /// <b>Örnekleme atlamıyor, patlıyor.</b> Kurulamayan bir ilkel sessizce
    /// listeden düşseydi, bağımlılığı eksik bir araç ya da kaynak <b>kapıya hiç
    /// görünmezdi</b> — bu deponun dört kez ödediği deliğin aynısı.
    /// </para>
    ///
    /// <para>
    /// <paramref name="surfaceOf"/> bir işlev olarak dışarıdan geliyor çünkü
    /// yüzey iki ilkelde <b>ayrı</b> üyeler: <c>BizigoMcpTool.Surface</c> ile
    /// <c>BizigoMcpResource.Surface</c> ortak bir arayüzde değil. Ortak bir
    /// arayüz çıkarmak SDK'nın iki ayrı taban sınıfına ek bir tip daha
    /// bindirirdi; okumanın yeri burası olduğu için işlev burada duruyor.
    /// </para>
    /// </summary>
    /// <typeparam name="TPrimitive">Kurulacak ilkel tabanı.</typeparam>
    /// <param name="types">Keşfedilen türler.</param>
    /// <param name="surface">Hangi yüzey.</param>
    /// <param name="surfaceOf">İlkelin beyan ettiği yüzeyi okuyan işlev.</param>
    /// <param name="services">Bağımlılıkları çözecek sağlayıcı.</param>
    public static IReadOnlyList<TPrimitive> Instantiate<TPrimitive>(
        IEnumerable<Type> types,
        McpSurface surface,
        Func<TPrimitive, McpSurface> surfaceOf,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(surfaceOf);
        ArgumentNullException.ThrowIfNull(services);

        var found = new List<TPrimitive>();

        foreach (var type in types)
        {
            TPrimitive primitive;

            try
            {
                // `surface` YALNIZCA onu isteyen yapıcıya veriliyor — koşulun
                // gerekçesi ve ölçümü `McpToolDiscovery` içinde.
                primitive = (TPrimitive)(WantsSurface(type)
                    ? ActivatorUtilities.CreateInstance(services, type, surface)
                    : ActivatorUtilities.CreateInstance(services, type));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"MCP ilkeli `{type.FullName}` kurulamadı: {error.Message}. "
                    + "Bağımlılığı DI'ya kaydedilmemiş olabilir. ATLANMIYOR — atlanan bir ilkel "
                    + "uyum kapısına hiç görünmez ve kapı yanlış sebeple yeşil yanar.",
                    error);
            }

            if (surfaceOf(primitive) == surface)
            {
                found.Add(primitive);
            }
        }

        return found;
    }

    /// <summary>
    /// İlkelin <b>herhangi bir</b> genel yapıcısı yüzeyi istiyor mu. Bütün
    /// yapıcılara bakılıyor: hangisinin seçileceğine <c>ActivatorUtilities</c>
    /// karar veriyor ve karar DI'da kayıtlı servislere bağlı.
    /// </summary>
    private static bool WantsSurface(Type type) =>
        type.GetConstructors()
            .Any(static constructor => constructor.GetParameters()
                .Any(static parameter => parameter.ParameterType == typeof(McpSurface)));
}

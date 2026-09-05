using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>Bir ürün projesi ve derleyicinin ondan ürettiği <b>gerçek</b> ad.</summary>
/// <param name="Project">Proje dosyasının adı — <c>Bizigo.Cli</c>.</param>
/// <param name="AssemblyName">
/// <c>&lt;AssemblyName&gt;</c> ile ezilmişse o, değilse proje adı — <c>bizigo</c>.
/// </param>
internal sealed record ProductProject(string Project, string AssemblyName, string File)
{
    /// <summary>Proje adı ile derleme adı ayrışmış mı.</summary>
    internal bool Renamed => !string.Equals(Project, AssemblyName, StringComparison.Ordinal);
}

/// <param name="Unloadable">
/// Diskte projesi olan ama derlemesi yüklenemeyen ürünler. <b>Sessizce
/// atlanmıyorlar</b>: yüklenemeyen bir derleme, keşfin çalıştığı ama bir
/// katmanı hiç görmediği hâl.
/// </param>
internal sealed record ProductSurface(
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<string> Unloadable);

/// <summary>
/// <b>Ürün keşfi — tek kaynak</b> (T55).
///
/// <para>
/// Bu depoda üç kapı aynı yüklemi ayrı ayrı yazmıştı:
/// <c>ArchitectureTests</c> (ürün derlemeleri + kayıt uzantıları),
/// <c>ProducesContractTests</c> (uç dosyaları + kaydedilecek servisler),
/// <c>CompositionRootTests</c> (ürün projeleri + çağrı grafiği). Üçüncü kopya
/// eşiği geçtiğinin işaretiydi (§9: <i>ortak yüzey varsa genişlet,
/// kopyalama</i>).
/// </para>
///
/// <para>
/// <b>Ama tekrarlanan şey YÜKLEM, istenen şey kümenin farklı dilimleri.</b>
/// Üç kapı üç ayrı soru soruyor ve üçünü tek bir listeye indirmek kapsamı
/// sessizce değiştirirdi — hem daraltarak hem <b>genişleterek</b>. Ölçüldü:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <see cref="AllProducts"/> — 18 derleme. Ürünün <b>tamamı</b>, simülatör
///     ve CLI dâhil.
///   </item>
///   <item>
///     <see cref="CompositionClosure"/> — 16 derleme. Üretim host'unun
///     <b>gerçekten yüklediği</b> kapanış. <c>Bizigo.Simulators</c> ve
///     <c>bizigo</c> burada <b>yok ve olmamalı</b>: ikisi de üretim
///     kompozisyonunun parçası değil.
///   </item>
///   <item>
///     <see cref="CompositionRoot"/> — tek derleme. Uçlar yalnızca API'de
///     yaşıyor; bu kapsam <b>bilerek</b> dar.
///   </item>
/// </list>
///
/// <para>
/// Genişlemenin bedeli ölçülmüş bir şey, tahmin değil:
/// <c>ArchitectureTests</c> bulduğu her kayıt uzantısını <b>gerçek bir
/// <c>WebApplicationBuilder</c> üzerinde çağırıyor</b> ve tanımadığı imzada
/// bilerek fırlatıyor. Kapsamı <see cref="AllProducts"/>'a açmak, simülatörün
/// <c>Add*</c> uzantılarını üretim servis grafiğine çağırmak olurdu.
/// </para>
/// </summary>
internal static class ProductDiscovery
{
    /// <summary>
    /// <b>Ürün projelerinin yaşadığı yerler — kapsamın BEYANI.</b>
    ///
    /// <para>
    /// Keşif diskten gidiyor, yani buraya konmayan bir ürün projesi <b>hiçbir
    /// kapıya görünmez</b>. Bu bir varsayım olarak bırakılamazdı: bekçisi
    /// <c>ProductDiscoveryTests.Urun_projeleri_yalnizca_beyan_edilen_alanlarda</c>
    /// ve çözümün <c>.sln</c>'deki proje listesinden okuyor — yani beyan
    /// bozulduğu gün kırmızı yanıyor, bir sonraki kişinin dikkatine
    /// bırakılmıyor.
    /// </para>
    /// </summary>
    internal static readonly string[] ProductAreas = ["src", "sim"];

    /// <summary>
    /// <b>Kompozisyon kökü</b> — üretim host'unun derlemesi.
    ///
    /// <para>
    /// <c>typeof(global::Program).Assembly</c> ile aynı şey, ama adı var:
    /// üç kapının hangisinin bu dar kapsamı <b>bilerek</b> seçtiği çağrı
    /// yerinden okunabilsin diye.
    /// </para>
    /// </summary>
    internal static Assembly CompositionRoot => typeof(global::Program).Assembly;

    private static readonly Lazy<IReadOnlyList<ProductProject>> LazyProjects = new(ScanProjects);
    private static readonly Lazy<ProductSurface> LazyAll = new(LoadAll);
    private static readonly Lazy<IReadOnlyList<Assembly>> LazyClosure = new(WalkClosure);

    /// <summary>Diskteki her ürün projesi ve gerçek derleme adı.</summary>
    internal static IReadOnlyList<ProductProject> Projects => LazyProjects.Value;

    /// <summary>
    /// <b>Kapsam: bütün ürün projeleri.</b> Diskten gidiyor ve
    /// <c>&lt;AssemblyName&gt;</c>'i onurlandırıyor.
    ///
    /// <para>
    /// <b>Neden referans kapanışından değil:</b> kapanış yalnızca kökten
    /// <b>erişilebilen</b> şeyi görüyor, ve kökten erişilemeyen bir uzantı
    /// aranan şeyin ta kendisi. <c>AddBizigoScenarioPlugins</c> böyleydi.
    /// </para>
    /// </summary>
    internal static ProductSurface AllProducts => LazyAll.Value;

    /// <summary>
    /// <b>Kapsam: üretim host'unun derleme kapanışı.</b>
    /// <see cref="CompositionRoot"/>'tan başlayıp ürün referanslarını geçişli
    /// olarak yürüyor.
    ///
    /// <para>
    /// <b>Bir derlemenin ürün olup olmadığı ÖNEKTEN değil DİSKTEN
    /// soruluyor</b> ve bu bir düzeltme (T55). Eski hâli
    /// <c>reference.Name.StartsWith("Bizigo.")</c> diyordu; önek bir
    /// <b>konvansiyon</b> ve <c>Bizigo.Cli</c> onu
    /// <c>&lt;AssemblyName&gt;bizigo&lt;/AssemblyName&gt;</c> ile zaten
    /// bozuyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bugün kurbanı yok ve bu ölçüldü:</b> önek kusuru düzeltilse de küme
    /// değişmiyor (16 → 16), çünkü CLI'yi <b>referanslayan ürün projesi yok</b>
    /// — CLI ikinci bir kompozisyon kökü ve bu kapanışta olmaması <b>doğru</b>.
    /// Yani düzeltme gizli bir kusuru kapatıyor, görünen bir arızayı değil, ve
    /// gerekçesi de bu olmalı: bir gün yeniden adlandırılmış bir proje kapanışa
    /// girerse <b>sessizce</b> atlanırdı.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<Assembly> CompositionClosure => LazyClosure.Value;

    // ------------------------------------------------------------- yüklemler

    /// <summary>
    /// <c>Add*(this IServiceCollection …)</c> — servis kaydı uzantıları.
    ///
    /// <para>
    /// Üç kapının da aradığı şeklin <b>tek tanımı</b>. Statik sınıf =
    /// <c>sealed</c> + <c>abstract</c>; uzantı olmak
    /// <see cref="ExtensionAttribute"/> ile belli.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<MethodInfo> ServiceRegistrars(IEnumerable<Assembly> assemblies) =>
        Extensions(assemblies, "Add", static p => p == typeof(IServiceCollection));

    /// <summary><c>Map*(this IEndpointRouteBuilder …)</c> — uç kaydı uzantıları.</summary>
    internal static IReadOnlyList<MethodInfo> EndpointRegistrars(IEnumerable<Assembly> assemblies) =>
        Extensions(assemblies, "Map", static p => typeof(IEndpointRouteBuilder).IsAssignableFrom(p));

    /// <summary>
    /// İkisi birden — <c>CompositionRootTests</c>'in çağrı grafiği her iki
    /// aileyi de kök sayıyor.
    /// </summary>
    internal static IReadOnlyList<MethodInfo> AllRegistrars(IEnumerable<Assembly> assemblies)
    {
        var list = assemblies as IReadOnlyCollection<Assembly> ?? [.. assemblies];

        return [.. ServiceRegistrars(list).Concat(EndpointRegistrars(list))
            .OrderBy(static m => m.Name, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<MethodInfo> Extensions(
        IEnumerable<Assembly> assemblies,
        string prefix,
        Func<Type, bool> firstParameter) =>
        [.. assemblies
            .SelectMany(static a => a.GetTypes())
            // Statik sınıf = sealed + abstract.
            .Where(static t => t is { IsSealed: true, IsAbstract: true })
            .SelectMany(static t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(static m => m.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Where(m => m.GetParameters() is [{ } first, ..] && firstParameter(first.ParameterType))
            .OrderBy(static m => m.Name, StringComparer.Ordinal)];

    // --------------------------------------------------------------- keşif

    private static IReadOnlyList<ProductProject> ScanProjects()
    {
        var found = new List<ProductProject>();

        foreach (var area in ProductAreas)
        {
            var directory = Path.Combine(RepositoryLayout.Root, area);

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory
                .EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                var project = Path.GetFileNameWithoutExtension(file);

                var renamed = Regex.Match(
                    File.ReadAllText(file), @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");

                found.Add(new ProductProject(
                    project,
                    renamed.Success ? renamed.Groups[1].Value : project,
                    file));
            }
        }

        return found;
    }

    private static ProductSurface LoadAll()
    {
        var assemblies = new List<Assembly>();
        var unloadable = new List<string>();

        foreach (var project in Projects)
        {
            try
            {
                assemblies.Add(Assembly.Load(new AssemblyName(project.AssemblyName)));
            }
            catch (Exception error)
            {
                unloadable.Add($"{project.Project} (derleme adı `{project.AssemblyName}`): {error.GetType().Name}");
            }
        }

        return new ProductSurface(
            [.. assemblies.DistinctBy(static a => a.GetName().Name, StringComparer.Ordinal)
                .OrderBy(static a => a.GetName().Name, StringComparer.Ordinal)],
            unloadable);
    }

    /// <summary>
    /// <b>Bu derleme adı bir ürün mü?</b> Cevap <b>diskten</b> geliyor —
    /// <c>Bizigo.</c> önekinden değil.
    ///
    /// <para>
    /// Ayrı bir üye olmasının sebebi <b>ölçülebilirlik</b>: kapanışın içine
    /// gömülü bir süzgeç, önek hâline geri döndüğünde <b>hiçbir testi
    /// düşürmüyordu</b> — ölçtüm, üç kapı da yeşil kaldı. Bugün kurbanı
    /// olmayan bir kusurun bekçisi ancak süzgeci doğrudan sınayarak kurulur.
    /// </para>
    /// </summary>
    internal static bool IsProduct(string assemblyName) =>
        ProductAssemblyNames.Contains(assemblyName);

    /// <summary>Diskteki her ürün projesinin <b>gerçek</b> derleme adı.</summary>
    internal static IReadOnlySet<string> ProductAssemblyNames => LazyNames.Value;

    private static readonly Lazy<IReadOnlySet<string>> LazyNames = new(
        static () => Projects.Select(static p => p.AssemblyName).ToHashSet(StringComparer.Ordinal));

    private static IReadOnlyList<Assembly> WalkClosure()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([CompositionRoot]);

        while (pending.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name
                    && IsProduct(name)
                    && !found.ContainsKey(name))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return [.. found.Values.OrderBy(static a => a.GetName().Name, StringComparer.Ordinal)];
    }
}

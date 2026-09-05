using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T50 — var olmak ile bağlı olmak ayrı sorulardır.</b>
///
/// <para>
/// Bu depodaki bekçiler bir uzantının <b>var olduğunu</b> ve <b>doğru
/// olduğunu</b> sınıyor. Hiçbiri <b>üretime bağlı olduğunu</b> sınamıyordu.
/// <c>ArchitectureTests</c> keşfettiği her <c>Add*</c> uzantısının kapsam
/// doğrulamasından geçtiğini görüyor; <c>ProducesContractTests</c> keşfettiği
/// her <c>Map*</c> uzantısının uçlarının sözleşmesi olduğunu görüyor. İkisi de
/// uzantıyı <b>kendi</b> kurduğu kapta çağırıyor — yani ikisi de
/// <c>Program.cs</c>'in o uzantıyı hiç çağırmadığı bir dünyada yeşil yanar.
/// </para>
///
/// <para>
/// Aynı boşluk bir turda <b>iki kez bağımsız olarak</b> bulundu: T48 bunu
/// "aramadım" diye yazdı, T44 ise canlı örneğine çarptı —
/// <c>AddBizigoScenarioPlugins</c> T43'ten beri yazılı duruyor ve
/// <c>Program.cs</c>'te <b>hiç çağrılmıyor</b>. Görülmesi de tesadüftü: T44
/// ilgisiz bir referans ekleyip <c>Bizigo.ScenarioPlugin</c>'i kompozisyon
/// kökünün geçişli kapanışına sokunca keşif bir fazla buldu ve elle tutulan
/// beklenen küme kırmızı yandı. Referans eklenmeseydi kimse görmeyecekti.
/// </para>
///
/// <para>
/// <c>CLAUDE.md</c> §7'nin sınıfı: hata yok, sayaç yok, belirti yok. Bir şey
/// ölçülmediyse çalıştığı varsayılmaz.
/// </para>
/// </summary>
public sealed class CompositionRootTests
{
    /// <summary>
    /// <b>Kompozisyon kökleri.</b> Ürünün gerçekten çalıştırdığı iki giriş
    /// noktası.
    ///
    /// <para>
    /// <c>Bizigo.Simulators</c> bilerek <b>yok</b>: geliştirme aracı, ürünün
    /// kompozisyon kökü değil. Yalnızca simülatörün çağırdığı bir uzantı bu
    /// kapıda "bağlı değil" görünür — ve bu doğru cevap, çünkü soru
    /// <i>"ürüne bağlı mı"</i>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>İkinci ad bir bulgu.</b> CLI'nin derleme adı <c>Bizigo.Cli</c> değil
    /// <c>bizigo</c>: proje <c>&lt;AssemblyName&gt;</c> ile yeniden
    /// adlandırıyor. Yani <c>Bizigo.</c> önekine bakan her yansıma keşfi CLI
    /// derlemesini <b>hiç görmüyor</b> — bu kapının ilk hâli de görmedi,
    /// <c>ArchitectureTests.ProductAssemblies</c> de aynı öneke bakıyor.
    /// Derleme adına güvenmek bu deponun kendi <c>CLAUDE.md</c> §4 dersinin
    /// başka bir kılığı: <i>dizin adına güvenme</i>.
    /// </remarks>
    private static readonly string[] RootAssemblies = ["Bizigo.Api", "bizigo"];

    /// <summary>
    /// <b>Bağlanacak ama henüz bağlanmamış.</b> Her satır bir ticket atfı
    /// taşıyor — <c>ProducesContractTests.Pending</c> deseninin aynısı.
    ///
    /// <para>
    /// Bu liste <b>küçülmeli</b>. Bir satırın burada durması, o uzantının
    /// yazıldığı ama ürüne hiç bağlanmadığı anlamına geliyor: testleri yeşil,
    /// kapsam doğrulaması yeşil, ve üretimde <b>yok</b>.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> PendingWiring = new(StringComparer.Ordinal)
    {
        ["AddBizigoScenarioPlugins"] =
            "T43'te yazıldı, hiçbir kompozisyon kökünden çağrılmıyor: senaryo kataloğu " +
            "bugün üretim DI grafiğinde YOK. Bağlamak T46/T47 kolunun kararı (T50 ölçer, bağlamaz).",
    };

    /// <summary>
    /// <b>Hiçbir zaman kompozisyon kökünden çağrılmayacak.</b> Gerekçesiyle.
    ///
    /// <para>
    /// <c>CLAUDE.md</c> §8: <i>"bir gün kapanacak" ile "hiç kapanmayacak" aynı
    /// listede duramaz</i> — ikisi tek listedeyken "liste boşaldı mı" sorusunun
    /// cevabı asla evet olamıyor. Sayı <see cref="ExpectedNeverWiredCount"/> ile
    /// çivili: buraya bir satır eklemek <b>iki ayrı bilinçli hareket</b>
    /// gerektiriyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> NeverWired = new(StringComparer.Ordinal)
    {
    };

    private const int ExpectedNeverWiredCount = 0;

    // ---------------------------------------------------------------------
    // Keşif: ne var?
    // ---------------------------------------------------------------------

    /// <summary>
    /// Keşfedilen ürün derlemeleri ve <b>yüklenemeyenler</b>.
    ///
    /// <para>
    /// <b>Neden kompozisyon kökünün kapanışından değil:</b>
    /// <c>ArchitectureTests</c> derleme kapanışını <c>Bizigo.Api</c>'den
    /// başlatıyor ve bu, bu ticket'ın sorusu için yanlış yer olurdu — <b>kökten
    /// erişilemeyen</b> bir uzantı aranan şeyin ta kendisi.
    /// <c>AddBizigoScenarioPlugins</c> tam olarak böyleydi: Api'nin kapanışında
    /// olmadığı için o keşfe hiç görünmüyordu.
    /// </para>
    /// </summary>
    private sealed record ProductSurface(
        IReadOnlyList<Assembly> Assemblies,
        IReadOnlyList<string> Unloadable);

    private static readonly Lazy<ProductSurface> Product = new(DiscoverProduct);

    private static ProductSurface DiscoverProduct()
    {
        var assemblies = new List<Assembly>();
        var unloadable = new List<string>();

        foreach (var (project, assemblyName) in ProductProjects())
        {
            try
            {
                assemblies.Add(Assembly.Load(new AssemblyName(assemblyName)));
            }
            catch (Exception error)
            {
                unloadable.Add($"{project} (derleme adı `{assemblyName}`): {error.GetType().Name}");
            }
        }

        return new ProductSurface(
            [.. assemblies.DistinctBy(static a => a.GetName().Name, StringComparer.Ordinal)
                .OrderBy(static a => a.GetName().Name, StringComparer.Ordinal)],
            unloadable);
    }

    /// <summary>
    /// Ürün projeleri <b>diskten</b>: <c>src/</c> ve <c>sim/</c> altındaki her
    /// <c>.csproj</c>, ve her birinin <b>gerçek derleme adı</b>.
    ///
    /// <para>
    /// <b>Neden derleme referanslarından değil:</b> referans kapanışı
    /// <c>Bizigo.</c> önekine bakmak zorunda ve önek bir <b>konvansiyon</b> —
    /// CLI onu <c>&lt;AssemblyName&gt;bizigo&lt;/AssemblyName&gt;</c> ile
    /// bozuyor ve önekli keşiflerin hepsine görünmez oluyor. Proje dosyası
    /// konvansiyona değil, derleyicinin gerçekten ürettiği ada bakıyor.
    /// </para>
    ///
    /// <para>
    /// Yüklenemeyen bir proje sessizce atlanmıyor: sayılıyor ve
    /// <see cref="Kapi_kapsamini_beyan_ediyor"/> kırmızı yanıyor. Görülemeyen
    /// bir derleme, içindeki her uzantının "yok" sayılması demek.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Project, string AssemblyName)> ProductProjects()
    {
        foreach (var area in new[] { "src", "sim" })
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
                var text = File.ReadAllText(file);
                var renamed = System.Text.RegularExpressions.Regex.Match(
                    text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");

                yield return (
                    Path.GetFileNameWithoutExtension(file),
                    renamed.Success ? renamed.Groups[1].Value : Path.GetFileNameWithoutExtension(file));
            }
        }
    }

    private static IReadOnlyList<Assembly> ProductAssemblies() => Product.Value.Assemblies;

    /// <summary>
    /// Kayıt ve uç uzantıları — <c>Add*(this IServiceCollection)</c> ve
    /// <c>Map*(this IEndpointRouteBuilder)</c>.
    ///
    /// <para>
    /// <b>Bilinen borç:</b> aynı yüklem bu depoda üç yerde duruyor
    /// (<c>ArchitectureTests.Registrars</c>, <c>ProducesContractTests.Registrars</c>
    /// ve burası) ve §9 ikinci kopya yazmayı yasaklıyor. Ortak bir yüzeye
    /// taşımak <c>ArchitectureTests</c>'i düzenlemeyi gerektiriyordu; o dosya
    /// şu anda T44'ün altında <b>canlı ve kırmızı</b>, ve aynı satırı iki ajana
    /// yazdırmak §9'un ayrıca yasakladığı şey. Borç ödenmedi, <b>gizlenmedi</b>:
    /// gerekçesi burada ve raporda duruyor.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MethodInfo> Registrars() =>
        [.. ProductAssemblies()
            .SelectMany(static a => a.GetTypes())
            // Statik sınıf = sealed + abstract.
            .Where(static t => t is { IsSealed: true, IsAbstract: true })
            .SelectMany(static t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(static m => m.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(static m => m.GetParameters() is [{ } first, ..]
                && (first.ParameterType == typeof(IServiceCollection)
                    || typeof(IEndpointRouteBuilder).IsAssignableFrom(first.ParameterType)))
            .Where(static m => m.Name.StartsWith("Add", StringComparison.Ordinal)
                || m.Name.StartsWith("Map", StringComparison.Ordinal))
            .OrderBy(static m => m.Name, StringComparer.Ordinal)];

    // ---------------------------------------------------------------------
    // Bağ: ne çağrılıyor?
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Kapının kendi kör noktası — okuma yöntemi kapsamı belirliyor.</b>
    ///
    /// <para>
    /// İki yol vardı ve ikisi de bir şey kaçırıyor:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>Metin araması</b> (<c>Program.cs</c>'i okuyup <c>AddX()</c>
    /// aramak): bir uzantının <b>başka bir uzantının içinden</b> çağrılmasını
    /// göremez — ve bu deponun elle listesi tam olarak bu yüzden eksik kalmıştı
    /// (<c>AddBizigoDiscovery</c>, <c>AddBizigoIngest</c>'in içinden çağrılıyor).
    /// Ayrıca kaynak biçimine bağlı.</item>
    /// <item><b>Derlenmiş IL</b> (burada seçilen): koşullu çağrıyı, zinciri ve
    /// <c>if</c> içindeki kaydı görür, çünkü hepsi IL'de duruyor.</item>
    /// </list>
    ///
    /// <para>
    /// <b>IL'in göremedikleri — beyan:</b>
    /// </para>
    ///
    /// <list type="number">
    /// <item><b>Yansımayla ya da bir yapılandırma dosyasından</b> çağrılan kayıt.
    /// Böyle bir çağrı IL'de bir <c>call</c> olarak durmuyor.</item>
    /// <item><b>Ölü kod.</b> <c>if (false)</c> içindeki bir çağrı IL'de duruyor
    /// ve bu kapı onu "bağlı" sayıyor. Kapı <i>"ulaşılabilir mi"</i> sorusunu
    /// değil <i>"çağrı grafiğinde var mı"</i> sorusunu cevaplıyor.</item>
    /// <item><b>Kaynak üreteçlerinin</b> çalışma anında kurduğu bağlar.</item>
    /// </list>
    ///
    /// <para>
    /// Delege üzerinden kurulan bağlar <b>görülüyor</b>: <c>ldftn</c> /
    /// <c>ldvirtftn</c> operandları da toplanıyor, yoksa <c>Map*</c> içindeki
    /// her lambda handler kapanışın dışında kalırdı. Async giriş noktaları da
    /// izleniyor: iki kök de <c>await</c> ile bitiyor, yani gövdeleri
    /// derleyicinin ürettiği durum makinesinde — <c>AsyncStateMachineAttribute</c>
    /// takip edilmeseydi kapanış <b>giriş noktasının ilk satırında biterdi</b> ve
    /// kapı her şeyi "bağlı değil" sanardı.
    /// </para>
    /// </summary>
    private sealed record CallGraph(
        IReadOnlySet<MethodBase> Reached,
        int UnresolvedTokens,
        IReadOnlyList<string> MissingRoots);

    private static readonly Lazy<CallGraph> Wired = new(BuildCallGraph);

    private static CallGraph BuildCallGraph()
    {
        var missingRoots = new List<string>();
        var seeds = new List<MethodBase>();

        foreach (var name in RootAssemblies)
        {
            var assembly = ProductAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));

            if (assembly?.EntryPoint is { } entry)
            {
                seeds.Add(entry);
            }
            else
            {
                missingRoots.Add(name);
            }
        }

        var reached = new HashSet<MethodBase>();
        var unresolved = 0;
        var pending = new Queue<MethodBase>(seeds);

        while (pending.TryDequeue(out var method))
        {
            if (!reached.Add(method))
            {
                continue;
            }

            // Async ve iterator gövdeleri derleyicinin ürettiği durum
            // makinesinde duruyor ve oraya `call` ile gidilmiyor — çerçevenin
            // generic `Start<TStateMachine>` çağrısıyla giriliyor. Takip
            // edilmezse kapanış giriş noktasında biter.
            foreach (var machine in StateMachines(method))
            {
                foreach (var moveNext in machine.GetMethods(Everything))
                {
                    pending.Enqueue(moveNext);
                }
            }

            foreach (var callee in IlCallReader.Callees(method, ref unresolved))
            {
                if (IsProductMethod(callee))
                {
                    pending.Enqueue(callee);
                }
            }
        }

        return new CallGraph(reached, unresolved, missingRoots);
    }

    // IL okuma makinesi `IlCallReader`'a taşındı: M06'nın redaksiyon bekçisi
    // aynı çözücüye ihtiyaç duydu ve ikinci bir kopya, biri düzeltilip diğeri
    // düzeltilmediğinde İKİ BEKÇİYE FARKLI ŞEY GÖSTERİRDİ — ikisi de yeşil
    // kalarak (CLAUDE.md §9). Bu satırdaki davranış değişmedi.
    private const BindingFlags Everything = IlCallReader.Everything;

    private static IEnumerable<Type> StateMachines(MethodBase method)
    {
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { StateMachineType: { } asyncMachine })
        {
            yield return asyncMachine;
        }

        if (method.GetCustomAttribute<IteratorStateMachineAttribute>() is { StateMachineType: { } iterator })
        {
            yield return iterator;
        }
    }

    /// <summary>
    /// Kapanış yalnızca <b>ürün derlemelerinin içinde</b> yürüyor: çerçeveye
    /// dalmak hem gereksiz hem sonsuz. Küme derleme adı önekinden değil
    /// <see cref="ProductProjects"/>'ten geliyor — <c>bizigo</c> (CLI) önek
    /// kuralına uymuyor ve kapanış onun içinde yürüyemezse CLI'nin çağırdığı
    /// her uzantı "bağlı değil" görünürdü.
    /// </summary>
    private static bool IsProductMethod(MethodBase method) =>
        method.DeclaringType is { } type && ProductAssemblySet.Contains(type.Assembly);

    private static readonly Lazy<HashSet<Assembly>> ProductAssemblyLookup =
        new(static () => [.. ProductAssemblies()]);

    private static HashSet<Assembly> ProductAssemblySet => ProductAssemblyLookup.Value;

    // ---------------------------------------------------------------------
    // Kapılar
    // ---------------------------------------------------------------------

    private static string Name(MethodInfo registrar) =>
        $"{registrar.DeclaringType?.Name}.{registrar.Name}";

    private static IReadOnlyList<MethodInfo> Unwired() =>
        [.. Registrars().Where(registrar => !Wired.Value.Reached.Contains(registrar))];

    /// <summary>
    /// <b>T50'nin kapısı:</b> keşfedilen her kayıt/uç uzantısı ya kompozisyon
    /// kökünün çağrı grafiğinde, ya <see cref="PendingWiring"/>'de, ya
    /// <see cref="NeverWired"/>'da.
    /// </summary>
    [Fact]
    public void Kesfedilen_her_uzanti_kompozisyon_kokune_bagli()
    {
        var orphans = Unwired()
            .Select(Name)
            .Where(name => !PendingWiring.ContainsKey(Method(name)) && !NeverWired.ContainsKey(Method(name)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            orphans.Length == 0,
            "Bu uzantı(lar) hiçbir kompozisyon kökünden çağrılmıyor:\n  " +
            string.Join("\n  ", orphans) +
            "\n\nYazılmış, testleri yeşil, ve ÜRETİMDE YOK. Ya bir kökten çağırın, ya " +
            "`PendingWiring`'e hangi ticket'ın bağlayacağıyla yazın, ya da gerçekten hiç " +
            "bağlanmayacaksa `NeverWired`'a gerekçesiyle ekleyip " +
            "`ExpectedNeverWiredCount`'u da güncelleyin.");
    }

    private static string Method(string qualified) => qualified.Split('.')[^1];

    /// <summary>
    /// <b>Ölçüt: "kökten erişilebiliyor mu", "Program.cs'te birebir geçiyor mu"
    /// değil.</b>
    ///
    /// <para>
    /// <c>AddBizigoDiscovery</c> kompozisyon kökünde <b>geçmiyor</b>:
    /// <c>AddBizigoIngest</c>'in içinden çağrılıyor. Metin araması yapan bir
    /// kapı onu düşürürdü ve bu bir <b>yanlış pozitif</b> olurdu — bu depoda
    /// bedeli yazılı: insanlar yanlış pozitif veren bir bekçiyi susturmayı
    /// öğreniyor, ve susturulan bekçi olmayan bekçiden kötü.
    /// </para>
    ///
    /// <para>
    /// Bu test zincir takibini <b>çiviliyor</b>: biri bir gün IL kapanışını
    /// metin aramasıyla değiştirirse burası kırmızı yanıyor. Çiftin diğer
    /// yarısı — kapının her şeyi "bağlı" saymadığı —
    /// <see cref="Listeler_bayat_giris_tasimiyor"/> tarafından korunuyor:
    /// bugün <see cref="PendingWiring"/>'de duran uzantı gerçekten bağlanınca
    /// o test kırmızı yanıp satırın silinmesini istiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_zinciri_takip_ediyor()
    {
        var chained = Registrars()
            .SingleOrDefault(static m => m.Name == "AddBizigoDiscovery");

        Assert.True(
            chained is not null,
            "`AddBizigoDiscovery` bulunamadı — bu testin ölçüm tabanı o. Uzantı " +
            "yeniden adlandırıldıysa zincir emsali de güncellenmeli.");

        Assert.True(
            Wired.Value.Reached.Contains(chained!),
            "`AddBizigoDiscovery` kökten erişilebilir sayılmadı. Bu uzantı kompozisyon " +
            "kökünde BİREBİR geçmiyor; `AddBizigoIngest`'in içinden çağrılıyor. Kapı " +
            "zinciri takip etmiyorsa yanlış pozitif üretiyor demektir.");
    }

    /// <summary>
    /// Listeler bayatlayamaz: bağlanmış bir uzantı listede kalırsa liste
    /// küçülmeyi bırakır ve boşluk yine görünmez olur.
    /// </summary>
    [Fact]
    public void Listeler_bayat_giris_tasimiyor()
    {
        var known = Registrars().Select(static m => m.Name).ToHashSet(StringComparer.Ordinal);
        var unwired = Unwired().Select(static m => m.Name).ToHashSet(StringComparer.Ordinal);

        var vanished = PendingWiring.Keys.Concat(NeverWired.Keys)
            .Where(name => !known.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            vanished.Length == 0,
            "Listelerde artık var olmayan uzantı(lar): " + string.Join(", ", vanished));

        var connected = PendingWiring.Keys.Concat(NeverWired.Keys)
            .Where(name => !unwired.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            connected.Length == 0,
            "Artık kompozisyon kökünden çağrılan uzantı(lar) hâlâ listede: " +
            string.Join(", ", connected) + " — listeden silin.");
    }

    /// <summary>
    /// İki liste ayrık ve muafiyet sessizce büyüyemez — <c>CLAUDE.md</c> §8.
    /// </summary>
    [Fact]
    public void Muafiyet_listesi_sessizce_buyuyemez()
    {
        Assert.True(
            NeverWired.Count == ExpectedNeverWiredCount,
            $"Kalıcı muafiyet sayısı {ExpectedNeverWiredCount} olmalı, {NeverWired.Count} bulundu. " +
            "Muafiyet eklemek bu sabiti de değiştirmeyi gerektiriyor — kaçış kapısı sessizce " +
            "genişleyemesin diye (CLAUDE.md §8).");

        foreach (var (name, reason) in NeverWired.Concat(PendingWiring))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name} girişi gerekçesiz.");
        }

        Assert.Empty(PendingWiring.Keys.Intersect(NeverWired.Keys, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>Kapı kendi kapsamını beyan ediyor</b> — T48'in kurduğu desen.
    ///
    /// <para>
    /// Beyan etmeyen bir kapı, kapsamını iddia etmiş sayılıyor. Burada üç şey
    /// sayılıyor: kökler bulundu mu, çözülemeyen IL tokenı kaldı mı, ve keşif
    /// gerçekten bir şey buldu mu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapi_kapsamini_beyan_ediyor()
    {
        var graph = Wired.Value;

        Assert.True(
            Product.Value.Unloadable.Count == 0,
            "Bu ürün projelerinin derlemesi yüklenemedi:\n  " +
            string.Join("\n  ", Product.Value.Unloadable) +
            "\n\nGörülemeyen bir derleme, içindeki her uzantının 'yok' sayılması demek. " +
            "Birim test projesine referans ekleyin.");

        Assert.True(
            graph.MissingRoots.Count == 0,
            "Giriş noktası bulunamayan kompozisyon kökü: " + string.Join(", ", graph.MissingRoots) +
            " — kök bulunamazsa kapanış boş kalır ve kapı her şeyi 'bağlı değil' sanar.");

        Assert.True(
            graph.UnresolvedTokens == 0,
            $"{graph.UnresolvedTokens} IL metot tokenı çözülemedi. Çözülemeyen her token, " +
            "kapının göremediği bir çağrı demek: bağlı bir uzantı 'bağlı değil' görünebilir.");

        // Keşif ve kapanış gerçekten iş görüyor: ikisinden biri boş kalsaydı
        // bütün testler anlamsız yere geçerdi.
        Assert.NotEmpty(Registrars());
        Assert.True(
            graph.Reached.Count > 100,
            $"Çağrı grafiği yalnızca {graph.Reached.Count} metot buldu — kapanış erken bitmiş " +
            "olmalı (async durum makinesi takibi kırıldıysa bu olur).");
    }

    /// <summary>
    /// <b>Ölçümün kendisi:</b> bugün kaç uzantı var, kaçı bağlı.
    ///
    /// <para>
    /// Sayı sabitlenmiyor — altı ajan paralel uzantı ekliyor ve sabitlenen sayı
    /// güncellenmesi rutinleşen bir sayıya dönüşür (<c>CLAUDE.md</c> §6).
    /// Sabitlenen tek şey <b>oran değil kural</b>: bağlı olmayan her uzantı bir
    /// listede duruyor, ve o listeler yukarıdaki kapılarla korunuyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Baglilik_muhasebesi_tutuyor()
    {
        var all = Registrars();
        var unwired = Unwired();

        Assert.Equal(
            all.Count,
            all.Count(registrar => Wired.Value.Reached.Contains(registrar)) + unwired.Count);

        // Listelerin toplamı, bağlı olmayanların tamamını kapsıyor.
        Assert.Equal(
            unwired.Count,
            unwired.Count(registrar =>
                PendingWiring.ContainsKey(registrar.Name) || NeverWired.ContainsKey(registrar.Name)));
    }
}

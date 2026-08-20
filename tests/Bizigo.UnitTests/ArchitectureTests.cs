using System.Reflection;
using System.Runtime.CompilerServices;
using Bizigo.Alerting;
using Bizigo.Api.Connectors;
using Bizigo.Api.Webhooks;
using Bizigo.Authoring;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Ingest;
using Bizigo.Normalization;
using Bizigo.Parsing;
using Bizigo.Query;
using Bizigo.Replay;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetArchTest.Rules;

namespace Bizigo.UnitTests;

/// <summary>
/// Kapsam ayrımının (K17) derleme zamanı bekçisi.
///
/// Bu kurallar olmadan tek bir aceleci PR yeter: biri kolaylık olsun diye
/// <c>Bizigo.Api</c>'ye <c>ClickHouseConnection</c> açar, kapsam filtresi
/// atlanır ve kimse fark etmez. Testin amacı insanı yakalamak değil, o yolu
/// baştan kapatmak.
/// </summary>
public sealed class ArchitectureTests
{
    private const string ClickHouseDriverNamespace = "ClickHouse.Driver";

    private static readonly Assembly[] AssembliesThatMustNotTouchTheDriver =
    [
        typeof(AccessScope).Assembly,          // Bizigo.Contracts
        typeof(IScopedQuery).Assembly,         // Bizigo.Query
        typeof(ControlPlaneDbContext).Assembly,// Bizigo.ControlPlane
        typeof(RawObjectKey).Assembly,         // Bizigo.Storage.Raw
        typeof(ParserMarker).Assembly,         // Bizigo.Parsing
        typeof(NormalizationMarker).Assembly,  // Bizigo.Normalization
        typeof(IngestMarker).Assembly,         // Bizigo.Ingest

        // RCA motoru ClickHouse'u DOĞRUDAN sorgulamıyor, sağlayıcılara soruyor
        // (RCA özelliği §3) ve sağlayıcılar `IScopedQuery`den geçiyor. Kanıt
        // yolu K17'nin en cazip kaçış deliği: bir korelasyon sorgusunu "tek
        // seferlik" ham SQL olarak yazmak çok kolay, ve o sorgu kapsam
        // filtresini taşımazsa bir ekip başka bir ekibin verisini kanıt olarak
        // görür — üstelik rapor bunu doğru veri gibi sunar.
        typeof(EvidenceMarker).Assembly,       // Bizigo.Evidence
    ];

    [Fact]
    public void Yalnizca_Storage_ClickHouse_surucuye_referans_verebilir()
    {
        foreach (var assembly in AssembliesThatMustNotTouchTheDriver)
        {
            var offenders = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(ClickHouseDriverNamespace)
                .GetTypes()
                .Select(t => t.FullName)
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"{assembly.GetName().Name} '{ClickHouseDriverNamespace}' bağımlılığı taşıyor: " +
                string.Join(", ", offenders) +
                ". Ham ClickHouse erişimi yalnızca Bizigo.Storage.ClickHouse içinde olabilir " +
                "(K17 / F1 §10.2).");
        }
    }

    [Fact]
    public void Api_dogrudan_ClickHouse_okuyucularina_erisemez()
    {
        // API katmanı EventReader/ChangeEventReader'ı doğrudan kullanamaz;
        // IScopedQuery üzerinden geçmeli. Aksi halde denetim kaydı ve kapsam
        // daraltması atlanabilir.
        var apiAssembly = typeof(global::Program).Assembly;

        var offenders = Types.InAssembly(apiAssembly)
            .That()
            .HaveDependencyOnAny(
                typeof(EventReader).FullName!,
                typeof(ChangeEventReader).FullName!)
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "API katmanı okuyuculara doğrudan erişiyor: " + string.Join(", ", offenders) +
            ". IScopedQuery kullanılmalı.");
    }

    /// <summary>
    /// <b>Singleton bir servis, scoped bir servisi tutamaz.</b>
    ///
    /// <para>
    /// T26'nın <c>DeviceConfigRunner</c>'ı tam bunu yapıyordu: singleton olarak
    /// kaydedilmişti ve <c>IScopedQuery</c> alıyordu, o da
    /// <c>ControlPlaneDbContext</c> taşıyor. Sonucu tek bir EF bağlamının
    /// sürecin ömrü boyunca paylaşılması — EF bağlamı iş parçacığı güvenli
    /// değil ve değişiklik izleyicisi hiç boşalmıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Sessizce yaşadı.</b> Üretimde <c>ValidateScopes</c> kapalı olduğu için
    /// uygulama hatasız açılıyordu; kusuru ortaya çıkaran şey, T14'ün OpenAPI
    /// belge üretiminin <c>Main</c>'i gerçekten çalıştırması oldu — ve o da
    /// yalnızca birleştirme sırasında görüldü, T26 indikten sonra kimse tipleri
    /// yeniden üretmediği için. Bu test o tesadüfü kaldırıyor: kapsam
    /// doğrulaması artık birim testinde, her koşumda.
    /// </para>
    /// </summary>
    [Fact]
    public void Uretim_DI_grafi_kapsam_dogrulamasindan_geciyor()
    {
        var builder = ProductionServices(out var registrars);

        // Keşfin gerçekten iş gördüğü burada da sınanıyor: çağrılar bir yerde
        // yutulsaydı grafik boş kalır ve test anlamsız yere geçerdi.
        Assert.NotEmpty(registrars);
        Assert.True(builder.Services.Count > 50, "Servis grafiği beklenmedik biçimde küçük.");

        // `Build()` doğrulamayı kendisi koşuyor: Development ortamında
        // `ValidateScopes` ve `ValidateOnBuild` açık. Hiçbir bağlantı
        // kurulmuyor — yalnızca grafik inşa ediliyor.
        var exception = Record.Exception(() => builder.Build());

        Assert.True(
            exception is null,
            "Üretim servis grafiği kapsam doğrulamasından geçmiyor — büyük ihtimalle bir singleton "
            + "scoped bir servisi tutuyor (captive dependency):\n" + exception?.Message);
    }

    /// <summary>
    /// <b>Bekçinin kendisinin bekçisi.</b> Bugün bilinen kayıt uzantılarının
    /// hepsi bulunuyor mu?
    ///
    /// <para>
    /// Elle yazılmış olan artık <b>denetlenen küme</b> değil, <b>beklenen
    /// küme</b>: keşif bunlardan azını bulursa bir katman sessizce doğrulama
    /// dışında kalmış demektir, fazlasını bulursa yeni bir katman gelmiş ve
    /// bilinçli olarak buraya yazılması gerekiyor. Aradaki fark önemli —
    /// birincisi kapının neyi denetlediğini belirliyordu, bu ise kapının
    /// gördüğünü <i>doğruluyor</i>.
    /// </para>
    ///
    /// <para>
    /// Liste yazılırken <c>AddBizigoAuthentication</c> ve
    /// <c>AddBizigoDiscovery</c> elle tutulan sürümde <b>yoktu</b>: ilki
    /// <c>Program.cs</c>'te çağrılıyor ama doğrulamaya hiç girmiyordu, ikincisi
    /// <c>AddBizigoIngest</c>'in içinden çağrılıyor. Yani liste zaten iki
    /// eksikti ve bunu kimse görmüyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapsam_bekcisi_butun_kayit_uzantilarini_kendisi_buluyor()
    {
        ProductionServices(out var registrars);

        Assert.Equal(
            [
                "AddBizigoAlerting", "AddBizigoAuthentication", "AddBizigoAuthoring",
                "AddBizigoDataPlane", "AddBizigoDiscovery", "AddBizigoEvidence",
                "AddBizigoIngest", "AddBizigoParsing", "AddBizigoRawArchive",
                "AddBizigoReplay", "AddChangeConnectors", "AddChangeWebhooks",
                "AddControlPlane",

                // Bizim yazdığımız bir uzantı DEĞİL: OpenAPI paketinin XML
                // yorum desteği bunu `Bizigo.Api` derlemesinin İÇİNE üretiyor
                // (`Microsoft.AspNetCore.OpenApi.Generated`, tip adı bir içerik
                // özetini taşıyor). Dışlamak yerine kabul ediliyor, çünkü
                // `Program.cs` gerçekten `AddOpenApi()` çağırıyor — yani üretim
                // grafiğinin parçası ve doğrulanması bir kayıp değil kazanç.
                // Dışlama kuralı yazmak ise listeleri yeniden körleştirmenin
                // yoluydu; burada dışlanan hiçbir şey yok.
                "AddOpenApi",
            ],
            registrars.Select(static m => m.Name).ToArray());

    }

    /// <summary>
    /// <c>Bizigo.*</c> derlemelerindeki <b>bütün</b> <c>IServiceCollection</c>
    /// uzantıları — yansımayla.
    ///
    /// <para>
    /// Elle yazılmış bir liste yerine burayı kullanmanın tek sebebi var: bu
    /// depoda aynı yapısal delik <b>üç kez</b> ısırdı. <c>Produces&lt;T&gt;</c>
    /// kapısı uçları elle yazılmış bir <c>Map*</c> listesinden topluyordu ve 16
    /// uç kapıya hiç görünmeden üç test yeşil yanıyordu; bu bekçi <c>Add*</c>
    /// çağrılarını elle tutuyordu ve <c>AddBizigoEvidence</c> listede yokken
    /// kanıt sağlayıcıları tam da bu doğrulamanın yakalaması gereken hatayı
    /// taşıyordu. Elle tutulan liste er ya da geç bekçiyi kör ediyor.
    /// </para>
    ///
    /// <para>
    /// Derlemeler de elle sayılmıyor: <b>kompozisyon kökünden</b>
    /// (<c>Bizigo.Api</c>) başlanıp <c>Bizigo.*</c> referansları geçişli olarak
    /// yükleniyor. Test projesinin referans listesinden gitmek yanlış olurdu —
    /// o listede üretimde olmayan şeyler bulunabilir ve üretimde olan bir şey
    /// eksik kalabilir.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MethodInfo> Registrars() =>
        [.. ProductAssemblies()
            .SelectMany(static a => a.GetTypes())
            // Statik sınıf = sealed + abstract.
            .Where(static t => t is { IsSealed: true, IsAbstract: true })
            .SelectMany(static t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(static m => m.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(static m => m.Name.StartsWith("Add", StringComparison.Ordinal))
            .Where(static m => m.GetParameters() is [{ } first, ..]
                && first.ParameterType == typeof(IServiceCollection))
            .OrderBy(static m => m.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Kompozisyon kökünden geçişli olarak yüklenen <c>Bizigo.*</c> derlemeleri.
    ///
    /// <para><c>AppDomain.GetAssemblies()</c> yeterli değil: bir derleme henüz
    /// hiç dokunulmadıysa yüklü olmuyor ve keşif onu sessizce atlıyor —
    /// kapatmaya çalıştığımız delik tam olarak bu.</para>
    /// </summary>
    private static IReadOnlyList<Assembly> ProductAssemblies()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(global::Program).Assembly]);

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
    /// Keşfedilen her kayıt uzantısını çağırıp üretim grafiğini kuruyor.
    /// </summary>
    ///
    /// <para>
    /// <b>İmza çeşitliliği.</b> Uzantıların üç ayrı şekli var:
    /// yalnızca <c>IServiceCollection</c> (<c>AddBizigoReplay</c>),
    /// <c>+ IConfiguration</c> (çoğunluk), <c>+ string connectionString</c>
    /// (<c>AddControlPlane</c>) ve <c>+ ClickHouseOptions</c>
    /// (<c>AddBizigoDataPlane</c>). Argüman üretimi <b>tip başına</b> yapılıyor,
    /// <c>string</c> ise ayrıca <b>parametre adına</b> bakıyor: adı
    /// <c>connectionString</c> olmayan bir <c>string</c>'e bağlantı dizesi
    /// vermek, testin sessizce yanlış bir şeyi kurması olurdu.
    /// </para>
    ///
    /// <para>
    /// <b>Tanınmayan imza atlanmıyor, testi düşürüyor.</b> Atlamak bu bekçiyi
    /// yeniden kör ederdi — hem de en sinsi biçimde, çünkü keşif çalışıyor
    /// görünürken bir katman doğrulama dışında kalırdı. Yeni bir şekil geldiğinde
    /// doğru hareket burayı öğretmek.
    /// </para>
    ///
    /// <para>
    /// <b>Çağrı sırası ada göre</b>, <c>Program.cs</c>'teki sıra değil. Sıra
    /// yalnızca <c>TryAdd</c> semantiğinde hangi kaydın kazandığını değiştiriyor
    /// (<c>AddChangeConnectors</c> ↔ <c>AddChangeWebhooks</c>); ömür doğrulaması
    /// <b>her</b> tanımlayıcıya bakıyor, dolayısıyla ikisi de denetleniyor.
    /// Kompozisyon sırasının doğruluğu <c>Program.cs</c>'in işi ve orada
    /// gerekçesiyle yazılı.
    /// </para>
    private static WebApplicationBuilder ProductionServices(out IReadOnlyList<MethodInfo> registrars)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Uygulamanın gerçekten okuduğu yapılandırma yerine asgari bir küme:
        // amaç ayarları değil, servis grafiğinin ÖMÜRLERİNİ sınamak.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlPlane"] = "Host=localhost;Database=bizigo;Username=x;Password=y",
            ["ConnectionStrings:ClickHouse"] = "Host=localhost;Port=8123;Database=bizigo",
        });

        registrars = Registrars();

        foreach (var registrar in registrars)
        {
            var arguments = registrar.GetParameters()
                .Select(p => ArgumentFor(p, builder))
                .ToArray();

            registrar.Invoke(null, arguments);
        }

        return builder;
    }

    private static object ArgumentFor(ParameterInfo parameter, WebApplicationBuilder builder)
    {
        if (parameter.ParameterType == typeof(IServiceCollection))
        {
            return builder.Services;
        }

        if (parameter.ParameterType == typeof(IConfiguration))
        {
            return builder.Configuration;
        }

        if (parameter.ParameterType == typeof(ClickHouseOptions))
        {
            return new ClickHouseOptions
            {
                ConnectionString = builder.Configuration.GetConnectionString("ClickHouse")!,
            };
        }

        if (parameter.ParameterType == typeof(string) && parameter.Name == "connectionString")
        {
            return builder.Configuration.GetConnectionString("ControlPlane")!;
        }

        throw new NotSupportedException(
            $"Kayıt uzantısı '{parameter.Member.Name}' tanınmayan bir parametre taşıyor: "
            + $"{parameter.ParameterType.Name} {parameter.Name}. "
            + "Bu parametreyi `ArgumentFor` içinde tanıtın — ATLAMAYIN. Atlanan bir imza, "
            + "o katmanı kapsam doğrulamasının dışında bırakır ve bekçi çalışıyor görünürken "
            + "kör olur; bu deponun aynı hatayı üçüncü kez yapmaması için burası patlıyor.");
    }

    [Fact]
    public void Contracts_hicbir_altyapiya_bagimli_olmamali()
    {
        var offenders = Types.InAssembly(typeof(AccessScope).Assembly)
            .That()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Amazon", ClickHouseDriverNamespace)
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Bizigo.Contracts altyapı bağımlılığı taşıyor: " + string.Join(", ", offenders));
    }
}

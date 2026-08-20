using System.Reflection;
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

        builder.Services.AddControlPlane(builder.Configuration.GetConnectionString("ControlPlane")!);
        builder.Services.AddBizigoDataPlane(new ClickHouseOptions
        {
            ConnectionString = builder.Configuration.GetConnectionString("ClickHouse")!,
        });
        builder.Services.AddBizigoParsing(builder.Configuration);
        builder.Services.AddBizigoIngest(builder.Configuration);
        builder.Services.AddBizigoRawArchive(builder.Configuration);
        builder.Services.AddBizigoReplay();
        builder.Services.AddBizigoAuthoring();
        builder.Services.AddChangeWebhooks(builder.Configuration);
        builder.Services.AddChangeConnectors(builder.Configuration);
        builder.Services.AddBizigoAlerting(builder.Configuration);

        // T34. Bu satır **Program.cs ile elle eşleşiyor** ve listeden düşen bir
        // `Add*` çağrısı bekçiyi o katmana sessizce kör bırakıyor — bu depoda
        // aynı yapısal delik `Produces<T>` kapısında zaten yaşandı: uçlar elle
        // yazılmış bir listeden toplanıyordu ve 16 uç kapıya hiç görünmeden üç
        // test yeşil yanıyordu. Kanıt katmanı eklendiğinde liste güncellenmezse
        // aynı şey burada olurdu; nitekim kanıt sağlayıcıları ilk yazımda
        // singleton'dı ve tam da bu doğrulamanın yakalaması gereken hatayı
        // taşıyorlardı.
        builder.Services.AddBizigoEvidence();

        // `Build()` doğrulamayı kendisi koşuyor: Development ortamında
        // `ValidateScopes` ve `ValidateOnBuild` açık. Hiçbir bağlantı
        // kurulmuyor — yalnızca grafik inşa ediliyor.
        var exception = Record.Exception(() => builder.Build());

        Assert.True(
            exception is null,
            "Üretim servis grafiği kapsam doğrulamasından geçmiyor — büyük ihtimalle bir singleton "
            + "scoped bir servisi tutuyor (captive dependency):\n" + exception?.Message);
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

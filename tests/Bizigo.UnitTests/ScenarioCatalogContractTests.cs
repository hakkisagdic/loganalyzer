using Bizigo.Evidence;
using Bizigo.Query;
using Bizigo.ScenarioPlugin;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// Sevk edilen senaryo dosyalarının kapısı (T43).
///
/// <para>
/// <b>Muafiyet iki ayrı bilinçli hareket gerektiriyor</b> ve iki hareket iki
/// ayrı dosyada duruyor: gerekçe senaryo dosyasında (<c>constraints_waived</c>),
/// sayı burada (<see cref="ExpectedWaivedCount"/>). Tek başına birincisi bir
/// kaçış kapısı — anahtarı yazmak yeterdi; tek başına ikincisi gerekçesiz bir
/// sayı. Kaçışı pahalı yapan şey görünürlük değil, <b>ikinci hareket</b>.
/// </para>
/// </summary>
public sealed class ScenarioCatalogContractTests
{
    /// <summary>
    /// Muafiyeti kabul edilmiş adımlar: <c>&lt;senaryo&gt;/&lt;adım&gt;</c>.
    ///
    /// <para>
    /// <b>Gerekçeler burada DEĞİL, senaryo dosyasında.</b> Gerekçeyi de buraya
    /// kopyalamak iki metin üretirdi ve ikisi sessizce ayrışırdı — bu deponun
    /// belge/kod ayrışması diye adını koyduğu sınıf. Bu listenin işi gerekçeyi
    /// anlatmak değil, muafiyetin <b>sayısını</b> bir bilinçli harekete
    /// bağlamak.
    /// </para>
    ///
    /// <para>
    /// Sayım <b>adım</b> düzeyinde, senaryo düzeyinde değil. Senaryo düzeyinde
    /// sayılsaydı, üç adımının ikisi muaf olan RCA senaryosu "muaf senaryo"
    /// diye görünür ve <c>bind-evidence</c>'ın gerçekten kapattığı kapı
    /// sayımdan kaybolurdu — yani sayaç, ölçmek istediği şeyin tersini
    /// gösterirdi.
    /// </para>
    /// </summary>
    private static readonly IReadOnlySet<string> Waived = new HashSet<string>(StringComparer.Ordinal)
    {
        "builtin.rca.network/rank-hypotheses",
    };

    /// <summary>
    /// <see cref="Waived"/>'ın büyümesi <b>ayrıca</b> bir hareket. İkisi tek
    /// olsaydı listeye satır eklemek muafiyet eklemeye yeterdi.
    ///
    /// <para>
    /// <b>2 → 1 (T44).</b> <c>write-actions</c>'ın muafiyeti düştü: aksiyon
    /// artık kendi kanıt atfını taşıyor ve <c>evidence_ids_must_exist</c> onu
    /// doğruluyor. Gerekçe senaryo dosyasında; buradaki tek iş, düşüşün de
    /// <b>iki bilinçli hareket</b> olması — ekleme kadar silme de.
    /// </para>
    /// </summary>
    private const int ExpectedWaivedCount = 1;

    /// <summary>
    /// Kayıtlı sağlayıcı kümesi <b>DI'den keşfediliyor</b>, elle yazılmıyor.
    ///
    /// <para>
    /// Elle yazılsaydı kusur ölçülmüş bir kusurun aynısı olurdu:
    /// <c>Produces&lt;T&gt;</c> kapısı uçları elle yazılmış bir listeden
    /// topluyordu, üç uç dosyası listede olmadığı için 16 uç kapıya hiç
    /// görünmedi ve üç test de yeşildi.
    /// </para>
    /// </summary>
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();

        // Sağlayıcıların tek bağımlılığı kapsam kapısı; senaryo yüklemesi hiç
        // sorgu çalıştırmıyor, yalnızca `Id` okuyor.
        services.AddScoped<IScopedQuery, RecordingScopedQuery>();
        services.AddBizigoEvidence();
        services.AddBizigoScenarioPlugins();

        return services.BuildServiceProvider();
    }

    private static ScenarioCatalogReport LoadCatalog(IScenarioProviderRegistry registry) =>
        ScenarioCatalog.Load(RepositoryLayout.ScenarioDirectory, registry);

    [Fact]
    public void Katalog_hatasiz_yukleniyor()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IScenarioProviderRegistry>();

        var report = LoadCatalog(registry);

        Assert.True(report.Ok, report.Describe());

        // Boş bir dizin, aşağıdaki muafiyet bekçisini sessizce yeşil bırakırdı:
        // boş küme, boş beklenen kümeyle eşleşir. Ölçülen kusurun aynısı.
        Assert.NotEmpty(report.Scenarios);
    }

    /// <summary>
    /// <b>Muaf adım kümesi sabit.</b> Bir senaryoya gerekçeli muafiyet eklemek
    /// burayı kırmızı yakıyor; gerekçeyi silmek de.
    /// </summary>
    [Fact]
    public void Muaf_adimlar_beklenen_kumeyle_ortusuyor()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IScenarioProviderRegistry>();

        var actual = LoadCatalog(registry).Scenarios
            .SelectMany(s => s.WaivedStepKeys)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            actual.SetEquals(Waived),
            "Muaf adım kümesi değişti.\n" +
            "  Katalogda olup listede olmayan: " + string.Join(", ", actual.Except(Waived)) + "\n" +
            "  Listede olup katalogda olmayan: " + string.Join(", ", Waived.Except(actual)) + "\n" +
            $"Değişiklik bilinçliyse `{nameof(Waived)}` ve `{nameof(ExpectedWaivedCount)}`'u birlikte güncelleyin.");
    }

    /// <summary>
    /// İkinci hareket. <see cref="Waived"/>'a satır eklemek tek başına
    /// yetmiyor.
    /// </summary>
    [Fact]
    public void Muafiyet_sayisi_sabitle_ortusuyor()
    {
        Assert.True(
            Waived.Count == ExpectedWaivedCount,
            $"Muafiyet listesi {ExpectedWaivedCount} yerine {Waived.Count} satır taşıyor. " +
            $"Değişiklik bilinçliyse `{nameof(ExpectedWaivedCount)}`'u da güncelleyin.");
    }

    /// <summary>
    /// Muaf OLMAYAN adımların kısıtı gerçekten dolu. Bu test olmadan
    /// <see cref="Muaf_adimlar_beklenen_kumeyle_ortusuyor"/> muafiyeti sayardı
    /// ama <b>kapının kapandığını</b> hiç sınamazdı.
    /// </summary>
    [Fact]
    public void Muaf_olmayan_her_adim_kisit_tasiyor()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IScenarioProviderRegistry>();

        var steps = LoadCatalog(registry).Scenarios
            .SelectMany(s => s.Steps.Select(step => (Scenario: s, Step: step)))
            .Where(pair => !pair.Step.Output.IsWaived)
            .ToList();

        Assert.NotEmpty(steps);
        Assert.All(steps, pair =>
        {
            Assert.NotEmpty(pair.Step.Output.Constraints);
            Assert.Null(pair.Step.Output.ConstraintsWaived);
        });
    }

    /// <summary>
    /// Muaf her adımın gerekçesi <b>dolu</b>. Yükleyici zaten reddediyor; bu
    /// test o reddin katalog üzerinde de geçerli olduğunu gösteriyor — yani
    /// muafiyet listesindeki her satırın arkasında yazılı bir cümle var.
    /// </summary>
    [Fact]
    public void Muaf_her_adimin_gerekcesi_dolu()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IScenarioProviderRegistry>();

        var waived = LoadCatalog(registry).Scenarios
            .SelectMany(s => s.Steps)
            .Where(step => step.Output.IsWaived)
            .ToList();

        // `Assert.Single` yerine sabitle karşılaştırma: sayının BİR olması
        // bugünkü durum, sabite eşit olması ise kuralın kendisi. Birincisi
        // yazılsaydı sabit sessizce boşta kalırdı ve "iki ayrı hareket"
        // güvencesi bu testten düşerdi.
        Assert.True(
            waived.Count == ExpectedWaivedCount,
            $"Katalogda {waived.Count} muaf adım var, beklenen {ExpectedWaivedCount}. " +
            $"Değişiklik bilinçliyse `{nameof(Waived)}` ve `{nameof(ExpectedWaivedCount)}`'u birlikte güncelleyin.");

        Assert.All(waived, step => Assert.False(string.IsNullOrWhiteSpace(step.Output.ConstraintsWaived)));
    }

    /// <summary>
    /// Sağlayıcı kümesi DI kayıtlarından geliyor: <c>AddBizigoEvidence</c>'a
    /// eklenen bir sağlayıcı senaryolara <b>kendiliğinden</b> açılıyor, ve
    /// kaldırılan bir sağlayıcıya atıf yapan senaryo yüklenemez oluyor.
    /// </summary>
    [Fact]
    public void Saglayici_kumesi_DI_kayitlarindan_kesfediliyor()
    {
        using var container = BuildContainer();
        using var scope = container.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IScenarioProviderRegistry>();
        var providers = scope.ServiceProvider.GetServices<IEvidenceProvider>().Select(p => p.Id).ToList();

        Assert.NotEmpty(providers);
        Assert.Equal(providers.Order(StringComparer.Ordinal), registry.ProviderIds.Order(StringComparer.Ordinal));

        // Kataloğun atıf yaptığı her sağlayıcı gerçekten kayıtlı.
        foreach (var scenario in LoadCatalog(registry).Scenarios)
        {
            Assert.All(scenario.Evidence.Providers, id => Assert.True(registry.IsRegistered(id)));
        }
    }

    /// <summary>
    /// Aynı kimliğin iki dosyada olması reddediliyor: biri sessizce kazanır ve
    /// hangisinin koştuğu dosya adına bağlı kalırdı.
    /// </summary>
    [Fact]
    public void Ayni_kimlik_iki_dosyada_duramaz()
    {
        var registry = new ScenarioProviderRegistry(["logs.volume"]);
        var directory = Directory.CreateTempSubdirectory("bizigo-scenarios-");

        try
        {
            const string Yaml = """
                apiVersion: bizigo.dev/v1
                kind: Scenario
                metadata: { id: test.duplicate, version: 1.0.0, owner: platform-team }
                spec:
                  trigger: { on: [manual] }
                  evidence: { providers: [logs.volume] }
                  steps:
                    - id: only-step
                      task: "Tek iş."
                      input: evidence.items
                      output: { schema: some_list, constraints: [evidence_ids_must_exist] }
                  publish: { requires_review: false }
                """;

            File.WriteAllText(Path.Combine(directory.FullName, "a.yaml"), Yaml);
            File.WriteAllText(Path.Combine(directory.FullName, "b.yaml"), Yaml);

            var report = ScenarioCatalog.Load(directory.FullName, registry);

            Assert.False(report.Ok);
            Assert.Contains("zaten", report.Describe(), StringComparison.Ordinal);
            Assert.Single(report.Scenarios);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

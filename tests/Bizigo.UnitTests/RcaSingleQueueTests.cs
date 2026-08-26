using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Dört kaynak tek yoldan giriyor</b> — ve ayrı bir yol açan değişiklik
/// kırmızı yanıyor (T45, RCA §5).
///
/// <para>
/// §5'in açılış cümlesi: "Ayrı yollar olsaydı kota ve döngü koruması beş kez
/// yazılacaktı." Bu dosya o cümleyi bir bekçiye çeviriyor. Davranış testi tek
/// başına yetmez: dördü de bugün kapıdan geçiyor diye <b>yarın</b> beşinci bir
/// yol açılmayacağı anlamına gelmiyor, ve o yol açıldığında hiçbir davranış
/// testi düşmez — yeni yolun kendi testleri yeşil yanar.
/// </para>
/// </summary>
public sealed class RcaSingleQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>rca_runs</c> satırı yazan tek yer <see cref="RcaAdmission"/> olmalı.
    ///
    /// <para>
    /// Kaynak metni üzerinde arıyoruz, tip bağımlılığı üzerinde değil: bir tipin
    /// <c>ControlPlaneDbContext</c>'e bağımlı olması meşru (herkes bağımlı),
    /// ama <c>RcaRuns</c> koleksiyonuna <b>satır eklemek</b> kabul kararı vermek
    /// demek. Mimari test bu ayrımı göremiyor; metin görebiliyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kosum_satiri_yazan_tek_yer_kabul_kapisi()
    {
        var root = RepositoryLayout.Root;
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (Path.GetFileName(file) == "RcaAdmission.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);

            if (text.Contains("RcaRuns.Add", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Kabul kapısının dışında RCA koşumu yaratan yer var — ikinci bir kuyruk yolu demek: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Dördü de aynı tipi üretiyor ve o tipin tek tüketicisi kapı.
    ///
    /// <para>
    /// Fabrikaların dördü de <see cref="RcaTriggerRequest"/> döndürüyor; biri
    /// kendi tipini üretmeye başlarsa bu test derlenmez — ve derlenmemesi
    /// istenen şey, çünkü o değişiklik "dört yol tek kuyrukta" ilkesini
    /// sessizce değil <b>gürültüyle</b> bozmalı.
    /// </para>
    /// </summary>
    [Fact]
    public void Dort_kaynak_da_ayni_talep_tipini_uretiyor()
    {
        RcaTriggerRequest[] requests =
        [
            RcaTriggerSources.FromAlert(new AlertTriggerEntity
            {
                RuleId = Guid.NewGuid(),
                OwnerGroup = "network/core",
                WindowFrom = Now.AddMinutes(-15),
                WindowTo = Now,
                Summary = "sınama",
            }),
            RcaTriggerSources.FromManual("analyst", ["network/core"], Now.AddHours(-1), Now),
            RcaTriggerSources.FromExternal("svc", "anahtar", ["network/core"], Now.AddHours(-1), Now),
            RcaTriggerSources.FromSchedule("gunluk", ["network/core"], Now.AddHours(-1), Now),
        ];

        // Dördü de kapalı kümenin ayrı bir değerini taşıyor — hiçbiri
        // "diğerlerinden biri gibi" davranmıyor.
        Assert.Equal(
            [RcaTriggerSource.Alert, RcaTriggerSource.Manual, RcaTriggerSource.External, RcaTriggerSource.Schedule],
            requests.Select(r => r.Source));

        // Ve hepsinin anahtarı aynı iki fonksiyondan doğuyor.
        Assert.All(requests, r =>
        {
            Assert.NotEmpty(RcaTriggerKey.Lineage(r.Source, r.Identity, r.OwnerGroup));
            Assert.NotEmpty(RcaTriggerKey.Debounce(r.Source, r.Identity, r.OwnerGroup, Now));
        });
    }

    /// <summary>
    /// Dördü de <b>gerçekten</b> kapıdan geçiyor ve dördünün de kaydı düşüyor.
    /// Yapısal bekçinin davranış tarafındaki karşılığı.
    /// </summary>
    [Fact]
    public async Task Dort_kaynak_da_kapidan_gecip_kayit_birakiyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        var gate = new RcaAdmission(
            factory,
            new AlwaysAllowQuotaGate(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RcaAdmission>.Instance,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));

        await gate.AdmitAsync(
            RcaTriggerSources.FromAlert(new AlertTriggerEntity
            {
                RuleId = Guid.NewGuid(),
                OwnerGroup = "network/core",
                WindowFrom = Now.AddMinutes(-15),
                WindowTo = Now,
                Summary = "sınama",
            }), Token);

        await gate.AdmitAsync(RcaTriggerSources.FromManual("analyst", ["network/core"], Now.AddHours(-1), Now), Token);
        await gate.AdmitAsync(RcaTriggerSources.FromExternal("svc", "k1", ["network/core"], Now.AddHours(-1), Now), Token);
        await gate.AdmitAsync(RcaTriggerSources.FromSchedule("gunluk", ["network/core"], Now.AddHours(-1), Now), Token);

        await using var db = factory.CreateDbContext();
        var runs = db.RcaRuns.ToList();

        Assert.Equal(4, runs.Count);
        Assert.All(runs, run => Assert.True(run.Accepted));

        // Dört ayrı kaynak, dört ayrı anahtar: hiçbiri diğerini debounce etmedi.
        Assert.Equal(4, runs.Select(r => r.DebounceKey).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Kapalı kümenin boyutu sabitleniyor.
    ///
    /// <para>
    /// Beşinci bir <b>kaynak</b> eklemek çekirdek kararı: §5'in tek-kuyruk
    /// garantisi yeni değerler için tanımsız. Anomali zinciri buraya
    /// eklenmemeli — o bir kaynak değil devam kuralı ve izi <c>Depth</c>'te.
    /// </para>
    /// </summary>
    [Fact]
    public void Kaynak_kumesi_dort_degerde_kapali()
    {
        Assert.Equal(4, Enum.GetValues<RcaTriggerSource>().Length);

        Assert.DoesNotContain(
            Enum.GetNames<RcaTriggerSource>(),
            name => name.Contains("Chain", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Anomaly", StringComparison.OrdinalIgnoreCase));
    }
}

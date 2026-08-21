using Bizigo.Devices;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bizigo.Simulators;

/// <param name="Profile">Okunan profil; doğrulama düştüyse yine de dönüyor.</param>
/// <param name="Errors">Boşsa profil geçerli.</param>
public sealed record ProfileLoadResult(SimulatorProfile Profile, IReadOnlyList<string> Errors);

/// <summary>
/// <c>catalog/simulators/*.yaml</c> okuyucusu ve <b>doğrulayıcısı</b>.
///
/// <para>
/// Doğrulama okuyucudan ayrı bir adım değil, aynı adım. Ayrı olsaydı biri
/// okuyup doğrulamayı unutabilirdi — ve unutulduğu yer, simülatörün var olmayan
/// bir örnek dosyaya işaret ettiği ve sessizce boş satır bastığı yer olurdu.
/// </para>
/// </summary>
public static class SimulatorProfileStore
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        // Profil alanları `owner_group` gibi; C# tarafı `OwnerGroup`. Şemanın
        // yazımı JSON sözleşmesiyle aynı (§8) — iki ayrı yazım kuralı, okuyanın
        // hangisini hatırlayacağını sorması demekti.
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        //
        // `IgnoreUnmatchedProperties()` YOK ve bu bilinçli. Varsayılan davranış
        // bilinmeyen bir alanda hata vermek; yutmak, `owner_grup` diye yazılmış
        // bir alanın sessizce YOK SAYILMASI demekti — profil geçerli görünür,
        // kapsam boş kalır, ve hata ancak ekran boş çıktığında fark edilir.
        // Bekçinin kendi belgesi "bilinmeyen alan kırmızı yanar" diyor; bu satır
        // olmadan o cümle yalan oluyordu.
        .Build();

    /// <summary>
    /// Dizindeki her profili okur ve doğrular.
    /// </summary>
    /// <param name="profileDirectory">Profillerin bulunduğu dizin.</param>
    /// <param name="repositoryRoot">Örnek ve config yollarının çözüleceği kök.</param>
    public static IReadOnlyList<ProfileLoadResult> LoadAll(
        string profileDirectory,
        string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var results = new List<ProfileLoadResult>();

        foreach (var path in Directory.EnumerateFiles(profileDirectory, "*.yaml").Order(StringComparer.Ordinal))
        {
            SimulatorProfile profile;

            try
            {
                profile = Yaml.Deserialize<SimulatorProfile>(File.ReadAllText(path))
                    ?? new SimulatorProfile();
            }
            catch (YamlException error)
            {
                // Ayrıştırma hatası da bir DOĞRULAMA bulgusu: koşum burada
                // patlamak yerine bütün profilleri okuyup hepsini birden
                // raporluyor. Tek tek düşen bir okuyucu, ikinci bozuk profili
                // ancak birincisi düzeltildikten sonra gösterirdi.
                results.Add(new ProfileLoadResult(
                    new SimulatorProfile { Id = Path.GetFileNameWithoutExtension(path) },
                    [$"YAML okunamadı: {error.Message}"]));

                continue;
            }

            results.Add(new ProfileLoadResult(
                profile,
                Validate(profile, path, profileDirectory, repositoryRoot)));
        }

        return results;
    }

    /// <summary>
    /// Vendor komutları — <b>üründen okunuyor</b>, burada yazılmıyor.
    ///
    /// <para>
    /// Bir profilin bu komutlardan birini tekrarlaması yasak ve bekçisi
    /// aşağıda. Gerekçe: komutun tek kaynağı toplayıcı. Profilde
    /// tekrarlansaydı, ürün komutu değiştirdiği gün simülatör eski komuta cevap
    /// vermeye devam ederdi — test yeşil kalır, üretim kırılır.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> VendorCommands { get; } =
    [
        .. new IConfigCollector[]
            {
                new FortiGateCollector(),
                new CiscoAsaCollector(),
                new MikroTikCollector(),
            }
            .SelectMany(collector => collector.Commands)
            .SelectMany(command => command.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(command => command.Trim())
            .Where(command => command.Length > 3)
            .Distinct(StringComparer.Ordinal),
    ];

    private static List<string> Validate(
        SimulatorProfile profile,
        string path,
        string profileDirectory,
        string repositoryRoot)
    {
        var errors = new List<string>();
        var fileName = Path.GetFileNameWithoutExtension(path);

        if (!string.Equals(profile.Id, fileName, StringComparison.Ordinal))
        {
            errors.Add($"`id` ('{profile.Id}') dosya adıyla ('{fileName}') ayrışıyor. " +
                       "Envanterdeki source_id id'den türüyor; ayrıştığı gün iki yarım cihaz doğar.");
        }

        foreach (var (alan, deger) in new[]
                 {
                     ("vendor", profile.Vendor),
                     ("product", profile.Product),
                     ("hostname", profile.Hostname),
                     ("owner_group", profile.OwnerGroup),
                 })
        {
            if (string.IsNullOrWhiteSpace(deger))
            {
                errors.Add($"`{alan}` boş. Kapsam ve envanter satırı bu alanlardan kuruluyor.");
            }
        }

        // Örnek dosyalar: profil onlara İŞARET ediyor, kopyalamıyor. İşaret
        // kırıksa simülatör sessizce hiçbir şey basmaz.
        foreach (var sample in profile.Syslog?.Samples ?? [])
        {
            if (!File.Exists(Path.Combine(repositoryRoot, sample)))
            {
                errors.Add($"Örnek dosya yok: {sample}");
            }
        }

        // Config dosyaları profil dizinine göreli.
        foreach (var (ad, yol) in ConfigPaths(profile))
        {
            if (!File.Exists(Path.Combine(profileDirectory, yol)))
            {
                errors.Add($"Config dosyası yok ({ad}): {yol}");
            }
        }

        var metin = File.ReadAllText(path);

        foreach (var command in VendorCommands)
        {
            if (metin.Contains(command, StringComparison.Ordinal))
            {
                errors.Add($"Profil bir vendor komutunu tekrarlıyor: '{command}'. " +
                           "Komutların tek kaynağı `src/Bizigo.Devices/ConfigCollector.cs`.");
            }
        }

        return errors;
    }

    private static IEnumerable<(string Ad, string Yol)> ConfigPaths(SimulatorProfile profile)
    {
        if (profile.Config is null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(profile.Config.Baseline))
        {
            yield return ("baseline", profile.Config.Baseline);
        }

        foreach (var (ad, yol) in profile.Config.Scenarios)
        {
            yield return (ad, yol);
        }
    }
}

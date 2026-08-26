using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bizigo.Simulators;

/// <param name="IdpGroup">Keycloak'taki grup yolu (<c>/network/core</c>).</param>
/// <param name="OwnerGroup">Ürünün kapsam grubu (<c>network/core</c>).</param>
public sealed record FleetIdpMapping(string IdpGroup, string OwnerGroup, string Note);

/// <param name="Profile">
/// <c>catalog/simulators/&lt;id&gt;.yaml</c> dosyasının adı.
///
/// <para>
/// Filo kaydı <b>owner_group taşımıyor</b> ve bu bir karar: grup profilde
/// duruyor, burada tekrarlanmıyor. İki yerde dursaydı ayrışabilirlerdi ve
/// ayrışan şey <b>kapsamın kendisi</b> olurdu — bu depoda ölçülmüş bir hata
/// sınıfı: <i>"CSV'de aynı kaynak iki kez geçince son satır sessizce
/// kazanıyordu; kazanan şey owner_group."</i>
/// </para>
/// </param>
public sealed record FleetDevice(string Profile);

/// <summary>
/// Filo tanımı (S05) — <b>tek dosya</b>.
///
/// <para>
/// Tek dosya bir kabul kriteri: beş cihazın tanımı beş yere dağılırsa "filo
/// neye benziyor" sorusunun cevabı hiçbir yerde durmaz.
/// </para>
/// </summary>
public sealed class FleetDefinition
{
    public int Version { get; set; } = 1;

    public List<FleetIdpMappingEntry> IdpMappings { get; set; } = [];

    public List<FleetDeviceEntry> Devices { get; set; } = [];
}

/// <summary>YAML bağlayıcının yazabilmesi için değiştirilebilir hâl.</summary>
public sealed class FleetIdpMappingEntry
{
    public string IdpGroup { get; set; } = string.Empty;
    public string OwnerGroup { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class FleetDeviceEntry
{
    public string Profile { get; set; } = string.Empty;
}

/// <param name="Fleet">Okunan tanım.</param>
/// <param name="Errors">
/// Doğrulama hataları. <b>Boş değilse filo kullanılmıyor</b> — kısmen geçerli
/// bir filo, kapsam yayılımını yarım kuran bir filodur ve yarım kapsam en pahalı
/// hata sınıfı.
/// </param>
public sealed record FleetLoadResult(FleetDefinition Fleet, IReadOnlyList<string> Errors);

/// <summary>
/// <c>catalog/simulators/filo.yaml</c> okuyucusu ve <b>doğrulayıcısı</b> (S05).
///
/// <para>
/// <b>Doğrulayıcı olması asıl iş.</b> Filo, kapsamın ekranda görünmesini
/// sağlayan şey; bozuk bir filo tanımı ekranda <i>"kapsam çalışıyor"</i>
/// görüntüsü üretip aslında yanlış veriyi gösterebilir. Hataların hepsi
/// <b>yükleme anında</b> çıkıyor — koşum anına bırakılsaydı yalnızca o cihaza
/// dokunan test görürdü.
/// </para>
/// </summary>
public static class FleetStore
{
    /// <summary>Filo dosyasının katalog içindeki adı.</summary>
    public const string FileName = "filo.yaml";

    /// <summary>
    /// Kapsam yayılımının anlamlı olması için gereken <b>asgari</b> grup sayısı.
    ///
    /// <para>
    /// Bir sabit değil bir <b>eşik</b>: tek gruba toplanmış bir filoda kapsam
    /// filtresinin filtrelediği kanıtlanamaz — her şey geçer ve test yeşil
    /// kalır. İki, "negatif kanıt üretilebilir"in en küçük değeri.
    /// </para>
    /// </summary>
    public const int MinimumOwnerGroups = 2;

    /// <param name="repositoryRoot">
    /// Örnek dosya yollarının çözüleceği kök. <b>Ayrı bir parametre</b>: profil
    /// kataloğu ile depo kökü aynı dizin değil, ve ikisini birleştirmek
    /// profillerin <c>samples</c> yollarını çözümsüz bırakıyor — ölçüldü, ilk
    /// koşumda beş profil birden "örnek dosya yok" verdi.
    /// </param>
    public static FleetLoadResult Load(string profileDirectory, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var path = Path.Combine(profileDirectory, FileName);

        if (!File.Exists(path))
        {
            return new FleetLoadResult(new FleetDefinition(), [$"Filo dosyası yok: {path}"]);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        FleetDefinition fleet;

        try
        {
            fleet = deserializer.Deserialize<FleetDefinition>(File.ReadAllText(path))
                    ?? new FleetDefinition();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return new FleetLoadResult(new FleetDefinition(), [$"Filo dosyası ayrıştırılamadı: {ex.Message}"]);
        }

        return new FleetLoadResult(fleet, Validate(fleet, profileDirectory, repositoryRoot));
    }

    private static List<string> Validate(
        FleetDefinition fleet,
        string profileDirectory,
        string repositoryRoot)
    {
        var errors = new List<string>();

        if (fleet.Devices.Count == 0)
        {
            errors.Add("Filoda hiç cihaz yok.");
            return errors;
        }

        // AYNI CİHAZ İKİ KEZ: reddediliyor, son satır kazanmıyor.
        //
        // Karar ve gerekçesi: bu depoda ölçülmüş bir hata sınıfı var —
        // "CSV'de aynı kaynak iki kez geçince son satır sessizce kazanıyordu ve
        // kazanan şey owner_group, yani kapsamın kendisi." Sessiz kazanma,
        // kapsamı yazan kişinin göremediği bir yerde değiştiriyor.
        //
        // Ret ucuz: bir satır silinir. Sessiz kazanma pahalı: belirtisi yok ve
        // yanlış grup ekranda doğru görünür.
        var duplicates = fleet.Devices
            .GroupBy(d => d.Profile, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicates)
        {
            errors.Add(
                $"'{duplicate}' filoda birden çok kez tanımlı. Son satır sessizce kazanmıyor: " +
                "kazanacak şey owner_group olurdu, yani kapsamın kendisi.");
        }

        var profiles = SimulatorProfileStore
            .LoadAll(profileDirectory, repositoryRoot)
            .ToDictionary(r => r.Profile.Id, r => r, StringComparer.Ordinal);

        var groups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in fleet.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Profile))
            {
                errors.Add("Cihaz kaydında `profile` boş.");
                continue;
            }

            if (!profiles.TryGetValue(device.Profile, out var loaded))
            {
                errors.Add(
                    $"'{device.Profile}' profili katalogda yok. Filo, var olmayan bir cihazı " +
                    "envantere yazamaz — yazsaydı kaynak envanterde görünür, veri hiç gelmezdi.");
                continue;
            }

            if (loaded.Errors.Count > 0)
            {
                errors.Add($"'{device.Profile}' profili bozuk: {string.Join("; ", loaded.Errors)}");
                continue;
            }

            groups.Add(loaded.Profile.OwnerGroup);
        }

        // Kapsam yayılımı eşiği. Bu, "filo doğru" demiyor — "filo kapsamı
        // GÖSTEREBİLİR" diyor.
        if (groups.Count < MinimumOwnerGroups)
        {
            errors.Add(
                $"Filo {groups.Count} owner_group taşıyor; en az {MinimumOwnerGroups} gerekiyor. " +
                "Tek gruplu bir filoda kapsam filtresinin filtrelediği kanıtlanamaz: her şey " +
                "geçer ve test yeşil kalır.");
        }

        // Eşlemeler: her owner_group'un bir IdP karşılığı olmalı, yoksa o grubun
        // verisi ekranda hiç kimseye görünmez.
        var mapped = fleet.IdpMappings
            .Select(m => m.OwnerGroup)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var group in groups.Except(mapped, StringComparer.Ordinal))
        {
            errors.Add(
                $"'{group}' grubunun IdP eşlemesi yok. Eşlemesiz bir grup ekranda HİÇ KİMSEYE " +
                "görünmez ve bu, boş bir ekranı 'veri yok' diye okutur.");
        }

        foreach (var mapping in fleet.IdpMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.IdpGroup) || string.IsNullOrWhiteSpace(mapping.OwnerGroup))
            {
                errors.Add("IdP eşlemesinde `idp_group` ya da `owner_group` boş.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Filodaki cihazların profilleri, dosyadaki sırayla.
    /// </summary>
    public static IReadOnlyList<SimulatorProfile> Profiles(
        FleetDefinition fleet,
        string profileDirectory,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        var profiles = SimulatorProfileStore
            .LoadAll(profileDirectory, repositoryRoot)
            .ToDictionary(r => r.Profile.Id, r => r.Profile, StringComparer.Ordinal);

        return [.. fleet.Devices
            .Where(d => profiles.ContainsKey(d.Profile))
            .Select(d => profiles[d.Profile])];
    }
}

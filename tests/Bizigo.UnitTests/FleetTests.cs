using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// Filo tanımı ve kapsam yayılımının önkoşulları (S05).
///
/// <para>
/// <b>Filo neyi mümkün kılıyor:</b> ürünün en pahalı hata sınıfı kapsam (K17)
/// ve tek gruba toplanmış bir filoda o sınıfın ekranda görünmesi
/// <b>imkânsız</b> — analist her şeyi görür ve kapsamın uygulandığını hiçbir
/// görüntü göstermez. İki grup, negatif kanıtın en küçük hâli.
/// </para>
/// </summary>
public sealed class FleetTests
{
    private static readonly string ProfileDirectory =
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    private static FleetDefinition Fleet(params string[] profiles) => new()
    {
        Devices = [.. profiles.Select(p => new FleetDeviceEntry { Profile = p })],
        IdpMappings =
        [
            new() { IdpGroup = "/network/core", OwnerGroup = "network/core" },
            new() { IdpGroup = "/network/edge", OwnerGroup = "network/edge" },
        ],
    };

    /// <summary>
    /// Depodaki filo dosyası <b>geçerli</b>.
    ///
    /// <para>
    /// Kuralların var olması ile bugün ihlal edilmemesi ayrı şeyler; bu test
    /// ikincisini sabitliyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Depodaki_filo_gecerli()
    {
        var loaded = FleetStore.Load(ProfileDirectory, RepositoryLayout.Root);

        Assert.Empty(loaded.Errors);
        Assert.Equal(5, loaded.Fleet.Devices.Count);
    }

    /// <summary>
    /// <b>Filo iki gruba yayılıyor</b> — kapsam yayılımının önkoşulu.
    ///
    /// <para>
    /// Bu test sonucu değil <b>zemini</b> sınıyor: kapsam testleri bu koşul
    /// sağlanmadan yazılabilir ve <i>yanlış sebeple</i> geçer — filtrelenecek
    /// bir şey olmadığı için.
    /// </para>
    /// </summary>
    [Fact]
    public void Filo_iki_gruba_yayiliyor()
    {
        var loaded = FleetStore.Load(ProfileDirectory, RepositoryLayout.Root);
        var profiles = FleetStore.Profiles(loaded.Fleet, ProfileDirectory, RepositoryLayout.Root);

        var groups = profiles
            .Select(p => p.OwnerGroup)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            groups.Count >= FleetStore.MinimumOwnerGroups,
            $"Filo {groups.Count} grup taşıyor: {string.Join(", ", groups.Order(StringComparer.Ordinal))}");

        // İki grup da GERÇEKTEN dolu: bir grubun tek cihazı silinirse yayılım
        // kalır ama negatif kanıt zayıflar; bu testin gördüğü şey o değil, ama
        // grupların boş olmadığı da sabitleniyor.
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g)));
    }

    /// <summary>
    /// <b>Aynı cihaz iki kez tanımlanamıyor — son satır sessizce kazanmıyor.</b>
    ///
    /// <para>
    /// <b>Karar ve gerekçesi.</b> Bu depoda ölçülmüş bir hata sınıfı var:
    /// <i>"CSV'de aynı kaynak iki kez geçince son satır sessizce kazanıyordu —
    /// kazanan şey owner_group, yani kapsamın kendisi."</i> Sessiz kazanma,
    /// kapsamı yazan kişinin göremediği bir yerde değiştiriyor.
    /// </para>
    ///
    /// <para>
    /// Ret ucuz: bir satır silinir. Sessiz kazanma pahalı: belirtisi yok ve
    /// yanlış grup ekranda <b>doğru</b> görünür.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayni_cihaz_iki_kez_tanimlanamiyor()
    {
        var fleet = Fleet("fw-ankara-01", "fw-izmir-01", "fw-ankara-01");

        var errors = Validate(fleet);

        Assert.Contains(errors, e => e.Contains("birden çok kez", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("owner_group", StringComparison.Ordinal));
    }

    /// <summary>
    /// Tek gruplu filo <b>reddediliyor</b>.
    ///
    /// <para>
    /// Hata metni sebebi söylüyor: her şey geçer ve test yeşil kalır. Yalnızca
    /// "en az iki grup gerekiyor" deseydi okuyan kişi bunu keyfî bir kural
    /// sanardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Tek_gruplu_filo_reddediliyor()
    {
        // İkisi de `network/core`.
        var fleet = Fleet("fw-ankara-01", "asa-dc-01");

        var errors = Validate(fleet);

        Assert.Contains(errors, e => e.Contains("owner_group taşıyor", StringComparison.Ordinal));
    }

    /// <summary>
    /// Var olmayan profil <b>reddediliyor</b>.
    ///
    /// <para>
    /// Kabul edilseydi kaynak envanterde görünür ve veri hiç gelmezdi — yani
    /// "sessiz cihaz" gibi okunan, aslında var olmayan bir cihaz.
    /// </para>
    /// </summary>
    [Fact]
    public void Var_olmayan_profil_reddediliyor()
    {
        var fleet = Fleet("fw-ankara-01", "fw-izmir-01", "olmayan-cihaz");

        Assert.Contains(Validate(fleet), e => e.Contains("katalogda yok", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Eşlemesiz grup reddediliyor.</b>
    ///
    /// <para>
    /// Eşlemesi olmayan bir <c>owner_group</c>'un verisi ekranda <b>hiç
    /// kimseye</b> görünmez — ve boş bir ekran "veri yok" diye okunur. Yani
    /// eksik bir satır, veri kaybı gibi görünen bir yapılandırma hatası
    /// üretiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Eslemesiz_grup_reddediliyor()
    {
        var fleet = Fleet("fw-ankara-01", "fw-izmir-01");
        fleet.IdpMappings.RemoveAll(m => m.OwnerGroup == "network/edge");

        Assert.Contains(Validate(fleet), e => e.Contains("IdP eşlemesi yok", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Filo dosyası owner_group taşımıyor</b> — grup profilde, tek yerde.
    ///
    /// <para>
    /// İki yerde dursaydı ayrışabilirlerdi ve ayrışan şey kapsamın kendisi
    /// olurdu. Bu test dosyanın <b>şeklini</b> sabitliyor: biri kolaylık olsun
    /// diye filoya grup eklerse burası kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Filo_dosyasi_grup_tekrarlamiyor()
    {
        var text = File.ReadAllText(Path.Combine(ProfileDirectory, FleetStore.FileName));

        // `idp_mappings` bloğu owner_group taşıyor ve taşımalı; sınanan şey
        // `devices` bloğu.
        var devicesBlock = text[text.IndexOf("devices:", StringComparison.Ordinal)..];

        Assert.DoesNotContain("owner_group", devicesBlock, StringComparison.Ordinal);
    }

    /// <summary>Doğrulamayı geçici bir dosya üzerinden koşturur.</summary>
    private static IReadOnlyList<string> Validate(FleetDefinition fleet)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bizigo-filo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // Profil kataloğu gerçek olandan kopyalanıyor: doğrulayıcı
            // profilleri okuyor ve sahte bir katalog, testin sınadığı şeyi
            // değil taklidi ölçerdi.
            //
            // `profiller/` alt dizini de kopyalanıyor ve bu ölçülerek öğrenildi:
            // yalnızca `*.yaml` kopyalanınca beş profil birden "config dosyası
            // yok" verdi, grup kümesi boş kaldı, ve "eşlemesiz grup" testi
            // hiçbir zaman kendi iddiasına ULAŞAMADI — yani kırmızıydı ama
            // yanlış sebeple.
            foreach (var file in Directory.GetFiles(ProfileDirectory, "*.yaml"))
            {
                if (!string.Equals(Path.GetFileName(file), FleetStore.FileName, StringComparison.Ordinal))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }
            }

            foreach (var source in Directory.GetDirectories(ProfileDirectory))
            {
                var target = Path.Combine(directory, Path.GetFileName(source));
                Directory.CreateDirectory(target);

                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var destination = Path.Combine(target, relative);

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }

            File.WriteAllText(Path.Combine(directory, FleetStore.FileName), Serialize(fleet));

            return FleetStore.Load(directory, RepositoryLayout.Root).Errors;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Serialize(FleetDefinition fleet)
    {
        var mappings = string.Join("\n", fleet.IdpMappings.Select(m =>
            $"  - idp_group: {m.IdpGroup}\n    owner_group: {m.OwnerGroup}\n    note: test"));

        var devices = string.Join("\n", fleet.Devices.Select(d => $"  - profile: {d.Profile}"));

        return $"version: 1\nidp_mappings:\n{mappings}\ndevices:\n{devices}\n";
    }
}

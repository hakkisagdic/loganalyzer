using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// Cihaz simülatör profilleri (FS · S01).
///
/// <para>
/// Docker yok, SSH yok — bu paket ajan sınırında koşuyor (§2). Sınandığı şey
/// profil şeması ve N1 taşıyıcısının davranışı; <b>SSH'ın kendisi değil</b>.
/// </para>
/// </summary>
public sealed class SimulatorProfileTests
{
    private static string ProfileDirectory =>
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    private static IReadOnlyList<ProfileLoadResult> Load() =>
        SimulatorProfileStore.LoadAll(ProfileDirectory, RepositoryLayout.Root);

    /// <summary>
    /// Depodaki her profil geçerli.
    ///
    /// <para>
    /// Bu testin yazılırken ilk koşumu <b>kırmızı</b> yandı ve haklıydı:
    /// <c>lb-web-01</c> var olmayan bir örnek dosyaya işaret ediyordu
    /// (<c>json.log</c>, oysa dosya <c>access-json.log</c>). Kırık bir işaret
    /// simülatörü sessizce hiçbir şey basmayan bir şeye çevirirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_profil_gecerli()
    {
        var results = Load();

        Assert.NotEmpty(results);

        var bozuk = results
            .Where(r => r.Errors.Count > 0)
            .Select(r => $"{r.Profile.Id}: {string.Join("; ", r.Errors)}")
            .ToArray();

        Assert.True(bozuk.Length == 0, "Geçersiz profil:\n  " + string.Join("\n  ", bozuk));
    }

    /// <summary>
    /// <b>Filo birden çok kapsam grubuna yayılmış.</b>
    ///
    /// <para>
    /// Tek gruba toplanmış bir filo, ürünün en pahalı hata sınıfının (K17 —
    /// kapsam) ekranda görünmesini imkânsız kılar: analist her şeyi görür ve
    /// kapsamın uygulandığını hiçbir görüntü göstermez. Bu yüzden bir sayı
    /// değil bir <b>ayrım</b> sınanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Filo_birden_cok_kapsam_grubuna_yayiliyor()
    {
        var gruplar = Load()
            .Select(r => r.Profile.OwnerGroup)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            gruplar.Length >= 2,
            "Filo tek kapsam grubunda: " + string.Join(", ", gruplar) +
            ". İki analistin ekranının farklı olduğunu gösterecek hiçbir profil yok.");
    }

    /// <summary>
    /// Hiçbir profil vendor komutunu tekrarlamıyor.
    ///
    /// <para>
    /// Komutların tek kaynağı <c>ConfigCollector</c>. Profilde tekrarlansaydı,
    /// ürün komutu değiştirdiği gün simülatör eski komuta cevap vermeye devam
    /// ederdi: test yeşil kalır, üretim kırılır.
    /// </para>
    /// </summary>
    [Fact]
    public void Hicbir_profil_vendor_komutunu_tekrarlamiyor()
    {
        var komutlar = new IConfigCollector[]
            {
                new FortiGateCollector(),
                new CiscoAsaCollector(),
                new MikroTikCollector(),
            }
            .SelectMany(c => c.Commands)
            .SelectMany(c => c.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => c.Length > 3)
            .ToArray();

        var ihlal = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ProfileDirectory, "*.yaml"))
        {
            var metin = File.ReadAllText(path);

            ihlal.AddRange(komutlar
                .Where(k => metin.Contains(k, StringComparison.Ordinal))
                .Select(k => $"{Path.GetFileName(path)} → '{k}'"));
        }

        Assert.True(ihlal.Count == 0, "Profil vendor komutu taşıyor:\n  " + string.Join("\n  ", ihlal));
    }

    /// <summary>
    /// <b>Bilinmeyen alan sessizce yutulmuyor.</b>
    ///
    /// <para>
    /// Okuyucu <c>IgnoreUnmatchedProperties()</c> KULLANMIYOR ve bu testin
    /// koruduğu şey o karar. Yutulsaydı <c>owner_grup</c> diye yazılmış bir alan
    /// yok sayılırdı: profil geçerli görünür, kapsam boş kalır ve hata ancak
    /// ekran boş çıktığında fark edilirdi — bu depodaki en pahalı hata sınıfı.
    /// </para>
    ///
    /// <para>
    /// Ayrıştırma hatası koşumu patlatmıyor, bir <b>doğrulama bulgusuna</b>
    /// dönüyor: tek tek düşen bir okuyucu ikinci bozuk profili ancak birincisi
    /// düzeltildikten sonra gösterirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Bilinmeyen_alan_sessizce_yutulmuyor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            // `owner_grup` — `owner_group` yazılacakken yapılan bir harf hatası.
            File.WriteAllText(
                Path.Combine(directory, "typo-01.yaml"),
                """
                id: typo-01
                vendor: fortinet
                product: fortigate
                hostname: typo-01
                owner_grup: network/core
                """);

            var result = Assert.Single(SimulatorProfileStore.LoadAll(directory, RepositoryLayout.Root));

            Assert.NotEmpty(result.Errors);
            Assert.Contains(
                result.Errors,
                error => error.Contains("owner_grup", StringComparison.Ordinal)
                    || error.Contains("YAML okunamadı", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>N1: varsayılan koşum baseline'ı döndürüyor.</summary>
    [Fact]
    public async Task Varsayilan_kosum_baseline_donduruyor()
    {
        var profile = Load().Single(r => r.Profile.Id == "fw-ankara-01").Profile;
        var transport = new SimulatedDeviceTransport(profile, ProfileDirectory);

        var result = await transport.RunAsync(Target(), ["show"], TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("set hostname \"fw-ankara-01\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("#conf_file_ver=12", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// N1: senaryo gerçekten farklı bir config döndürüyor.
    ///
    /// <para>
    /// Sınanan şey fark motoru değil — o T26'nın işi. Burada sınanan, senaryo
    /// mekanizmasının <b>gerçekten başka bir metin</b> verdiği; aksi hâlde
    /// T26'nın testleri hep aynı girdiyle koşar ve fark üretmediği için yeşil
    /// yanardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Senaryo_baska_bir_config_donduruyor()
    {
        var profile = Load().Single(r => r.Profile.Id == "fw-ankara-01").Profile;

        var taban = await new SimulatedDeviceTransport(profile, ProfileDirectory)
            .RunAsync(Target(), ["show"], TestContext.Current.CancellationToken);

        var kural = await new SimulatedDeviceTransport(profile, ProfileDirectory, "kural-eklendi")
            .RunAsync(Target(), ["show"], TestContext.Current.CancellationToken);

        Assert.True(kural.Ok, kural.Error);
        Assert.NotEqual(taban.Output, kural.Output);
        Assert.DoesNotContain("engelle-tor", taban.Output, StringComparison.Ordinal);
        Assert.Contains("engelle-tor", kural.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Bilinmeyen senaryo sessizce baseline'a düşmüyor.</b>
    ///
    /// <para>
    /// Düşseydi, adı yanlış yazılmış bir senaryo testi yeşil bırakır ve "fark
    /// yok" sonucu doğru sanılırdı — bu deponun en pahalı hata sınıfı (§7).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bilinmeyen_senaryo_sessizce_basarili_olmuyor()
    {
        var profile = Load().Single(r => r.Profile.Id == "fw-ankara-01").Profile;
        var transport = new SimulatedDeviceTransport(profile, ProfileDirectory, "boyle-bir-senaryo-yok");

        var result = await transport.RunAsync(Target(), ["show"], TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("boyle-bir-senaryo-yok", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Output);
    }

    /// <summary>
    /// Config yüzeyi olmayan profil bu yüzeyde <b>başarısız</b> dönüyor, boş değil.
    ///
    /// <para>
    /// Boş metin "cihaza bağlanıldı ama config'i yok" derdi. Gerçek şu ki
    /// <c>lb-web-01</c> bir ağ cihazı değil ve config yüzeyi hiç yok — ikisi
    /// farklı cümleler.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Config_yuzeyi_olmayan_profil_acikca_basarisiz()
    {
        var profile = Load().Single(r => r.Profile.Id == "lb-web-01").Profile;

        Assert.Null(profile.Ssh);

        var result = await new SimulatedDeviceTransport(profile, ProfileDirectory)
            .RunAsync(Target(), ["show"], TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("config yüzeyi yok", result.Error, StringComparison.Ordinal);
    }

    /// <summary>Profil gizli bilginin kendisini değil, nereden okunacağını taşıyor.</summary>
    [Fact]
    public void Profil_gizli_bilgi_tasimiyor()
    {
        foreach (var result in Load())
        {
            if (result.Profile.Ssh is null)
            {
                continue;
            }

            Assert.False(
                string.IsNullOrWhiteSpace(result.Profile.Ssh.CredentialEnv),
                $"{result.Profile.Id}: `credential_env` boş — parola nereden okunacak?");
        }

        // Şemada `credential` diye bir alan OLMAMALI: olsaydı bir gün birinin
        // oraya gerçek bir parola yazması an meselesiydi.
        Assert.Null(typeof(SimulatorSsh).GetProperty("Credential"));
    }

    private static DeviceTarget Target() => new()
    {
        Vendor = "fortinet",
        Host = "127.0.0.1",
        Username = "bizigo-ro",
        Credential = "kullanilmiyor",
    };
}

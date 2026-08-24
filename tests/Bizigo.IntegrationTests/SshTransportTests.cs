using Bizigo.Devices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b><see cref="SshDeviceTransport"/>'un ilk kez gerçekten koştuğu yer</b>
/// (S03).
///
/// <para>
/// Taşıyıcı olgu: bu sınıf F1'de yazıldı ve o günden beri <b>tek bir test onu
/// ağa çıkarmadı</b>. N1 (<c>SimulatedDeviceTransport</c>) süreç içi çalışıyor
/// ve kendi yorumunda ne kanıtlamadığını söylüyor: el sıkışma, kimlik
/// doğrulama, kanal açılışı, exec isteği ve çıktı çerçeveleme o seviyede
/// <b>hiç yaşanmıyor</b>.
/// </para>
///
/// <para>
/// <b>Neden container, neden süreç içi bir SSH kütüphanesi değil:</b> süreç içi
/// bir sunucu istemciyi <i>kütüphaneye</i> karşı sınardı, sunucuya karşı değil.
/// Ayrıca S06 (CLI öykünmesi) aynı zemine ihtiyaç duyuyor ve süreç içi bir
/// çözüm orada atılıp yeniden yazılırdı.
/// </para>
///
/// <para>
/// <b>Yanlış parola ölçütü neden "401 dönüyor" DEĞİL:</b> SSH'ın HTTP durum
/// kodu yok. Ölçüt oraya yazılsaydı tanım gereği hiç gerçekleşemezdi — bu
/// depoda "adı ile gövdesi ayrışan bekçi" diye ölçülen sınıfın kitabi hâli.
/// Ölçüt <see cref="DeviceCommandResult.Ok"/>'in <see langword="false"/> olması
/// ve hatanın <b>kimlik doğrulama</b> hatası olması.
/// </para>
///
/// <para>
/// <b>Koordinatör koşturur (§2).</b> Container ayağa kalkıyor, yani ajan
/// sınırının dışında.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshTransportTests : IAsyncLifetime
{
    private const string Username = "bizigo-ro";
    private const string Password = "sim-parola";
    private const string Profile = "fw-ankara-01";

    private IFutureDockerImage _image = null!;
    private IContainer _server = null!;

    public async ValueTask InitializeAsync()
    {
        // İmaj compose'daki ile AYNI bağlamdan derleniyor: iki ayrı tanım
        // olsaydı testin ayağa kaldırdığı sunucu ile geliştiricinin elle
        // kaldırdığı sunucu zamanla ayrışırdı.
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "deploy/ssh-sim")
            .WithDockerfile("Dockerfile")
            .WithCleanUp(true)
            .Build();

        await _image.CreateAsync(TestContext.Current.CancellationToken);

        // İmaj adı kurucuya veriliyor: parametresiz kurucu kullanımdan
        // kalktı ve `DevStackFixture` de aynı biçimi kullanıyor.
        _server = new ContainerBuilder(_image.FullName)
            .WithEnvironment("SIM_PROFILE", Profile)
            .WithEnvironment("SIM_USERNAME", Username)
            .WithEnvironment("SIM_PASSWORD", Password)

            // Katalog salt okunur: bir koşum sonrakinin girdisini bozamasın.
            .WithBindMount(
                Path.Combine(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath, "catalog", "simulators"),
                "/sim",
                DotNet.Testcontainers.Configurations.AccessMode.ReadOnly)
            .WithPortBinding(22, assignRandomHostPort: true)

            // Banner bekleniyor, port DEĞİL: açık bir port "dinliyor" demek,
            // "SSH konuşuyor" demek değil. Erken bağlanan bir test, sunucunun
            // hazır olmadığını "kimlik doğrulama başarısız" diye okurdu.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("N2 hazır"))
            .Build();

        await _server.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _image.DisposeAsync();
    }

    private DeviceTarget Target(string? password = null, string? username = null) => new()
    {
        Vendor = "fortinet",
        Host = _server.Hostname,
        Port = _server.GetMappedPublicPort(22),
        Username = username ?? Username,
        Credential = password ?? Password,
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static SshDeviceTransport Transport() =>
        new(NullLogger<SshDeviceTransport>.Instance);

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: ürün <b>gerçek bir SSH sunucusuna</b>
    /// bağlanıp vendor komutunu çalıştırıyor ve config metnini alıyor.
    ///
    /// <para>
    /// Komutlar <see cref="FortiGateCollector"/>'dan geliyor, elle yazılmıyor:
    /// ürün komutunu değiştirirse test onunla birlikte değişir. Elle yazsaydım
    /// tablo ikinci kez kopyalanır ve ayrışma "cihaz cevap verdi" diye yeşil
    /// kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gercek_sunucudan_config_cekiliyor()
    {
        var collector = new FortiGateCollector();

        var result = await Transport().RunAsync(
            Target(), collector.Commands, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);

        // Baseline config'in tanınabilir bir parçası: dosyanın tamamını
        // karşılaştırmak, config düzeltildiğinde testi ilgisiz bir sebeple
        // kırardı.
        Assert.Contains("config", result.Output, StringComparison.Ordinal);
        Assert.NotEmpty(result.Output.Trim());
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: <b>yanlış parola reddediliyor</b> ve
    /// sonuç bir kimlik doğrulama hatası.
    ///
    /// <para>
    /// Ölçüt "401" değil — SSH'ın durum kodu yok. Burada bakılan şey
    /// <c>Ok == false</c> ve hatanın <c>SshDeviceTransport</c>'un kendi yazdığı
    /// kimlik doğrulama metni olması.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yanlis_parola_kimlik_dogrulama_hatasi_veriyor()
    {
        var result = await Transport().RunAsync(
            Target(password: "yanlis-parola"),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("Kimlik doğrulama", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Output);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: hata metni <b>parolayı taşımıyor</b>.
    ///
    /// <para>
    /// <c>SshDeviceTransport</c> istisna metnini kütüphaneden almıyor, kendisi
    /// yazıyor — ama bu iddia bugüne kadar <b>gerçek bir istisnayla</b> hiç
    /// sınanmadı. Kütüphanenin ne bastığına güvenilmediği yazılıydı; burası
    /// onu ölçüyor.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hata_metni_parolayi_sizdirmiyor()
    {
        const string secret = "cok-gizli-parola-42";

        var result = await Transport().RunAsync(
            Target(password: secret),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: bilinmeyen kullanıcı da reddediliyor.
    ///
    /// <para>
    /// Ayrı bir test çünkü ayrı bir yol: OpenSSH var olmayan kullanıcıyı da
    /// parola hatası gibi gösteriyor (kullanıcı numaralandırmasını önlemek
    /// için) ve ürünün bunu <b>aynı</b> sonuca çevirdiği ölçülmeli — iki farklı
    /// arıza tek bir "bağlanamadı"ya düşerse operatör hangisi olduğunu bilemez.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bilinmeyen_kullanici_reddediliyor()
    {
        var result = await Transport().RunAsync(
            Target(username: "olmayan-kullanici"),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Error);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: <b>hazırlık komutu ile config komutu
    /// ayrı ayrı çalışıyor</b> ve toplayıcı ilkinde durmuyor.
    ///
    /// <para>
    /// FortiGate toplayıcısı önce sayfalamayı kapatıyor
    /// (<c>config system console…</c>), sonra <c>show</c> diyor. Simülatör
    /// hazırlık komutuna hata dönseydi toplayıcı config'e hiç gelmezdi — ve bu,
    /// gerçek cihazda da aynı şekilde kırılacak bir yol.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hazirlik_komutu_akisi_durdurmuyor()
    {
        var commands = new FortiGateCollector().Commands;

        // Toplayıcının ilk komutu hazırlık; tek başına çalıştırıldığında da
        // başarılı olmalı.
        Assert.True(commands.Count >= 2);

        var prepared = await Transport().RunAsync(
            Target(), [commands[0]], TestContext.Current.CancellationToken);

        Assert.True(prepared.Ok, prepared.Error);
    }

    /// <summary>
    /// Koşturulduğunda kanıtladığı şey: cihaz <b>kabuk vermiyor</b>.
    ///
    /// <para>
    /// Zorlanmış komut olmasaydı simülatör bir Linux kutusu gibi davranırdı:
    /// <c>ls</c> çalışır, vendor komutu çalışmazdı. Bu, ürünün asla
    /// karşılaşmayacağı bir yüzey — ve S06'nın öykündüğü CLI de bu değil.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Taninmayan_komut_reddediliyor()
    {
        var result = await Transport().RunAsync(
            Target(), ["ls -la /"], TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
    }
}

using System.Text;
using Bizigo.Devices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>N3 — CLI öykünmesi</b> (S06 · S08). Toplayıcının <b>doğru</b> koştuğunu sınayan
/// paket.
///
/// <para>
/// S03 <i>"ürün gerçek bir SSH sunucusuna bağlanabiliyor mu"</i> sorusunu
/// kapattı. Bu sınıf ayrı bir soru soruyor: <b>cihaz bir cihaz gibi
/// davrandığında</b> toplayıcı hâlâ doğru veriyi alıyor mu. Taşıyıcı cümle:
/// gerçek bir ASA <c>--More--</c> basar ve toplayıcı onu görmezse çıktının
/// yarısını alır — <b>hatasız</b>.
/// </para>
///
/// <para>
/// <b>Koordinatör koşturur (§2).</b> Her test bir container ayağa kaldırıyor.
/// Ajan tarafında yazıldılar ve <b>koşturulmadılar</b>; her testin özet
/// yorumunda koşturulduğunda ne kanıtlayacağı yazılı.
/// </para>
///
/// <para>
/// <b>Sayfalamanın Docker'sız ölçülen kısmı</b> <c>SshSimulatorCliTests</c>'te
/// (birim paketi): öykünmenin kendi davranışı — kesme, sınıflandırma, vendor
/// metni — konteyner istemiyor ve orada ölçüldü. Buradaki testlerin eklediği
/// şey <b>ürünün</b> o davranışla karşılaştığında ne yaptığı, yani
/// <see cref="SshDeviceTransport"/>'un gerçek bir exec kanalından ne okuduğu.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliEmulationTests : IAsyncLifetime
{
    private const string Username = "bizigo-ro";
    private const string Password = "sim-parola";
    private const string FortiGate = "fw-ankara-01";
    private const string CiscoAsa = "asa-dc-01";

    /// <summary>
    /// Sayfa boyutu bilerek küçük: <c>fw-ankara-01</c> baseline'ı bundan
    /// belirgin biçimde uzun, yani sayfalama gerçekten tetikleniyor. Config'ten
    /// büyük bir sayfa boyutu sayfalamayı hiç tetiklemez ve testler
    /// <i>"sayfalama sorunsuz"</i> diye yeşil kalırdı — ölçülen hiçbir şey
    /// olmadan.
    ///
    /// <para>
    /// Bu sayı ile config uzunluğunun ilişkisi <see cref="SayfalamaOlculebilir"/>
    /// tarafından sınanıyor; yalnızca yorumda kalsaydı baseline kısaldığında
    /// sessizce yanlış olurdu.
    /// </para>
    /// </summary>
    private const string PageLines = "10";

    private IFutureDockerImage _image = null!;
    private readonly List<IContainer> _started = [];

    public async ValueTask InitializeAsync()
    {
        // İmaj compose'daki ile AYNI bağlamdan derleniyor — `SshTransportTests`
        // ile aynı karar ve aynı gerekçe: iki ayrı tanım zamanla ayrışır.
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "deploy/ssh-sim")
            .WithDockerfile("Dockerfile")
            .WithCleanUp(true)
            .Build();

        await _image.CreateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _started)
        {
            await container.DisposeAsync();
        }

        await _image.DisposeAsync();
    }

    /// <param name="paging">
    /// <c>etkilesimli</c> · <c>daima</c> · <c>kapali</c> — anlamları
    /// <c>deploy/ssh-sim/komut-dagitici.sh</c> içinde yazılı.
    /// </param>
    private async Task<IContainer> StartAsync(
        string profile = FortiGate,
        string paging = "etkilesimli",
        string scenario = "")
    {
        var builder = new ContainerBuilder(_image.FullName)
            .WithEnvironment("SIM_PROFILE", profile)
            .WithEnvironment("SIM_USERNAME", Username)
            .WithEnvironment("SIM_PASSWORD", Password)
            .WithEnvironment("SIM_SCENARIO", scenario)
            .WithEnvironment("SIM_PAGING", paging)
            .WithEnvironment("SIM_PAGE_LINES", PageLines)

            // `--More--` cevapsız kaldığında cihazın bırakma süresi. Bu sayı bir
            // ÖLÇÜT değil: hiçbir iddia süreye bakmıyor, hepsi çıktının
            // içeriğine bakıyor (§6).
            .WithEnvironment("SIM_MORE_TIMEOUT", "2")

            .WithBindMount(
                Path.Combine(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath, "catalog", "simulators"),
                "/sim",
                DotNet.Testcontainers.Configurations.AccessMode.ReadOnly)
            .WithPortBinding(22, assignRandomHostPort: true)

            // Banner bekleniyor, port DEĞİL — S03'ün gerekçesi: açık bir port
            // "dinliyor" demek, "SSH konuşuyor" demek değil.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("N2 hazır"));

        var container = builder.Build();

        _started.Add(container);

        await container.StartAsync(TestContext.Current.CancellationToken);

        return container;
    }

    private static DeviceTarget Target(IContainer server, string vendor = "fortinet") => new()
    {
        Vendor = vendor,
        Host = server.Hostname,
        Port = server.GetMappedPublicPort(22),
        Username = Username,
        Credential = Password,
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static SshDeviceTransport Transport() => new(NullLogger<SshDeviceTransport>.Instance);

    private static int ConfigLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Trim().Length > 0 && !line.Contains("--More--", StringComparison.Ordinal));

    /// <summary>
    /// Sayfalamanın <b>gerçekten tetiklendiğini</b> sınar: sayfa boyutu
    /// config'ten kısa olmak zorunda.
    ///
    /// <para>
    /// Bu kontrol olmadan sayfalama testleri, sayfa boyutu config'ten uzun
    /// olduğu gün <b>hiçbir şey ölçmeden</b> yeşil kalırdı — "sayfalama
    /// sorunsuz" görünen bir koşum, aslında sayfalamanın hiç çalışmadığı bir
    /// koşum olurdu. Depoda adı konmuş sınıf: yeşil bir sonuç, ölçümün
    /// yapılmadığı anlamına da gelebiliyor (§6).
    /// </para>
    /// </summary>
    private static void SayfalamaOlculebilir(string profile)
    {
        var lines = ProfileConfigLines(profile);

        Assert.True(
            int.Parse(PageLines, System.Globalization.CultureInfo.InvariantCulture) < lines,
            $"Sayfa boyutu ({PageLines}) profil config'inden ({lines} satır) kısa değil — " +
            "sayfalama hiç tetiklenmez ve bu sınıftaki sayfalama testleri boş yere yeşil kalır.");
    }

    /// <summary>
    /// Bir profilin config dosyasındaki <b>anlamlı satır sayısı</b> — testin
    /// beklediği sayı buradan geliyor, elle yazılmıyor.
    ///
    /// <para>
    /// İlk yazımda sabit <c>33</c> yazılmıştı ve <b>yanlış olan sayı değil,
    /// sayının kaynağıydı</b>: baseline düzeltilseydi test <b>ilgisiz bir
    /// sebeple</b> kırılır ve kıran şey ürünün davranışı olmazdı. Oran da
    /// çözüm değildi — <i>"çıplak `show`'dan uzun"</i> gibi bir iddia,
    /// sayfalamanın hiç uygulanmadığı hâli de geçirirdi.
    /// </para>
    ///
    /// <para>
    /// Sayıyı <b>aynı kaynaktan türetmek</b> ikisini birden çözüyor: iddia
    /// mutlak kalıyor (<i>config'in tamamı</i>) ve baseline değiştiğinde
    /// beklenen sayı onunla birlikte değişiyor. Depoda emsali var — T39'un
    /// görünüm kolonlarını göç dosyasından okuması, T32'nin sentetik alanları
    /// <c>FIELD_MAP</c>'ten okuması.
    /// </para>
    /// </summary>
    /// <summary>
    /// Profil config'inin <b>son anlamlı satırı</b> — çıktının sonuna kadar
    /// geldiğini anlamanın işareti.
    ///
    /// <para>
    /// Sabit bir metin yazmak yerine kaynaktan okunuyor, <see cref="ProfileConfigLines"/>
    /// ile aynı gerekçeyle: baseline düzeltildiğinde işaret onunla birlikte
    /// değişiyor ve test ilgisiz bir sebeple kırılmıyor.
    /// </para>
    /// </summary>
    private static string LastConfigLine(string profile, string scenario = "baseline")
    {
        var path = Path.Combine(
            CommonDirectoryPath.GetSolutionDirectory().DirectoryPath,
            "catalog", "simulators", "profiller", profile, $"{scenario}.conf");

        Assert.True(File.Exists(path), $"Profil config'i yok: {path}");

        var last = File.ReadAllLines(path).LastOrDefault(l => l.Trim().Length > 0);

        Assert.False(string.IsNullOrWhiteSpace(last), $"Profil config'i boş: {path}");

        return last!.Trim();
    }

    private static int ProfileConfigLines(string profile, string scenario = "baseline")
    {
        var path = Path.Combine(
            CommonDirectoryPath.GetSolutionDirectory().DirectoryPath,
            "catalog", "simulators", "profiller", profile, $"{scenario}.conf");

        // Dosyanın VARLIĞI iddia ediliyor: yolu yanlış yazılmış bir yardımcı
        // sıfır döndürüp "config hiç gelmedi" hâlini sessizce geçirirdi.
        Assert.True(File.Exists(path), $"Profil config'i yok: {path}");

        var lines = File.ReadAllLines(path).Count(l => l.Trim().Length > 0);

        Assert.True(lines > 0, $"Profil config'i boş: {path}");

        return lines;
    }

    // ------------------------------------------------------- BULGU: sayfalama

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey — S06'nın bulgusunun S08'de
    /// kapandığı yer:</b> sayfalayan bir cihazda toplayıcı artık <b>tam</b>
    /// config alıyor.
    ///
    /// <para>
    /// S06'da bu test bulgunun kaydıydı ve ters yönde iddia ediyordu: çıktı
    /// yarım geliyordu, <c>Ok=true</c>, <c>Error</c> boş, <c>Failure=None</c> —
    /// yani üründe hiçbir şey yanlış gitmiş görünmüyordu, oysa fark motoru
    /// kaybı <b>silinmiş yüzlerce satır</b> diye okuyacaktı.
    /// </para>
    ///
    /// <para>
    /// S08'in düzeltmesi toplayıcının komutunu <b>kendi kendine yeten tek bir
    /// oturuma</b> topladı. Bu test artık o düzeltmenin geri alınmasını
    /// yakalayan bekçi: komutlar yine ayrı elemanlara bölünürse çıktı yeniden
    /// yarım gelir ve burası kırmızı yanar.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçüt HTTP durum kodu değil</b> ve olamaz — SSH'ın öyle bir şeyi yok
    /// (S03'ün dersi). Ölçüt satır sayısı: alınan config, cihazdakinin
    /// <b>tamamı</b>.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sayfalama_acikken_bile_toplayici_tam_config_aliyor()
    {
        SayfalamaOlculebilir(FortiGate);

        var server = await StartAsync(paging: "daima");

        var result = await Transport().RunAsync(
            Target(server),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(DeviceFailureKind.None, result.Failure);
        Assert.Empty(result.Error);

        // İmleç çıktıya HİÇ girmiyor: sayfalama aynı oturumda kapatıldı.
        Assert.DoesNotContain("--More--", result.Output, StringComparison.Ordinal);

        Assert.Equal(ProfileConfigLines(FortiGate), ConfigLines(result.Output));
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey — düzeltmenin DOĞRU KATMANDA
    /// olduğu:</b> sayfalama kapatma aynı oturumda taşınıyor, ayrı bir
    /// oturumda taşınmıyor.
    ///
    /// <para>
    /// <see cref="SshDeviceTransport"/> her komut için <c>CreateCommand</c>
    /// çağırıyor, yani <b>her eleman ayrı bir exec kanalı</b> — gerçek bir
    /// cihazda da ayrı bir oturum. <c>terminal pager 0</c> oturuma ait bir
    /// ayar; kanal kapanınca ölüyor. S08 bunu taşımaya çalışmadı, <b>gereksiz
    /// kıldı</b>: toplayıcının komutu artık kendi kendine yetiyor.
    /// </para>
    ///
    /// <para>
    /// Test iki kolu karşılaştırıyor ve <b>ikisi de gerekli</b>: toplayıcının
    /// kendi komutu tam çıktı veriyor, aynı config'i sayfalama kapatmadan
    /// isteyen çıplak bir komut ise <b>yarım</b>. İkinci kol olmadan birincisi,
    /// öykünmenin sayfalamayı hiç uygulamadığı hâlden ayırt edilemezdi — S06'da
    /// bu ayrımı tutan test buydu ve S08'de yönü değişti, kendisi değil.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sayfalama_kapatma_ayni_oturumda_tasiniyor_ayrida_tasinmiyor()
    {
        SayfalamaOlculebilir(FortiGate);

        var server = await StartAsync(paging: "daima");

        var kendine_yeten = await Transport().RunAsync(
            Target(server),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        // Sayfalamayı kapatmayan çıplak config komutu — toplayıcının S08
        // öncesindeki ikinci elemanının eşdeğeri.
        var ciplak = await Transport().RunAsync(
            Target(server), ["show"], TestContext.Current.CancellationToken);

        Assert.True(kendine_yeten.Ok, kendine_yeten.Error);
        Assert.True(ciplak.Ok, ciplak.Error);

        Assert.Equal(ProfileConfigLines(FortiGate), ConfigLines(kendine_yeten.Output));

        Assert.True(
            ConfigLines(ciplak.Output) < ProfileConfigLines(FortiGate),
            $"Çıplak `show` da tam config verdi ({ConfigLines(ciplak.Output)} satır) — " +
            "öykünme sayfalamıyor olabilir ve diğer kolun yeşilliği hiçbir şey ifade etmez.");
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> aynı öykünme sayfalama kapalıyken
    /// <b>tam</b> config veriyor.
    ///
    /// <para>
    /// Yukarıdaki iki testin tabanı. Bu olmadan "yarım geldi" iddiası bir
    /// karşılaştırma noktasından yoksun kalırdı ve öykünmenin config'i hiç
    /// basamadığı hâl ile ayırt edilemezdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sayfalama_kapaliyken_config_tam_geliyor()
    {
        var server = await StartAsync(paging: "kapali");

        var result = await Transport().RunAsync(
            Target(server),
            new FortiGateCollector().Commands,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.DoesNotContain("--More--", result.Output, StringComparison.Ordinal);
        Assert.Equal(ProfileConfigLines(FortiGate), ConfigLines(result.Output));
    }

    // -------------------------------------------------- vendor hata mesajları

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> vendor'ın kendi hata metni
    /// <see cref="DeviceCommandResult"/>'a <b>ulaşıyor</b> ve başarısızlık türü
    /// <see cref="DeviceFailureKind.CommandRejected"/>.
    ///
    /// <para>
    /// S06'nın ikinci kabul kriteri. Önceden yalnızca çıkış kodu taşınıyordu
    /// (<c>"… 127 koduyla döndü"</c>) ve cihazın cümlesi okunmadan atılıyordu —
    /// yani <i>"komutu yanlış yazdık"</i> ile <i>"cihaz kilitlendi"</i> operatöre
    /// aynı görünüyordu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Vendor_hata_mesaji_sonuca_tasiniyor()
    {
        var server = await StartAsync();

        var result = await Transport().RunAsync(
            Target(server), ["get system stotus"], TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(DeviceFailureKind.CommandRejected, result.Failure);

        // FortiGate'in KENDİ biçimi — bizim ürettiğimiz genel bir ret değil.
        Assert.Contains("command parse error", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> "komut yanlış" ile "cihaz cevap
    /// vermedi" <b>iki ayrı değer</b>.
    ///
    /// <para>
    /// İkisi de <c>Ok=false</c> üretiyor ve S06'nın kabul kriteri tam olarak
    /// bunların tek değere inmemesini istiyor. Ayrım metinde değil
    /// <see cref="DeviceFailureKind"/>'da: hata cümlesi Türkçe ve bir gün
    /// düzeltilecek, teşhis ona bağlanmamalı.
    /// </para>
    ///
    /// <para>
    /// Ulaşılamayan uç için <b>kapalı bir port</b> kullanılıyor, var olmayan bir
    /// host adı değil: DNS çözümlemesi ortamdan ortama farklı hata sınıfı
    /// üretiyor ve test CI ile yerel arasında sessizce ayrışırdı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Komut_reddi_ile_ulasilamama_ayri_degerler()
    {
        var server = await StartAsync();

        var reddedildi = await Transport().RunAsync(
            Target(server), ["get system stotus"], TestContext.Current.CancellationToken);

        var target = Target(server);

        var ulasilamadi = await Transport().RunAsync(
            new DeviceTarget
            {
                Vendor = target.Vendor,
                Host = target.Host,

                // Konteynerin yayımlamadığı bir port: bağlantı reddediliyor.
                Port = target.Port + 1,
                Username = target.Username,
                Credential = target.Credential,
                Timeout = TimeSpan.FromSeconds(10),
            },
            ["get system status"],
            TestContext.Current.CancellationToken);

        Assert.False(reddedildi.Ok);
        Assert.False(ulasilamadi.Ok);

        Assert.Equal(DeviceFailureKind.CommandRejected, reddedildi.Failure);
        Assert.Equal(DeviceFailureKind.Unreachable, ulasilamadi.Failure);
        Assert.NotEqual(reddedildi.Failure, ulasilamadi.Failure);
    }

    // ----------------------------------------------------- etkileşimli kabuk

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> cihaz artık <b>kendi kabuğunu</b>
    /// veriyor — prompt basıyor, komut alıyor, sayfalıyor.
    ///
    /// <para>
    /// S03 kabuk isteğini reddediyordu ve gerekçesi Linux kabuğu içindi; hâlâ
    /// geçerli (<c>ls</c> çalışmıyor). S06 vendor'ın kendi kabuğunu açıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Ürün bu yolu bugün kullanmıyor</b> — <see cref="SshDeviceTransport"/>
    /// exec kanalı açıyor. Test yine de burada, çünkü ticket'ın açtığı yüzey bu
    /// ve bir gün REST'siz bir vendor için kabuk yolu gerekirse zemini ölçülmüş
    /// olacak.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Etkilesimli_kabuk_prompt_basiyor()
    {
        var server = await StartAsync();

        var transcript = await ShellAsync(server, ["show"], "fw-ankara-01 #", TimeSpan.FromSeconds(20));

        Assert.Contains("N3 öykünmesi", transcript, StringComparison.Ordinal);

        // FortiGate promptu: `hostname # `. Hostname PROFİLDEN geliyor, elle
        // yazılmıyor — ikinci bir kopya olurdu.
        Assert.Contains("fw-ankara-01 #", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> etkileşimli oturumda sayfalama
    /// <b>varsayılan olarak açık</b> ve <c>--More--</c> basılıyor; toplayıcının
    /// hazırlık komutu <b>aynı oturumda</b> gönderildiğinde sayfalama gerçekten
    /// kapanıyor.
    ///
    /// <para>
    /// İkinci yarısı bulgunun karşı kutbu: hazırlık komutu <b>anlamsız değil</b>,
    /// yalnızca exec kanalında taşınmıyor. Aynı oturumda gönderildiğinde
    /// çalışıyor — yani kusur toplayıcının komutunda değil, komutu <b>nasıl</b>
    /// gönderdiğinde.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Etkilesimli_oturumda_sayfalama_kapatma_calisiyor()
    {
        var server = await StartAsync();

        var sayfali = await ShellAsync(server, ["show"], "--More--", TimeSpan.FromSeconds(20));

        Assert.Contains("--More--", sayfali, StringComparison.Ordinal);

        // Toplayıcının FortiGate hazırlık dizesi — üç satır, AYNI oturumda.
        // Sayfalama kapalıyken çıktı SONUNA KADAR geliyor, yani oturum
        // prompta dönüyor. Beklenen işaret o: `--More--`'ın YOKLUĞUNU beklemek
        // mümkün değil, ama tam çıktının varlığını beklemek mümkün.
        var kapatilmis = await ShellAsync(
            server,
            ["config system console", "set output standard", "end", "show"],
            LastConfigLine(FortiGate),
            TimeSpan.FromSeconds(20));

        Assert.DoesNotContain("--More--", kapatilmis, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> Cisco ailesinde <c>enable</c>
    /// promptu <c>&gt;</c>'den <c>#</c>'e çeviriyor.
    ///
    /// <para>
    /// Ayrı bir profil (<c>asa-dc-01</c>) ile koşuyor: prompt ailesi vendor'a
    /// bağlı ve FortiGate'te <c>enable</c> diye bir şey yok. Tek profille
    /// yazılsaydı test, öykünmenin vendor ayrımını hiç sınamazdı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cisco_enable_modu_promptu_degistiriyor()
    {
        var server = await StartAsync(profile: CiscoAsa);

        var transcript = await ShellAsync(server, ["enable"], "asa-dc-01#", TimeSpan.FromSeconds(20));

        Assert.Contains("asa-dc-01>", transcript, StringComparison.Ordinal);
        Assert.Contains("asa-dc-01#", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> bilinmeyen komuta vendor'ın kendi
    /// metni, <b>etkileşimli oturumda da</b>.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Etkilesimli_kabukta_vendor_hata_metni_basiliyor()
    {
        var server = await StartAsync(profile: CiscoAsa);

        var transcript = await ShellAsync(server, ["shwo run"], "% Invalid input detected", TimeSpan.FromSeconds(20));

        Assert.Contains("% Invalid input detected", transcript, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- senaryo seçimi

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> <c>SIM_SCENARIO</c> container'a
    /// <b>ulaşıyor</b> ve seçilen senaryonun config'i geliyor.
    ///
    /// <para>
    /// <b>Bu test S06 öncesinde KIRMIZI yanardı.</b> OpenSSH oturum çocuğuna
    /// temiz bir ortam kuruyor; <c>SIM_*</c> değişkenleri <c>ForceCommand</c>'a
    /// ulaşmıyor. S03 bunu profil için biliyordu (dosyaya yazıyordu) ama senaryo
    /// ortamdan okunuyordu — yani <b>senaryo seçimi sessizce yok sayılıyor</b>,
    /// dağıtıcı her koşumda baseline veriyordu. Compose satırı bunu yaptığını
    /// söylüyordu; yapmıyordu.
    /// </para>
    ///
    /// <para>
    /// Hiçbir test <c>SIM_SCENARIO</c> ayarlamadığı için ayrışma görünmüyordu.
    /// Ayarların taşındığını Docker'sız sınayan bekçi
    /// <c>SshSimulatorCliTests.Dagiticinin_okudugu_her_ayar_acilista_yaziliyor</c>.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Senaryo_secimi_container_a_ulasiyor()
    {
        var baseline = await StartAsync(paging: "kapali");
        var senaryolu = await StartAsync(paging: "kapali", scenario: "kural-eklendi");

        var commands = new FortiGateCollector().Commands;

        var once = await Transport().RunAsync(
            Target(baseline), commands, TestContext.Current.CancellationToken);

        var sonra = await Transport().RunAsync(
            Target(senaryolu), commands, TestContext.Current.CancellationToken);

        Assert.True(once.Ok, once.Error);
        Assert.True(sonra.Ok, sonra.Error);

        // `kural-eklendi` baseline'dan 7 satır uzun. Eşitlik, senaryonun
        // sessizce baseline'a düştüğü anlamına gelir.
        Assert.NotEqual(once.Output, sonra.Output);
        Assert.True(ConfigLines(sonra.Output) > ConfigLines(once.Output));
    }

    // -------------------------------------------------------------- yardımcı

    /// <summary>
    /// Etkileşimli bir kabuk oturumu açıp verilen satırları yazıyor, dökümü
    /// döndürüyor.
    ///
    /// <para>
    /// <see cref="SshDeviceTransport"/> kullanılmıyor ve kullanılamaz: ürünün
    /// taşıyıcısı exec kanalı açıyor, kabuk değil. Kabuk yolunu sınamak için
    /// istemci burada elle kuruluyor — ve bu, ürünün bu yüzeyi <b>bugün
    /// kullanmadığının</b> da kaydı.
    /// </para>
    ///
    /// <para>
    /// <paramref name="budget"/> bir ÖLÇÜT değil, oturumun tıkanıp koşumu
    /// süresiz kilitlememesi için bir tavan: hiçbir iddia süreye bakmıyor (§6).
    /// </para>
    /// </summary>
    /// <summary>
    /// Etkileşimli bir kabuk oturumu açıp verilen satırları yazıyor ve
    /// <paramref name="expected"/> görünene kadar <b>bekliyor</b>.
    ///
    /// <para>
    /// <b>İlk hâli hiçbir şey okumuyordu ve CI'da dört test boş dize gördü.</b>
    /// Sebep: <c>ShellStream.Read()</c> <b>bloke olmayan</b> bir çağrı —
    /// tamponda o an ne varsa onu döndürüyor. Komut yazıldıktan hemen sonra
    /// sunucu daha cevap vermemiş oluyor, <c>Read()</c> boş dönüyor ve döngü
    /// ilk turda kırılıyordu. Testler container'ı, betikleri ve imajı değil,
    /// <b>kendi okuma hatalarını</b> ölçüyordu.
    /// </para>
    ///
    /// <para>
    /// <c>Expect</c> ile bekleniyor ve bu bir <b>anlamsal</b> bekleme, sabit
    /// bir uyku değil: <i>"şu metin gelene kadar"</i>. §6'nın kuralı —
    /// <i>bir testin geçme sebebinin duvar saatiyle ilgisi olmamalı</i> — bu
    /// yüzden korunuyor; <paramref name="budget"/> bir ölçüt değil, oturum
    /// tıkandığında koşumu süresiz kilitlememek için bir tavan.
    /// </para>
    ///
    /// <para>
    /// Zaman aşımında <b>okunan her şey döndürülüyor</b>, istisna atılmıyor:
    /// iddia o zaman gerçek içerikle düşüyor ve arıza <i>"beklenen gelmedi,
    /// gelen şu"</i> diye okunuyor. İstisna atılsaydı hata mesajı oturumun ne
    /// bastığını hiç göstermezdi — CI'da kaybettiğim tur tam olarak buydu.
    /// </para>
    /// </summary>
    private static async Task<string> ShellAsync(
        IContainer server,
        IReadOnlyList<string> lines,
        string expected,
        TimeSpan budget)
    {
        using var client = new SshClient(
            server.Hostname, server.GetMappedPublicPort(22), Username, Password);

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        using var shell = client.CreateShellStream("vt100", 80, 24, 800, 600, 4096);

        var transcript = new StringBuilder();

        // Açılış banner'ı ve ilk prompt, ilk komut yazılmadan ÖNCE bekleniyor:
        // öykünme hazır olmadan yazılan satır kaybolabilir.
        transcript.Append(shell.Expect(BannerMarker, budget) ?? string.Empty);

        foreach (var line in lines)
        {
            shell.WriteLine(line);
            await shell.FlushAsync(TestContext.Current.CancellationToken);
        }

        transcript.Append(shell.Expect(expected, budget) ?? string.Empty);

        // Beklenen geldikten sonra tamponda kalanlar da alınıyor: `--More--`
        // sonrası basılan satırlar burada görünüyor.
        transcript.Append(shell.Read());

        return transcript.ToString();
    }

    /// <summary>
    /// Öykünmenin açılışta bastığı ilk satırın değişmez parçası — oturumun
    /// gerçekten kurulduğunu anlamanın işareti.
    /// </summary>
    private const string BannerMarker = "N3 öykünmesi";
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// N3 CLI öykünmesinin bekçileri — <b>Docker olmadan</b> (S06).
///
/// <para>
/// <b>Neden birim paketinde:</b> §2'nin ekseni "hangi paket" değil "konteyner
/// gerekiyor mu". <c>vendor-cli</c> saf bash ve tek başına kaynak alınabiliyor;
/// öykünmenin komut sınıflandırmasını ve sayfalamasını sınamak için sshd'ye,
/// imaja ya da ağa ihtiyaç yok. Konteyner isteyen kısım — gerçek bir SSH
/// oturumunun bu betiklere ulaşması — <see cref="Bizigo.IntegrationTests"/>
/// tarafında ve koordinatör koşturuyor.
/// </para>
///
/// <para>
/// <b>Betikler C#'a kopyalanmıyor, ÇALIŞTIRILIYOR.</b> Sınıflandırma kurallarını
/// burada ikinci kez yazmak §9'un yasakladığı kopya olurdu ve ayrışma tam olarak
/// bu testin sessizce yeşil kaldığı yerde doğardı: betik değişir, testteki kopya
/// eski kuralı sınamaya devam eder.
/// </para>
/// </summary>
public sealed class SshSimulatorCliTests
{
    private static string Script(string name) =>
        Path.Combine(RepositoryLayout.SshSimDirectory, name);

    /// <summary>
    /// <c>vendor-cli</c>'yi kaynak alıp tek bir fonksiyonu koşturur.
    ///
    /// <para>
    /// <c>bash</c> yoksa test <b>düşüyor</b>, atlanmıyor: atlanan bir test bu
    /// depoda kanıt sayılmıyor (§2) ve sessizce atlayan bir bekçi, bekçinin
    /// kendisinden tehlikeli (§7).
    /// </para>
    /// </summary>
    private static (string Output, int ExitCode) Bash(
        string body,
        string? stdin = null,
        params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepositoryLayout.SshSimDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(". ./vendor-cli.sh\n" + body);

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("bash başlatılamadı — S06 bekçileri bash gerektiriyor.");

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
        }

        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(milliseconds: 30_000);

        return (output, process.ExitCode);
    }

    // ------------------------------------------------- ayarların taşınması

    /// <summary>
    /// <b>Dağıtıcının okuduğu her <c>SIM_*</c> ayarı açılışta dosyaya
    /// yazılıyor.</b>
    ///
    /// <para>
    /// Ölçülmüş bir kusurun bekçisi: OpenSSH oturum çocuğuna temiz bir ortam
    /// kuruyor ve <c>SIM_*</c> gibi keyfi değişkenler sshd'nin ortamında dursa
    /// bile <c>ForceCommand</c>'a ulaşmıyor. S03 bunu profil için biliyordu
    /// (dosyaya yazıyordu) ama <c>SIM_SCENARIO</c> ortamdan okunuyordu ve
    /// compose onu ayarlıyordu — yani <b>senaryo seçimi sessizce yok
    /// sayılıyordu</b>: dağıtıcı her koşumda baseline veriyor, hata yok, sayaç
    /// yok, belirti yok. Fark testleri "değişiklik yok" sonucunu doğru sanardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Dagiticinin_okudugu_her_ayar_acilista_yaziliyor()
    {
        var consumed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var script in new[] { "komut-dagitici.sh", "cli-oykunmesi.sh", "vendor-cli.sh" })
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(Script(script)),
                @"\$\{(SIM_[A-Z_]+)"))
            {
                consumed.Add(match.Groups[1].Value);
            }
        }

        // Açılış betiğinin `/sim-ayarlar` dosyasına yazdıkları.
        var entrypoint = File.ReadAllText(Script("entrypoint.sh"));
        var heredoc = Regex.Match(entrypoint, @"<<AYAR\n(.*?)\nAYAR", RegexOptions.Singleline);

        Assert.True(heredoc.Success, "entrypoint.sh içinde `/sim-ayarlar` heredoc'u bulunamadı.");

        var written = Regex.Matches(heredoc.Groups[1].Value, @"^(SIM_[A-Z_]+)=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(consumed);

        var missing = consumed.Where(name => !written.Contains(name)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"Dağıtıcı şu ayarları okuyor ama açılış onları /sim-ayarlar'a yazmıyor: {string.Join(", ", missing)}. " +
            "OpenSSH bu değişkenleri ForceCommand'a taşımıyor; yazılmayan ayar SESSİZCE boş kalır.");
    }

    // ----------------------------------------- ürünün komutları ↔ öykünme

    /// <summary>
    /// <b>Toplayıcının gönderdiği hiçbir komut öykünmeye "bilinmeyen"
    /// düşmüyor.</b>
    ///
    /// <para>
    /// Komut tablosu simülatörde tekrarlanmıyor (S03 kararı) ama tanınma
    /// desenleri orada duruyor. Ürün bir komutu değiştirdiğinde desen eskisini
    /// tanımaya devam ederse, çekim testi <i>"cihaz cevap verdi"</i> diye yeşil
    /// kalır ve gerçek cihazda kırılır. Bu bekçi o ayrışmayı Docker'sız
    /// yakalıyor.
    /// </para>
    ///
    /// <para>
    /// Toplayıcılar <b>yansımayla</b> bulunuyor: yeni bir vendor eklendiğinde
    /// bu testin listesi elle güncellenmek zorunda olsaydı, elle tutulan liste
    /// bekçiyi körleştirirdi — bu depoda adı konmuş bir hata sınıfı.
    /// </para>
    /// </summary>
    [Fact]
    public void Toplayicilarin_gonderdigi_her_komut_oykunme_tarafindan_taniniyor()
    {
        var collectors = typeof(IConfigCollector).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsAssignableTo(typeof(IConfigCollector)))
            .Select(t => (IConfigCollector)Activator.CreateInstance(t)!)
            .ToArray();

        Assert.NotEmpty(collectors);

        foreach (var collector in collectors)
        {
            foreach (var command in collector.Commands)
            {
                var (kind, _) = Bash($"cli_komut_turu \"$KOMUT\"", environment: ("KOMUT", command));

                Assert.False(
                    kind.Trim() is "bilinmeyen" or "",
                    $"{collector.Vendor}: '{command}' komutunu N3 öykünmesi tanımıyor " +
                    $"(sonuç: '{kind.Trim()}'). deploy/ssh-sim/vendor-cli.sh · cli_komut_turu");
            }
        }
    }

    /// <summary>
    /// Hazırlık komutu ile config komutu <b>ayrı sınıflar</b>.
    ///
    /// <para>
    /// İkisi tek sınıfa düşseydi öykünme sayfalama kapatma komutuna config
    /// basardı ve toplayıcı iki kez config almış olurdu — fark motoru için
    /// görünmez, ama <c>terminal pager 0</c>'ın hiç uygulanmadığını da
    /// gizlerdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("show", "config")]
    [InlineData("more system:running-config", "config")]
    [InlineData("/export terse", "config")]
    [InlineData("terminal pager 0", "sayfalama-kapat")]
    [InlineData("terminal length 0", "sayfalama-kapat")]
    [InlineData("config system console", "sayfalama-baglam")]
    [InlineData("set output standard", "sayfalama-kapat")]
    [InlineData("end", "baglam-bitir")]
    [InlineData("enable", "enable")]
    [InlineData("ls -la /", "bilinmeyen")]
    public void Komut_siniflandirmasi(string command, string expected)
    {
        var (kind, _) = Bash("cli_komut_turu \"$KOMUT\"", environment: ("KOMUT", command));

        Assert.Equal(expected, kind.Trim());
    }

    // ------------------------------------------------------------ sayfalama

    /// <summary>
    /// <b>Sayfalama kapalıyken config tam geliyor.</b> Ölçümün tabanı: aşağıdaki
    /// kesilme testinin bir şey ifade edebilmesi için "kesilmemiş hâl" ölçülmüş
    /// olmalı.
    /// </summary>
    [Fact]
    public void Sayfalama_kapaliyken_config_tam_geliyor()
    {
        var config = TempConfig(40);

        try
        {
            var (output, exit) = Bash(
                $"cli_bas \"{config}\" kapali",
                environment: ("SIM_PAGE_LINES", "10"));

            Assert.Equal(0, exit);
            Assert.Equal(40, Lines(output).Count(l => l.StartsWith("satir ", StringComparison.Ordinal)));
            Assert.DoesNotContain("--More--", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    /// <summary>
    /// <b>S06'nın taşıyıcı cümlesi, ölçülmüş hâli.</b>
    ///
    /// <para>
    /// Sayfalama açıkken ve <c>--More--</c>'a kimse cevap vermediğinde cihaz
    /// çıktının geri kalanını <b>hiç basmıyor</b> ve çıkış kodu yine sıfır.
    /// Okuyan taraf yarım config'i BAŞARILI bir çekim olarak alıyor: hata yok,
    /// sayaç yok, belirti yok — §7'nin sınıfı.
    /// </para>
    ///
    /// <para>
    /// <b>Duvar saati ölçüt değil</b> (§6): iddia edilen şey süre değil,
    /// çıktının satır sayısı ve içinde <c>--More--</c> imleci olması. Girdi
    /// hemen kapanıyor, yani bekleme bile yaşanmıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sayfalama_acikken_cevapsiz_More_ciktiyi_sessizce_kesiyor()
    {
        var config = TempConfig(40);

        try
        {
            var (output, exit) = Bash(
                $"cli_bas \"{config}\" acik",
                stdin: string.Empty,
                environment: [("SIM_PAGE_LINES", "10"), ("SIM_MORE_TIMEOUT", "1")]);

            // Çıkış kodu SIFIR: sessizliğin kaynağı burası. Cihaz "hata" demiyor.
            Assert.Equal(0, exit);

            Assert.Equal(10, Lines(output).Count(l => l.StartsWith("satir ", StringComparison.Ordinal)));
            Assert.Contains("--More--", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // -------------------------------------------------------- vendor metni

    /// <summary>
    /// Üç vendor'ın hata metni <b>birbirinden ayrı</b> ve her biri kendi
    /// biçiminde. Genel tek bir ret, "komut yanlış"ı "cihaz cevap vermedi"den
    /// ayırt edilemez kılardı (S06 kabul kriteri).
    /// </summary>
    [Theory]
    [InlineData("cisco", "% Invalid input detected")]
    [InlineData("fortinet", "command parse error")]
    [InlineData("mikrotik", "bad command name")]
    public void Vendor_kendi_hata_metnini_basiyor(string vendor, string expected)
    {
        var (output, _) = Bash($"cli_hata {vendor} \"sh runn\"");

        Assert.Contains(expected, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>SSH yüzeyi olan her profilin vendor'ı öykünmede tanımlı.</b>
    ///
    /// <para>
    /// Tanımsız bir vendor için <c>cli_hata</c> <b>hiçbir şey basmıyor</b> —
    /// yani cihaz bilinmeyen komuta sessiz kalırdı. Açılış bu yüzden patlıyor
    /// (<c>entrypoint.sh</c>), ve bu bekçi patlamanın hangi profilde
    /// gerçekleşeceğini <b>container açmadan</b> söylüyor.
    /// </para>
    ///
    /// <para>
    /// <c>lb-web-01</c> (nginx) kapsam dışı ve bu şemanın bir özelliği: her
    /// profilin her yüzeyi taklit etmek zorunda değil.
    /// </para>
    /// </summary>
    [Fact]
    public void Ssh_yuzeyi_olan_her_profilin_vendoru_oykunmede_tanimli()
    {
        var (known, _) = Bash("printf '%s' \"$CLI_VENDORS\"");
        var vendors = known.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.NotEmpty(vendors);

        var profiles = SimulatorProfileStore.LoadAll(
            Path.Combine(RepositoryLayout.Root, "catalog", "simulators"),
            RepositoryLayout.Root);

        var withSsh = profiles
            .Select(r => r.Profile)
            .Where(p => p.Ssh is not null)
            .ToArray();

        Assert.NotEmpty(withSsh);

        foreach (var profile in withSsh)
        {
            Assert.True(
                vendors.Contains(profile.Vendor, StringComparer.Ordinal),
                $"'{profile.Id}' profilinin vendor'ı '{profile.Vendor}' N3 öykünmesinde tanımlı değil. " +
                $"Tanınanlar: {string.Join(", ", vendors)}. deploy/ssh-sim/vendor-cli.sh · CLI_VENDORS");
        }
    }

    // -------------------------------------------------------------- yardımcı

    private static string TempConfig(int lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bizigo-n3-{Guid.NewGuid():N}.conf");

        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => $"satir {i}"));

        return path;
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

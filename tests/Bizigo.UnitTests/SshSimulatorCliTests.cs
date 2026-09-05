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
    ///
    /// <para>
    /// <b>stderr AYRI döndürülüyor</b> ve bu bir düzeltme (S09).
    ///
    /// <para>
    /// İlk hâli yalnızca stdout okuyup stderr'i atıyordu. Ölçülen sonucu:
    /// S08'de durum makinesini çıkarırken vendor hata metninin <c>&gt;&amp;2</c>
    /// yönlendirmesi düştü, metin stdout'a kaydı, ve <b>hiçbir bekçi görmedi</b>
    /// — çünkü hepsi iki akışı da aynı yerden okuyormuş gibi davranıyordu.
    /// Ürün tarafında <c>DeviceCommandResult.Error</c> stderr'den besleniyor,
    /// yani ayrım <b>ölçülen bir şey</b> ve bekçinin körlüğü onu görünmez
    /// kılmıştı.
    /// </para>
    /// </summary>
    private static (string Output, string Error, int ExitCode) Bash(
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

        // stdout ÖNCE tamamen okunuyor, sonra stderr: ikisini sırayla okumak
        // boru dolduğunda kilitlenebilir, o yüzden stderr bir göreve alınıyor.
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        var error = errorTask.GetAwaiter().GetResult();

        process.WaitForExit(milliseconds: 30_000);

        return (output, error, process.ExitCode);
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
        Assert.NotEmpty(Collectors);

        foreach (var collector in Collectors)
        {
            // SATIR SATIR sınıflandırılıyor, komut dizesi bütün olarak değil
            // (S08). Toplayıcı komutları artık çok satırlı ve kendi kendine
            // yetiyor; bütün dizeyi tek bir `case`'e vermek ilk eşleşen desende
            // durup GERİ KALAN SATIRLARI HİÇ SINAMAMAK olurdu — ve test yeşil
            // kalırdı. Öykünmenin exec yolu da satır satır işliyor.
            foreach (var line in collector.Commands.SelectMany(Lines))
            {
                var (kind, _, _) = Bash("cli_komut_turu \"$KOMUT\"", environment: ("KOMUT", line));

                Assert.False(
                    kind.Trim() is "bilinmeyen" or "",
                    $"{collector.Vendor}: '{line}' satırını N3 öykünmesi tanımıyor " +
                    $"(sonuç: '{kind.Trim()}'). deploy/ssh-sim/vendor-cli.sh · cli_komut_turu");
            }
        }
    }

    /// <summary>
    /// <b>Vendor hata metni STDERR'e gidiyor, stdout'a değil</b> (S09).
    ///
    /// <para>
    /// <b>Ölçülmüş bir kusurun bekçisi ve kusuru ben yaptım.</b> S08'de durum
    /// makinesini ortak bir fonksiyona çıkarırken <c>cli_hata … &gt;&amp;2</c>
    /// yönlendirmesi düştü. <see cref="SshDeviceTransport"/>
    /// <c>DeviceCommandResult.Error</c>'ı <b>stderr'den</b> besliyor, yani metin
    /// stdout'a kayınca <c>Error</c> boş kaldı, taşıma kendi sentetik cümlesine
    /// düştü (<i>"'…' komutu 127 koduyla döndü"</i>) ve S06'nın ikinci kabul
    /// kriteri — vendor'ın kendi metninin ürüne ulaşması — <b>sessizce
    /// geçersiz oldu</b>. CI'da yakalandı, burada değil.
    /// </para>
    ///
    /// <para>
    /// <b>Neden hiçbir bekçi görmedi:</b> yardımcı yalnızca stdout okuyup
    /// stderr'i atıyordu, yani iki akış onun için tek bir akıştı. Metnin
    /// varlığını sınayan testler geçmeye devam etti — sınadıkları şey ürünün
    /// okuduğu şey değildi. Kapı vardı, <b>yanlış yere bakıyordu</b>.
    /// </para>
    ///
    /// <para>
    /// İddia bu yüzden <b>çift taraflı</b>: metin stderr'de VAR ve stdout'ta
    /// YOK. Yalnızca ilkini iddia etmek aynı körlüğü yeniden üretirdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("fortinet", "command parse error")]
    [InlineData("cisco", "% Invalid input detected")]
    [InlineData("mikrotik", "bad command name")]
    public void Vendor_hata_metni_stderre_gidiyor_stdouta_degil(string vendor, string expected)
    {
        var (output, error, exit) = Session(vendor, "/dev/null", ["taninmayan komut"]);

        Assert.Contains(expected, error, StringComparison.Ordinal);

        // STDOUT TEMİZ. Bu satır olmadan bekçi, metnin İKİ akışa birden
        // yazıldığı hâli de geçirirdi.
        Assert.DoesNotContain(expected, output, StringComparison.Ordinal);

        // Ret çıkış koduna da yansıyor: taşıma önce koda bakıyor.
        Assert.NotEqual(0, exit);
    }

    /// <summary>
    /// <b>Toplayıcının her komutu KENDİ KENDİNE YETİYOR</b> — S08'in düzeltmesi,
    /// Docker'sız (S08).
    ///
    /// <para>
    /// Sözleşme <see cref="IConfigCollector.Commands"/>'da yazılı: her eleman
    /// ayrı bir oturumda koşuyor, yani bir elemanda yazılan sayfalama ayarı bir
    /// sonrakine <b>taşınmıyor</b>. İhlali sessiz: config yarım geliyor,
    /// <c>Ok=true</c>, <c>Error</c> boş.
    /// </para>
    ///
    /// <para>
    /// Bekçi sözleşmeyi <b>metinden okumuyor</b>, öykünmeyi koşturuyor: her
    /// komut, sayfalama AÇIK bir oturumda tek başına işleniyor ve config
    /// okuyan bir komutun çıktısı <b>kesilmemiş</b> olmak zorunda. Yani
    /// toplayıcı yazarı sayfalamayı kapatmayı unutursa ya da ayrı bir elemana
    /// koyarsa, bu test <b>konteyner açmadan</b> kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Toplayicinin_her_komutu_kendi_kendine_yetiyor()
    {
        Assert.NotEmpty(Collectors);

        var config = TempConfig(40);

        try
        {
            foreach (var collector in Collectors)
            {
                // Öykünmenin vendor adı profilden geliyor (`fortinet`), ürünün
                // toplayıcı kimliği ise parser kimliği (`fortinet.fortigate`).
                var vendor = collector.Vendor.Split('.')[0];

                foreach (var command in collector.Commands)
                {
                    var lines = Lines(command);

                    // Config okumayan bir komut (varsa) bu kuralın dışında:
                    // sayfalama yalnızca çıktı basan komutu ilgilendiriyor.
                    if (!lines.Any(l => Kind(l) == "config"))
                    {
                        continue;
                    }

                    var (output, _, exit) = Session(vendor, config, lines);

                    Assert.Equal(0, exit);

                    Assert.Equal(40, Lines(output).Count(l => l.StartsWith("satir ", StringComparison.Ordinal)));

                    Assert.DoesNotContain(
                        "--More--",
                        output,
                        StringComparison.Ordinal);

                    // ÜSTTEKİ İDDİA SAYFALAMAYAN BİR VENDOR'DA BOŞ GEÇER.
                    //
                    // Öykünme yalnızca sayfalamayı kapatan komutunu
                    // modellediğimiz vendor'larda sayfalıyor (RouterOS'ta
                    // ölçmedik, o yüzden susuyor). Susan bir vendor'da
                    // "kesilmemiş çıktı" her zaman doğru — yani yukarıdaki
                    // satırlar orada hiçbir şey ifade etmiyor.
                    //
                    // Bu yüzden asıl sözleşme ayrıca ve DOĞRUDAN iddia
                    // ediliyor: sayfalayan bir vendor'ın config okuyan komutu,
                    // sayfalamayı AYNI komutta kapatmak zorunda.
                    if (PagingVendors.Contains(vendor, StringComparer.Ordinal))
                    {
                        Assert.True(
                            lines.Any(l => Kind(l) == "sayfalama-kapat"),
                            $"{collector.Vendor}: config okuyan komut sayfalamayı AYNI komutta kapatmıyor. " +
                            "Her komut kendi oturumunda koşuyor; ayrı bir elemanda kapatmak hiçbir işe " +
                            "yaramaz ve config YARIM gelir (IConfigCollector.Commands · S08).");
                    }
                }
            }
        }
        finally
        {
            File.Delete(config);
        }
    }

    /// <summary>
    /// <b>Ve öykünme hâlâ sayfalıyor.</b>
    ///
    /// <para>
    /// Yukarıdaki bekçinin tabanı ve onsuz anlamsız: sayfalamayı hiç
    /// uygulamayan bir öykünmede <i>"kesilmemiş çıktı"</i> iddiası her zaman
    /// geçerdi ve S08'in düzeltmesi geri alınsa bile kimse görmezdi. Burada
    /// <b>sayfalama kapatılmadan</b> aynı config okunuyor ve çıktının
    /// kesildiği ölçülüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sayfalama_kapatilmazsa_ayni_komut_yarim_cikti_veriyor()
    {
        var config = TempConfig(40);

        try
        {
            // Toplayıcının komutundan sayfalama kapatma satırları ÇIKARILIYOR;
            // geriye yalnızca config okuyan satır kalıyor.
            var only = Lines(new FortiGateCollector().Commands[0])
                .Where(l => Kind(l) == "config")
                .ToArray();

            Assert.NotEmpty(only);

            var (output, _, exit) = Session("fortinet", config, only);

            // Çıkış kodu SIFIR: sessizliğin kaynağı burası.
            Assert.Equal(0, exit);
            Assert.Contains("--More--", output, StringComparison.Ordinal);

            Assert.Equal(10, Lines(output).Count(l => l.StartsWith("satir ", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(config);
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
        var (kind, _, _) = Bash("cli_komut_turu \"$KOMUT\"", environment: ("KOMUT", command));

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
            var (output, _, exit) = Bash(
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
            var (output, _, exit) = Bash(
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
        var (output, _, _) = Bash($"cli_hata {vendor} \"sh runn\"");

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
        var (known, _, _) = Bash("printf '%s' \"$CLI_VENDORS\"");
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

    /// <summary>
    /// Toplayıcılar <b>yansımayla</b> bulunuyor: yeni bir vendor eklendiğinde
    /// bu testlerin listesi elle güncellenmek zorunda olsaydı, elle tutulan
    /// liste bekçiyi körleştirirdi — bu depoda adı konmuş bir hata sınıfı.
    /// </summary>
    private static readonly IConfigCollector[] Collectors = typeof(IConfigCollector).Assembly
        .GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsAssignableTo(typeof(IConfigCollector)))
        .Select(t => (IConfigCollector)Activator.CreateInstance(t)!)
        .ToArray();

    /// <summary>
    /// Öykünmenin <b>sayfaladığı</b> vendor'lar — betikten okunuyor, burada
    /// ikinci kez yazılmıyor.
    /// </summary>
    private static readonly string[] PagingVendors =
        Bash("printf '%s' \"$CLI_SAYFALAYAN_VENDORLAR\"").Output
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Kind(string command)
    {
        var (kind, _, _) = Bash("cli_komut_turu \"$KOMUT\"", environment: ("KOMUT", command));

        return kind.Trim();
    }

    /// <summary>
    /// Verilen satırları <b>tek bir oturumda</b>, sayfalama AÇIK başlayarak
    /// işler — dağıtıcının exec kanalında yaptığının aynısı.
    /// </summary>
    private static (string Output, string Error, int ExitCode) Session(
        string vendor,
        string configPath,
        IReadOnlyList<string> lines)
    {
        var body = string.Join(
            "\n",
            lines.Select(l => $"cli_komut_isle \"$VENDOR\" \"$CONFIG\" \"{l.Replace("\"", "\\\"", StringComparison.Ordinal)}\""));

        return Bash(
            $"cli_oturum_baslat \"$VENDOR\" acik\n{body}\nexit $CLI_RED",
            stdin: string.Empty,
            environment: [
                ("VENDOR", vendor),
                ("CONFIG", configPath),
                ("SIM_PAGE_LINES", "10"),
                ("SIM_MORE_TIMEOUT", "1"),
            ]);
    }

    private static string TempConfig(int lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bizigo-n3-{Guid.NewGuid():N}.conf");

        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => $"satir {i}"));

        return path;
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

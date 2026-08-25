using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Bizigo.Parsing.Samples;

namespace Bizigo.Simulators;

/// <param name="Lines">Basılan satır sayısı.</param>
/// <param name="Bytes">Tele giden bayt — kodlamadan SONRA.</param>
/// <param name="Elapsed">Geçen süre.</param>
public sealed record EmitResult(int Lines, long Bytes, TimeSpan Elapsed);

/// <summary>
/// <b>S02 — syslog basıcı.</b> Profilin örnek satırlarını collector'a basar.
///
/// <para>
/// Bugün boru hattını besleyen tek araç <c>bizigo seed golden</c> ve o
/// <b>doğrudan ClickHouse'a</b> yazıyor. Yani kodlama tespiti, WAL + fsync, ham
/// arşiv ve dispatcher kademeleri ölçüm verisiyle hiç koşmuyor. Buradan basılan
/// bir satır o yolun tamamından geçiyor.
/// </para>
///
/// <h3>Çerçeveleme: satır sonu, başka bir şey değil</h3>
///
/// <para>
/// Collector'ın syslog alıcısı <c>protocol: none</c> ile koşuyor: PRI ayrıştırma
/// yok, gövde olduğu gibi geçiyor ve satırları <b>satır sonu</b> ayırıyor. Bu
/// yüzden basıcı da RFC 3164/5424 başlığı üretmiyor — üretseydi o başlık gövdenin
/// parçası olarak arşive girerdi ve gerçek cihazın bastığından farklı bir şey
/// ölçerdik.
/// </para>
///
/// <h3>Kodlama: baytlar bilerek "bozuk"</h3>
///
/// <para>
/// Profil <c>encoding: windows-1254</c> diyorsa satır o kod sayfasıyla
/// kodlanıyor ve tele UTF-8 <b>olmayan</b> baytlar çıkıyor. Alıcı tarafta
/// <c>iso-8859-1</c> onları birebir taşıyor, ürün de kendi tespitini yapıyor.
/// Her şeyi UTF-8 basmak, F1'in en pahalı kararlarından birini (K24) hiç
/// sınamamak olurdu.
/// </para>
///
/// <h3>Zaman damgası ŞİMDİYE kaydırılıyor</h3>
///
/// <para>
/// Örnek dosyalar gerçek vendor örnekleri ve 2015–2022 tarihleri taşıyor.
/// Olduğu gibi basmak, saati altı yıl geride kalmış bir cihazı simüle etmek
/// olurdu — ve <c>events</c> tablosunun 90 günlük TTL'i o satırları
/// <b>yazıldı denildikten sonra</b> sessizce siliyor. Ölçüldü: 100 satır,
/// <c>processed 100</c>, INSERT başarılı, tabloda sıfır satır.
/// </para>
///
/// <para>
/// Kaydırıcı <see cref="SampleTimeRewriter"/> — <b>yeni yazılmadı</b>,
/// <c>GoldenSampleSeeder</c> zaten kullanıyordu ve ortak eve taşındı (§9).
/// <c>Rewritten: false</c> dönmesi hata değil: zaman damgası taşımayan ASA
/// satırında ürün alınma zamanına düşüyor, üretimde de öyle oluyor. Ürün
/// tarafındaki sayaç <c>ClickHouseEventSink.Expired</c>.
/// </para>
///
/// <h3>Bu basıcının SINIRI — cihaz kimliği</h3>
///
/// <para>
/// Ürün kaynak kimliğini <c>net.peer.ip</c>'den çözüyor
/// (<c>OtlpLogsDecoder.SourceKeyCandidates</c>) ve syslog alıcısı öznitelikleri
/// <b>alıcı başına</b> sabit veriyor, cihaz başına değil. Dolayısıyla aynı
/// makineden basan beş profil ürüne <b>tek kaynak</b> gibi görünür.
/// </para>
///
/// <para>
/// Bu, filonun kapsam yayılımını (K17) yok eder. Çözümü her cihazın kendi
/// IP'sinden basması, yani her profilin <b>kendi container'ı</b> — S02'nin
/// container adımı bir konvansiyon değil, bu mekanizmanın zorunlu kıldığı şey.
/// Tek makineden çok profil basmak <b>boru hattını</b> sınar, <b>kapsamı</b>
/// sınamaz; ikisini karıştıran bir ölçüm yanlış sayı üretir.
/// </para>
/// </summary>
public static class SyslogEmitter
{
    /// <summary>
    /// <c>saat-kaymasi</c> senaryosunun ileri kaydırdığı süre.
    ///
    /// <para>
    /// ⚠ <b>İşaretli sabit:</b> seçildi, ölçülmedi. Ölçüt "kaç dakika" değil,
    /// ürünün kaymayı <b>görünür</b> kılıp kılmadığı — yani bu sayının doğru
    /// değeri, kaymanın ürünün pencere mantığını aşacak kadar büyük ve
    /// olayı tümden görünmez yapmayacak kadar küçük olması. İkisinin arası
    /// ölçülmedi.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SkewAhead = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Profilin örneklerini basar.
    /// </summary>
    /// <param name="profile">Basılacak cihaz.</param>
    /// <param name="repositoryRoot">Örnek yollarının çözüleceği kök.</param>
    /// <param name="host">Collector adresi.</param>
    /// <param name="count">Basılacak satır sayısı; örnekler döngüye alınıyor.</param>
    /// <param name="scenario">
    /// Basıcı tarafındaki adlandırılmış geçiş (S04). Boş ya da
    /// <c>baseline</c> ise değişim yok.
    /// </param>
    public static async Task<EmitResult> EmitAsync(
        SimulatorProfile profile,
        string repositoryRoot,
        string host,
        int count,
        CancellationToken cancellationToken = default,
        string? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (profile.Syslog is null || profile.Syslog.Samples.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{profile.Id}' profilinin syslog yüzeyi yok. Basılacak örnek tanımlanmamış.");
        }

        var lines = ReadSamples(profile, repositoryRoot);

        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{profile.Id}' örnek dosyalarında hiç satır yok.");
        }

        // YÜZEY KONTROLÜ ÖNCE (S04): `kural-eklendi` config senaryosu ve
        // basıcıya verilirse hiçbir şey yapmaz. Sessizce yok saymak, senaryonun
        // koştuğu sanılan bir test bırakırdı — bu fazın kaçındığı tam olarak o.
        if (Scenarios.Reject(scenario, ScenarioSurface.Syslog) is { } yuzeyHatasi)
        {
            throw new InvalidOperationException(yuzeyHatasi);
        }

        var applied = Scenarios.Find(scenario);

        // `bozuk-kodlama`: profil UTF-8 diyor, tel latin-1 gidiyor.
        //
        // Kodlamayı BOZMAK, örnek dosyayı bozmakla aynı şey değil: dosya
        // olduğu gibi kalıyor ve yalnızca tele yazılırken başka bir kod
        // sayfasıyla yazılıyor. Ürünün karşılaştığı gerçek durum bu — cihaz
        // doğru metni yanlış kodlamayla basıyor.
        var encoding = applied?.Name == "bozuk-kodlama"
            ? System.Text.Encoding.Latin1
            : ResolveEncoding(profile.Encoding);

        // `saat-kaymasi`: damga ileri kayıyor.
        //
        // Kayma İLERİ, geri değil: geri kayan bir damga pencerenin dışına
        // düşüp olayı görünmez yapardı ve test "kayma yakalanmadı" ile
        // "olay hiç gelmedi"yi ayırt edemezdi.
        var skew = applied?.Name == "saat-kaymasi"
            ? SkewAhead
            : TimeSpan.Zero;
        var udp = string.Equals(profile.Syslog.Transport, "udp", StringComparison.OrdinalIgnoreCase);
        var port = udp ? 5141 : 5140;

        // Hız profilden; sıfır ya da negatifse beklemeden basıyoruz. Bekleme
        // duvar saatine bağlı ve bu ölçüm ONU istiyor: cihaz gerçekten bir
        // hızda basıyor ve backpressure ancak hızla görünür.
        var delay = profile.Syslog.RatePerMinute > 0
            ? TimeSpan.FromMinutes(1.0 / profile.Syslog.RatePerMinute)
            : TimeSpan.Zero;

        var clock = Stopwatch.StartNew();

        return udp
            ? await EmitUdpAsync(lines, encoding, host, port, count, delay, skew, clock, cancellationToken)
            : await EmitTcpAsync(lines, encoding, host, port, count, delay, skew, clock, cancellationToken);
    }

    /// <summary>
    /// <b>Tele giden satır.</b> TCP ve UDP kolları ikisi de burayı çağırıyor.
    ///
    /// <para>
    /// Ayrı bir metot olmasının sebebi ölçüldü: kaydırma iki kolda ayrı ayrı
    /// yazılıyken bekçi <see cref="SampleTimeRewriter"/>'ı doğrudan çağırıyordu
    /// ve basıcının onu <b>kullandığını hiç sınamıyordu</b> — kaydırma
    /// basıcıdan kaldırıldığında test yeşil kalıyordu. Yani bekçi, korumayı
    /// iddia ettiği şeyi korumuyordu (§7).
    /// </para>
    ///
    /// <para>
    /// Şimdi tek giriş var ve bekçi buradan geçiyor: kaydırma kaldırılırsa
    /// kırmızı yanıyor, ölçüldü.
    /// </para>
    /// </summary>
    public static string WireLine(string sample, DateTimeOffset at) =>
        SampleTimeRewriter.Rewrite(sample, at).Text;

    private static async Task<EmitResult> EmitTcpAsync(
        IReadOnlyList<string> lines,
        Encoding encoding,
        string host,
        int port,
        int count,
        TimeSpan delay,
        TimeSpan skew,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);

        await using var stream = client.GetStream();

        long bytes = 0;

        for (var i = 0; i < count; i++)
        {
            // Satır sonu ASCII: kodlama gövdeyi etkiliyor, ayırıcıyı değil.
            // Damga BASILDIĞI AN alınıyor — gerçek cihaz da öyle yapıyor.
            var payload = encoding.GetBytes(
                WireLine(lines[i % lines.Count], DateTimeOffset.UtcNow + skew));

            await stream.WriteAsync(payload, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);

            bytes += payload.Length + 1;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        await stream.FlushAsync(cancellationToken);

        // YARIM KAPATMA, sonra bekleme. İkisi de zorunlu ve ikisi de ölçülerek
        // öğrenildi: son yazımdan hemen sonra soketi kapatmak gönderilmemiş
        // veriyi RST ile düşürüyordu.
        //
        // Belirtisi bu deponun en pahalı sınıfındandı: basıcı "5 satır, 3334
        // bayt yazdım" diyordu ve ClickHouse'a SIFIR satır ulaşıyordu. Hiçbir
        // hata yok, hiçbir sayaç yok — yalnızca olmayan veri. Aynı beş satır ham
        // soketle, `shutdown(SEND)` + bekleme ile gönderildiğinde beşi de
        // ulaşıyordu; değişken tek başına kapatma biçimiydi.
        //
        // `Shutdown(Send)` FIN gönderiyor: alıcı akışın bittiğini görüyor ve
        // tamponundakini işliyor. Bekleme, collector'ın son satırı işlemesine
        // zaman tanıyor.
        client.Client.Shutdown(SocketShutdown.Send);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        return new EmitResult(count, bytes, clock.Elapsed);
    }

    private static async Task<EmitResult> EmitUdpAsync(
        IReadOnlyList<string> lines,
        Encoding encoding,
        string host,
        int port,
        int count,
        TimeSpan delay,
        TimeSpan skew,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient();
        client.Connect(host, port);

        long bytes = 0;

        for (var i = 0; i < count; i++)
        {
            // UDP'de her datagram bir satır; ayırıcı yine de yazılıyor çünkü
            // alıcının `line_end_pattern` varsayılanı onu bekliyor.
            var payload = encoding.GetBytes(
                WireLine(lines[i % lines.Count], DateTimeOffset.UtcNow + skew) + "\n");

            await client.SendAsync(payload, cancellationToken);
            bytes += payload.Length;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new EmitResult(count, bytes, clock.Elapsed);
    }

    private static List<string> ReadSamples(SimulatorProfile profile, string repositoryRoot)
    {
        var lines = new List<string>();

        foreach (var sample in profile.Syslog!.Samples)
        {
            var path = Path.Combine(repositoryRoot, sample);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"'{profile.Id}' örnek dosyaya işaret ediyor ama dosya yok: {sample}", path);
            }

            // Örnek dosyalar ürünün kendi kod sayfasında değil, DEPODA UTF-8.
            // Buradan okunan metin sonra profilin tel kodlamasına çevriliyor —
            // gerçek cihazın yaptığı da bu: kendi kod sayfasında basıyor.
            lines.AddRange(File.ReadAllLines(path, Encoding.UTF8)
                .Where(line => line.Trim().Length > 0));
        }

        return lines;
    }

    private static Encoding ResolveEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        // .NET Core kod sayfalarını varsayılan olarak taşımıyor; sağlayıcı bir
        // kez kaydedilmezse `windows-1254` "not supported" ile düşer.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return Encoding.GetEncoding(name);
    }
}

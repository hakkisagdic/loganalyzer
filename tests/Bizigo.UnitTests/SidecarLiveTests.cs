using System.Diagnostics;
using System.Globalization;
using Bizigo.Ingest.Discovery;
using Bizigo.Parsing.Grok;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Canlı</b> sidecar ölçümü — T12'nin taşıyıcı iddiasının tek gerçek kanıtı:
/// <i>sidecar arızalıyken ingest throughput'u düşmüyor.</i>
///
/// <para>
/// Diğer testler <c>HttpMessageHandler</c> seviyesinde sahte. Burada gerçek bir
/// uvicorn süreci, gerçek TCP ve gerçek arıza sinyalleri var:
/// <c>SIGKILL</c> = ölü sidecar (bağlantı reddi), <c>SIGSTOP</c> = <b>asılı</b>
/// sidecar (TCP kabul ediliyor, cevap gelmiyor). İkinci senaryo `docker compose
/// pause` ile de üretilebilir ama sinyalle üretmek hem daha ucuz hem tekrar
/// edilebilir — ölçmek istediğimiz şey konteyner değil, istemcinin davranışı.
/// </para>
///
/// <para>
/// <b>Varsayılan olarak atlanıyor.</b> <c>BIZIGO_SIDECAR_LIVE=1</c> ve
/// <c>sidecar/.venv</c> gerekiyor; CI'da ikisi de yok. Ölçüm testi, doğrulama
/// testi değil: sayıları üretmek için elle koşuluyor.
/// </para>
/// </summary>
public sealed class SidecarLiveTests : IDisposable
{
    private const int Port = 18099;

    /// <summary>Faz başına olay sayısı. Sıcak yol maliyetini ölçmeye fazlasıyla yeter.</summary>
    private const int Events = 20_000;

    private static readonly string VenvPython =
        Path.Combine(RepositoryLayout.Root, "sidecar", ".venv", "bin", "python");

    /// <summary>
    /// Rapor dosyaya <b>anında</b> yazılıyor. xunit <c>Console</c> çıktısını
    /// yutuyor ve test asılırsa hiçbir şey görünmüyor; ölçüm testinin en çok
    /// ihtiyaç duyduğu şey ise nerede takıldığı.
    /// </summary>
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "t12-live.log");

    private readonly List<string> _report = [];
    private Process? _sidecar;

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_report)
        {
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
    }

    private void Report(string line)
    {
        _report.Add(line);
        Log(line);
    }

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("BIZIGO_SIDECAR_LIVE") == "1" && File.Exists(VenvPython);

    /// <summary>
    /// Sıcak yolun <b>sidecar'sız</b> taban maliyeti: imza + önbellek araması.
    /// Canlı ölçümde faz farkları buna göre okunuyor — bu sayı bilinmeden
    /// "sidecar arızası yavaşlattı" demek mümkün değil, çünkü tabanın kendisi
    /// zaten yüksek olabilir.
    ///
    /// <para>
    /// K35'in kendi maliyet ölçümü burada <b>değil</b>:
    /// <see cref="HotPathCostMeasurement"/> ayrı bir sınıfta ve canlı sidecar
    /// gerektirmiyor. Maskeleme maliyeti saf CPU işi; onu Python venv'ine bağlamak
    /// ölçümün hiç koşulmamasının en kolay yolu olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Etiketleme_taban_maliyeti()
    {
        Assert.SkipUnless(Enabled, "BIZIGO_SIDECAR_LIVE=1 gerekiyor.");

        var masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
        var options = new SidecarOptions { QueueCapacity = 64, SampleRate = 0 };
        var stats = new DiscoveryStats();
        var annotator = new DiscoveryAnnotator(
            options, new TemplateCache(50_000), new DiscoveryQueue(options, stats), stats);

        // Isınma: `RegexOptions.Compiled` ilk kullanımda kod üretiyor.
        for (var index = 0; index < 2_000; index++)
        {
            annotator.Annotate(masks, "bench", Line(index), parseFailed: true);
        }

        const int Count = 50_000;
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < Count; index++)
        {
            annotator.Annotate(masks, "bench", Line(index + 9_000_000), parseFailed: true);
        }

        clock.Stop();

        var perEventNs = clock.Elapsed.TotalMilliseconds * 1_000_000 / Count;
        var signatureOnly = Stopwatch.StartNew();
        for (var index = 0; index < Count; index++)
        {
            masks.Compute(Line(index + 8_000_000));
        }

        signatureOnly.Stop();

        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"TABAN · etiketleme {perEventNs:N0} ns/olay " +
            $"({Count / clock.Elapsed.TotalSeconds:N0} olay/sn) · " +
            $"yalnız imza {signatureOnly.Elapsed.TotalMilliseconds * 1_000_000 / Count:N0} ns/olay"));
    }

    [Fact]
    public async Task Canli_sidecar_arizasi_sicak_yolu_etkilemiyor()
    {
        Assert.SkipUnless(Enabled, "BIZIGO_SIDECAR_LIVE=1 ve sidecar/.venv gerekiyor.");

        var masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
        var options = new SidecarOptions
        {
            BaseUrl = $"http://127.0.0.1:{Port}",
            Timeout = TimeSpan.FromSeconds(2),
            FailureThreshold = 5,
            // Üretimde 5 dk; testte 10 sn. Ölçülen şey sürenin kendisi değil,
            // geçişlerin gerçekleşmesi.
            BreakDuration = TimeSpan.FromSeconds(10),
            QueueCapacity = 2048,
            BatchSize = 200,
            SampleRate = 0,
            TemplateCacheCapacity = 50_000,
        };

        var stats = new DiscoveryStats();
        var queue = new DiscoveryQueue(options, stats);
        var cache = new TemplateCache(options.TemplateCacheCapacity);
        var breaker = new SidecarCircuitBreaker(options, TimeProvider.System);
        using var client = new SidecarClient(options);
        var annotator = new DiscoveryAnnotator(options, cache, queue, stats);
        var worker = new DiscoveryWorker(
            options, queue, client, breaker, cache, stats, NullLogger<DiscoveryWorker>.Instance);

        await StartSidecarAsync();
        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            // --- A: sağlıklı ------------------------------------------------
            var healthy = Measure(annotator, masks, Events, "A · sağlıklı", phase: 1);
            await Task.Delay(2_000, TestContext.Current.CancellationToken);
            Report($"    öğrenilen şablon: {stats.NewTemplates}, önbellek: {cache.Count}");

            // --- B: ölü (SIGKILL) -------------------------------------------
            // Fazın anlamlı olması için devrenin **kapalı** başlaması şart:
            // açık bir devreden başlarsak "açılma süresi" sıfır çıkar ve ölçüm
            // hiçbir şey söylemez.
            Assert.Equal(CircuitState.Closed, breaker.State);
            var beforeDead = breaker.OpenedCount;
            KillSidecar();
            var openedAt = Stopwatch.StartNew();
            var dead = Measure(annotator, masks, Events, "B · ölü (SIGKILL)", phase: 2);
            await WaitForAsync(() => breaker.OpenedCount > beforeDead, TimeSpan.FromSeconds(30));
            openedAt.Stop();
            Report(
                $"    devre açılma süresi: {openedAt.Elapsed.TotalSeconds:0.00} sn, " +
                $"devre-açık düşen: {stats.DroppedCircuitOpen}");

            // --- C: asılı (SIGSTOP) — asıl merak edilen sayı -----------------
            await RestartAndCloseCircuitAsync(annotator, masks, breaker, cache);
            var beforeOpen = breaker.OpenedCount;
            var timeoutsBefore = stats.Timeouts;
            StopSidecar();

            var hangClock = Stopwatch.StartNew();
            var hung = Measure(annotator, masks, Events, "C · asılı (SIGSTOP)", phase: 3);
            await WaitForAsync(() => breaker.OpenedCount > beforeOpen, TimeSpan.FromSeconds(60));
            hangClock.Stop();
            var hangTimeouts = stats.Timeouts - timeoutsBefore;
            Report(
                $"    devre açılma süresi: {hangClock.Elapsed.TotalSeconds:0.00} sn " +
                $"({hangTimeouts} zaman aşımı × {options.Timeout.TotalSeconds:0.#} sn)");

            // --- D: geri geliyor --------------------------------------------
            ContinueSidecar();
            await WaitForAsync(() => breaker.State == CircuitState.HalfOpen, TimeSpan.FromSeconds(40));
            Report("D · geri geliyor: yarı açık'a geçti");

            annotator.Annotate(masks, "live", "recovery probe 10.0.0.1 port 22", parseFailed: true);
            await WaitForAsync(() => breaker.State == CircuitState.Closed, TimeSpan.FromSeconds(30));
            Report("    yoklama başarılı → devre kapandı");

            // Sıcak yol maliyeti üç fazda da aynı kalmalı: annotator ağa hiç
            // çıkmıyor, yalnızca maskeleyip önbelleğe bakıyor.
            var worst = Math.Max(dead, hung);
            Assert.True(
                worst < healthy * 3,
                $"Sidecar arızalıyken sıcak yol {worst / healthy:0.00}× yavaşladı — ağ sıcak yola sızmış.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            KillSidecar();

            // xunit çıktısı yerine doğrudan konsola: bu bir ölçüm raporu.
            Console.WriteLine("\n=== T12 canlı sidecar ölçümü ===");
            foreach (var line in _report)
            {
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Olay başına etiketleme maliyeti (ns) — dönen değer karşılaştırma tabanı.
    ///
    /// <para>
    /// Her faz <b>kendi</b> imza uzayını kullanıyor (<paramref name="phase"/>).
    /// İlk denemede fazlar aynı satırları üretiyordu; ikinci faz baştan sona
    /// önbellek isabetiyle geçti, kuyruğa hiçbir şey girmedi ve devre kesici
    /// hiç açılmadı — ölçüm sessizce anlamsızlaşmıştı. Her olayın ıskalaması,
    /// aynı zamanda sıcak yolun <b>en pahalı</b> hâli: maskeleme + arama +
    /// kuyruğa yazma denemesi.
    /// </para>
    /// </summary>
    private double Measure(
        DiscoveryAnnotator annotator, MaskCatalog masks, int events, string label, int phase)
    {
        var latencies = new long[events];
        var total = Stopwatch.StartNew();

        for (var index = 0; index < events; index++)
        {
            var line = Line(index + (phase * 1_000_000));
            var start = Stopwatch.GetTimestamp();
            annotator.Annotate(masks, "live", line, parseFailed: true);
            latencies[index] = Stopwatch.GetTimestamp() - start;
        }

        total.Stop();
        Array.Sort(latencies);

        var perEventNs = total.Elapsed.TotalMilliseconds * 1_000_000 / events;
        var p99Ns = latencies[(int)(events * 0.99)] * 1_000_000_000.0 / Stopwatch.Frequency;
        var maxNs = latencies[^1] * 1_000_000_000.0 / Stopwatch.Frequency;

        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {events / total.Elapsed.TotalSeconds:N0} olay/sn · " +
            $"olay başına {perEventNs:N0} ns · p99 {p99Ns:N0} ns · en kötü {maxNs / 1_000_000:N2} ms"));

        return perEventNs;
    }

    /// <summary>
    /// Her satır <b>farklı</b> bir imzaya düşüyor: sabit kısımdaki
    /// <c>action-{n}</c> tokenı maskelenmiyor (sayı bir kelimenin içinde), o
    /// yüzden imza olay başına benzersiz kalıyor. Değişken alanlar (zaman, IP,
    /// port, pid) maskeleniyor — yani maskeleme işi de gerçekten yapılıyor.
    /// </summary>
    private static string Line(int index) => string.Create(
        CultureInfo.InvariantCulture,
        $"2026-08-17T10:{index % 60:00}:{index % 60:00}Z host-a{index % 250} " +
        $"proc[{index}]: action-a{index} denied for 10.{index % 250}.0.1 port {index % 65000}");

    private async Task RestartAndCloseCircuitAsync(
        DiscoveryAnnotator annotator,
        MaskCatalog masks,
        SidecarCircuitBreaker breaker,
        TemplateCache cache)
    {
        await StartSidecarAsync();
        await WaitForAsync(() => breaker.State != CircuitState.Open, TimeSpan.FromSeconds(40));

        // Devreyi fiilen kapatmak için başarılı bir istek gerekiyor.
        for (var attempt = 0; attempt < 40 && breaker.State != CircuitState.Closed; attempt++)
        {
            // `-a{n}` bilinçli: sayı bir kelimenin içinde kaldığı için maskelenmiyor,
            // yani her yoklama ayrı bir imza ve gerçekten kuyruğa giriyor.
            annotator.Annotate(masks, "live", $"warmup-a{attempt} from 10.0.0.{attempt}", parseFailed: true);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        Report($"    yeniden başladı, devre kapandı (önbellek: {cache.Count})");
    }

    private async Task StartSidecarAsync()
    {
        Log("[harness] sidecar başlatılıyor");

        var start = new ProcessStartInfo(VenvPython)
        {
            WorkingDirectory = Path.Combine(RepositoryLayout.Root, "sidecar"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("uvicorn");
        start.ArgumentList.Add("app.main:app");
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(Port.ToString(CultureInfo.InvariantCulture));
        // Erişim logu kapalı: ölçüm binlerce istek atıyor ve uvicorn her biri
        // için satır yazıyor. Log hacmi hem ölçümü kirletiyor hem de aşağıdaki
        // boru sorununu tetikliyor.
        start.ArgumentList.Add("--no-access-log");

        start.Environment["BIZIGO_MASKS_PATH"] = RepositoryLayout.MaskFile;
        // Redis bilerek erişilemez: ölçülen şey mining, kalıcılık değil.
        start.Environment["REDIS_URL"] = "redis://127.0.0.1:1/0";

        _sidecar = Process.Start(start)
            ?? throw new InvalidOperationException("uvicorn başlatılamadı.");

        // Boruları **boşaltmak zorunlu**. Yönlendirip okumazsak 64 KB'lık boru
        // dolduğunda uvicorn `write`'ta bloklanıyor: süreç ayakta görünüyor,
        // porta cevap vermiyor ve ölçüm sessizce kilitleniyor. İlk koşuşta tam
        // bunu yaşadım — 26 dakika asılı kaldı.
        _sidecar.OutputDataReceived += (_, e) => Log(e.Data);
        _sidecar.ErrorDataReceived += (_, e) => Log(e.Data);
        _sidecar.BeginOutputReadLine();
        _sidecar.BeginErrorReadLine();

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await WaitForAsync(
            () =>
            {
                try
                {
                    return probe.GetAsync(new Uri($"http://127.0.0.1:{Port}/healthz"))
                        .GetAwaiter().GetResult().IsSuccessStatusCode;
                }
                catch (Exception)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30));

        Log("[harness] sidecar sağlıklı");
    }

    private void KillSidecar()
    {
        if (_sidecar is null || _sidecar.HasExited)
        {
            return;
        }

        // Durdurulmuş bir süreç SIGKILL ile ölür ama önce devam ettirmek,
        // çocuklarının da toplanmasını garantiliyor.
        Signal("-CONT");
        _sidecar.Kill(entireProcessTree: true);
        _sidecar.WaitForExit(10_000);
        _sidecar = null;

        // Süreç öldü demek port serbest demek değil. Beklemezsek bir sonraki
        // uvicorn portu bağlayamadan ölüyor, sağlık yoklaması ise **eski**
        // sürece cevap verdiği için başarılı görünüyor — ölçüm sessizce
        // yanlış sürece bakmaya başlıyor.
        Log("[harness] port serbest kalması bekleniyor");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15) && IsPortOpen())
        {
            Thread.Sleep(100);
        }
    }

    private static bool IsPortOpen()
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            return probe.ConnectAsync("127.0.0.1", Port).Wait(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StopSidecar() => Signal("-STOP");

    private void ContinueSidecar() => Signal("-CONT");

    private void Signal(string signal)
    {
        if (_sidecar is null || _sidecar.HasExited)
        {
            return;
        }

        using var kill = Process.Start("/bin/kill", [signal, _sidecar.Id.ToString(CultureInfo.InvariantCulture)]);
        kill?.WaitForExit(5_000);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Koşul {timeout.TotalSeconds:0} sn içinde sağlanmadı.");
    }

    public void Dispose() => KillSidecar();
}

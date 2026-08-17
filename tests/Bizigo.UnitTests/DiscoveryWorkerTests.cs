using System.Diagnostics;
using System.Net;
using System.Text;
using Bizigo.Ingest.Discovery;
using Bizigo.Parsing.Grok;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Keşif işçisi — T12'nin taşıyıcı kabul kriteri:
/// <b>sidecar durdurulduğunda ingest etkilenmiyor</b>.
///
/// <para>
/// Buradaki testler tam olarak o cümlenin ölçülebilir hâli: sidecar hiç
/// cevap vermezken etiketleme çağrıları anında dönüyor, işçi ölmüyor,
/// devre kesici açılıyor ve sonrasında tek bir istek bile denenmiyor.
/// </para>
/// </summary>
public sealed class DiscoveryWorkerTests
{
    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return handler(request, cancellationToken);
        }
    }

    private sealed record Harness(
        DiscoveryWorker Worker,
        DiscoveryQueue Queue,
        DiscoveryAnnotator Annotator,
        DiscoveryStats Stats,
        SidecarCircuitBreaker Breaker,
        TemplateCache Cache,
        StubHandler Handler,
        FakeTimeProvider Time);

    private static Harness Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        SidecarOptions? options = null)
    {
        options ??= new SidecarOptions
        {
            QueueCapacity = 256,
            BatchSize = 32,
            FailureThreshold = 2,
            SampleRate = 0,
            Timeout = TimeSpan.FromMilliseconds(200),
        };

        var stats = new DiscoveryStats();
        var queue = new DiscoveryQueue(options, stats);
        var cache = new TemplateCache(1024);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero));
        var breaker = new SidecarCircuitBreaker(options, time);
        var stub = new StubHandler(handler);
        var client = new SidecarClient(
            options,
            new HttpClient(stub) { BaseAddress = new Uri("http://sidecar.test/") });

        return new Harness(
            new DiscoveryWorker(
                options, queue, client, breaker, cache, stats,
                NullLogger<DiscoveryWorker>.Instance),
            queue,
            new DiscoveryAnnotator(options, Masks, cache, queue, stats),
            stats,
            breaker,
            cache,
            stub,
            time);
    }

    /// <summary>
    /// Sıcak yolu <paramref name="events"/> kez koşturur. Dönen imza her zaman
    /// boş olmalı — sidecar cevabı beklenmediğinin doğrudan kanıtı.
    /// </summary>
    private static void Measure(Harness harness, int events)
    {
        for (var index = 0; index < events; index++)
        {
            Assert.Equal(
                string.Empty,
                harness.Annotator.Annotate("firewall", $"deny tcp 10.0.0.{index % 255}", parseFailed: true));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail(because);
    }

    [Fact]
    public async Task Sidecar_olduyse_etiketleme_bloklamiyor_ve_isci_yasiyor()
    {
        const int Events = 20_000;

        var harness = Build((_, _) => throw new HttpRequestException("Connection refused"));
        using var cts = new CancellationTokenSource();
        await harness.Worker.StartAsync(cts.Token);

        try
        {
            // Sıcak yolun sidecar'a hiç dokunmadığı, SÜRE ile değil DAVRANIŞ ile
            // sınanıyor: 20 bin olayın hepsi boş imza döndürüyor, yani etiketleme
            // hiçbir noktada cevabı beklemiyor.
            //
            // Buradaki throughput iddiasının ("sidecar ölüyken ingest yavaşlamıyor")
            // yeri `SidecarLiveTests` — gerçek süreç, gerçek TCP, ve elle
            // koşulan bir ÖLÇÜM. Duvar saatini birim paketine sokmak, ölçmek
            // istediğimiz şeyi değil makinenin o anki hızını sınamak olurdu;
            // F1'de aynı hata üç ayrı yerde çıktı.
            Measure(harness, Events);

            await WaitUntilAsync(
                () => harness.Breaker.State == CircuitState.Open,
                "Devre kesici açılmadı.");

            var callsWhenOpen = Volatile.Read(ref harness.Handler.Calls);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            // Devre açıkken hiç denenmiyor: ölü sidecar'ın maliyeti sıfır.
            Assert.Equal(callsWhenOpen, Volatile.Read(ref harness.Handler.Calls));

            // Düşürme sayacını İŞÇİ artırıyor (enqueue değil): kuyruktan bir
            // yığın alıp devreyi açık bulduğunda. Dolayısıyla devre açıldıktan
            // sonra hem yeni olay üretilmeli hem de işçinin bir turu beklenmeli.
            // Yukarıdaki 20 bin olayın bir kısmının devre açıkken işlenmesine
            // güvenmek bir yarıştı: makine hızlandıkça, ya da geri adım devreyi
            // geciktirdikçe döngü önce bitiyor.
            for (var index = 0; index < 100; index++)
            {
                harness.Annotator.Annotate("firewall", $"deny udp 10.9.9.{index % 255}", parseFailed: true);
            }

            await WaitUntilAsync(
                () => harness.Stats.DroppedCircuitOpen > 0,
                "Devre açıkken alınan yığınlar düşürülmüyor.");
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Basarili_yanit_onbellegi_dolduruyor()
    {
        const string Line = "Failed password for admin from 10.1.2.3 port 51234 ssh2";
        var signature = Masks.Signature(Line);

        var harness = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                  {"api_version":"v1","masks_version":1,"cluster_count":1,
                   "results":[{"id":"0","template_id":"linux:5","template":"t",
                               "is_new":true,"masked":{{System.Text.Json.JsonSerializer.Serialize(signature)}}}]}
                  """,
                Encoding.UTF8,
                "application/json"),
        }));

        using var cts = new CancellationTokenSource();
        await harness.Worker.StartAsync(cts.Token);

        try
        {
            harness.Annotator.Annotate("linux", Line, parseFailed: true);

            await WaitUntilAsync(() => harness.Cache.Count > 0, "Önbellek dolmadı.");

            Assert.True(harness.Cache.TryGet(signature, out var templateId));
            Assert.Equal("linux:5", templateId);
            Assert.Equal(0, harness.Stats.SignatureDrift);
            Assert.Equal(1, harness.Stats.NewTemplates);

            // Aynı imzalı ikinci satır artık sidecar'a hiç gitmiyor.
            Assert.Equal(
                "linux:5",
                harness.Annotator.Annotate(
                    "linux", "Failed password for admin from 172.16.0.1 port 22 ssh2", parseFailed: true));
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Maske_sapmasi_onbellege_yazilmiyor()
    {
        // Sidecar bizden farklı maskeliyor: `template_id` bu imzaya ait değil.
        // Yazmak sessizce yanlış veri üretirdi.
        var harness = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"api_version":"v1","masks_version":1,"cluster_count":1,
                 "results":[{"id":"0","template_id":"linux:5","template":"t",
                             "is_new":false,"masked":"BASKA BIR MASKELEME"}]}
                """,
                Encoding.UTF8,
                "application/json"),
        }));

        using var cts = new CancellationTokenSource();
        await harness.Worker.StartAsync(cts.Token);

        try
        {
            harness.Annotator.Annotate("linux", "deny tcp 10.0.0.1 -> 10.0.0.2", parseFailed: true);

            await WaitUntilAsync(() => harness.Stats.SignatureDrift > 0, "Sapma sayacı artmadı.");
            Assert.Equal(0, harness.Cache.Count);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Surum_uyusmazligi_devreyi_aciyor()
    {
        var harness = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"api_version":"v2","masks_version":1,"cluster_count":0,"results":[]}""",
                Encoding.UTF8,
                "application/json"),
        }));

        using var cts = new CancellationTokenSource();
        await harness.Worker.StartAsync(cts.Token);

        try
        {
            harness.Annotator.Annotate("linux", "deny tcp 10.0.0.1", parseFailed: true);

            // Tek istek yeter: eşik beklenmiyor.
            await WaitUntilAsync(
                () => harness.Breaker.State == CircuitState.Open,
                "Sürüm uyuşmazlığında devre açılmadı.");
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }
}

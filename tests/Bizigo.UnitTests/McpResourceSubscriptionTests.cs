using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Resources;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M07 · Aboneliğin ölçümü — ilan, onay, teslim ve sızıntı.</b>
///
/// <para>
/// Abonelik bu üründe dört ayrı iddia ve <b>dördü ayrı ayrı yanlış olabilir</b>:
/// yetenek ilan edildi mi, sunucu aboneliği <b>onayladı</b> mı, bildirim
/// <b>ulaştı</b> mı, ve abone gittiğinde kayıt <b>kapandı</b> mı. Tek bir
/// "abonelik çalışıyor" testi bunların hangisinin tuttuğunu söyleyemez.
/// </para>
///
/// <para>
/// <b>Ham JSON-RPC kullanılıyor ve sebebi ölçüldü:</b> çivilediğimiz revizyon
/// (<c>2026-07-28</c>, SEP-2575) <c>resources/subscribe</c>'ı <b>kaldırdı</b> ve
/// yerine <c>subscriptions/listen</c> + <c>resourceSubscriptions</c> koydu. SDK'nın
/// <b>sunucu</b> tarafı bunu uyguluyor ama <b>istemci</b> tarafı uygulamıyor:
/// <c>McpClient.SubscribeToResourceAsync</c> hâlâ kaldırılmış RPC'yi çağırıyor ve
/// sunucu onu göç ipucuyla reddediyor. Yani zinciri SDK istemcisiyle ölçmek
/// <b>mümkün değil</b>; ölçüm ham mesajla yapılıyor.
/// </para>
/// </summary>
public sealed class McpResourceSubscriptionTests
{
    private const string ListenIstegi = """
        {"jsonrpc":"2.0","id":1,"method":"subscriptions/listen","params":{"notifications":{"resourceSubscriptions":["bizigo://rca-runs"]},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"olcum","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
        """;

    private static readonly McpBoundaryDeclaration Boundary =
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "abonelik ölçümü: bellek içi");

    /// <summary>
    /// <b>Abonelik yalnızca <c>rca-runs</c> kaynağında.</b>
    ///
    /// <para>
    /// Hepsini açmak, ilan edilip bildirimi hiç gönderilmeyen bir yetenek
    /// bırakırdı: kanıt paketi ve RCA raporu <b>değişmiyor</b> (bir kez yazılıp
    /// okunuyorlar), parser tanımı ise operatör olayı. Küme <b>harfi harfine</b>
    /// yazılı — sabitlerden türetilseydi bir kaynağın bayrağı değiştiğinde test
    /// kendisiyle birlikte değişir ve <b>hiç kırmızı yanamazdı</b> (M07'nin
    /// birinci turunda tam bu kusur ölçüldü).
    /// </para>
    /// </summary>
    [Fact]
    public void Abonelik_destekleyen_tek_kaynak_var()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var abone = BizigoMcpServer
            .Resources(McpSurface.Product, McpComplianceTests.DeclaredAssemblies(McpSurface.Product), services)
            .Where(static resource => resource.SupportsSubscription)
            .Select(static resource => resource.Kind)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["rca-runs"], abone);
    }

    /// <summary>
    /// <b>Taşıma gönderemiyorsa yetenek ilan edilmiyor.</b>
    ///
    /// <para>
    /// İki yön birden ölçülüyor, çünkü tek yön yanıltıcı: yalnızca "kapalıyken
    /// kapalı" ölçülse her zaman kapalı bir bayrak da geçerdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Yetenek_tasimanin_gonderebilmesine_bagli(bool deliverable, bool beklenen)
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services,
            subscriptionsDeliverable: deliverable);

        Assert.Equal(beklenen, options.Capabilities?.Resources?.Subscribe ?? false);
    }

    /// <summary>
    /// <b>Kaldırılmış RPC gerçekten kaldırılmış</b> — ve sunucu göç ipucu veriyor.
    ///
    /// <para>
    /// Bu bir uyum ölçümü: bir istemci eski yolu denerse cevap <i>"böyle bir
    /// metot yok"</i> değil, <b>ne yapması gerektiği</b> olmalı. Aksi hâlde
    /// istemci yazarı revizyon farkını arar ve bulamaz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kaldirilmis_abonelik_rpcsi_goc_ipucu_donduruyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: false, ekIstek: """
            {"jsonrpc":"2.0","id":2,"method":"resources/subscribe","params":{"uri":"bizigo://rca-runs"}}
            """);

        var cevap = satirlar.FirstOrDefault(s => s.Contains("\"id\":2", StringComparison.Ordinal));

        Assert.NotNull(cevap);
        Assert.Contains("subscriptions/listen", cevap, StringComparison.Ordinal);
        Assert.Contains("resourceSubscriptions", cevap, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Yetenek kapalıyken sunucu aboneliği ONAYLAMIYOR.</b>
    ///
    /// <para>
    /// Ölçülen hâl: <c>subscriptions/acknowledged</c>'ın <c>notifications</c>'ı
    /// <b>boş</b> dönüyor — yani sunucu <i>"bu aboneliği taşımıyorum"</i> diyor.
    /// Bu, <c>SupportsSubscription</c> bayrağının bir süsleme <b>olmadığının</b>
    /// kanıtı: bayrak aboneliğin kabul edilmesinin şartı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yetenek_kapaliyken_abonelik_onaylanmiyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: false, yayinla: false);

        var ack = satirlar.Single(s => s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));

        Assert.DoesNotContain("resourceSubscriptions", ack, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Zincirin tamamı: onay + teslim.</b>
    ///
    /// <para>
    /// İki iddia birden ve ikisi ayrı: sunucu aboneliği <b>onaylıyor</b>
    /// (<c>resourceSubscriptions</c> geri dönüyor) <b>ve</b> yayın istemciye
    /// <b>ulaşıyor</b>. Onay olmadan teslim anlamsız (spesifikasyon istenmeyen
    /// bildirim göndermeyi yasaklıyor), teslim olmadan onay boş bir söz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Abonelik_onaylaniyor_ve_bildirim_ulasiyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: true);

        var ack = satirlar.Single(s => s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));

        Assert.Contains("bizigo://rca-runs", ack, StringComparison.Ordinal);

        var bildirim = satirlar.SingleOrDefault(s =>
            s.Contains("notifications/resources/updated", StringComparison.Ordinal));

        Assert.NotNull(bildirim);
        Assert.Contains("bizigo://rca-runs", bildirim, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>ÖLÇÜLEN SINIR: bildirim abonelik kimliğiyle ETİKETLENMİYOR.</b>
    ///
    /// <para>
    /// Spesifikasyon her abonelik bildiriminin
    /// <c>_meta/io.modelcontextprotocol/subscriptionId</c> taşımasını istiyor —
    /// aynı kanalı paylaşan abonelikler ancak öyle ayrılabiliyor. Onay
    /// bildirimi <b>etiketli</b> geliyor (SDK'nın kendi yolu), bizim yayınımız
    /// <b>etiketsiz</b>: erişebildiğimiz tek yayın ilkeli oturum geneline yazan
    /// <c>SendNotificationAsync</c> ve SDK'nın yönlendirmesi <c>internal</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Bu test sınırı KİLİTLİYOR, savunmuyor.</b> SDK bir gün genel bir
    /// yönlendirme yüzeyi açarsa ya da davranış değişirse burası kırmızı yanıyor
    /// ve o gün <c>McpResourceUpdates</c>'in açık kalemi kapanabiliyor. Sınırı
    /// yazıp ölçmemek, onu bir varsayıma çevirirdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bildirim_abonelik_kimligiyle_etiketlenmiyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: true);

        var ack = satirlar.Single(s => s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));
        var bildirim = satirlar.Single(s =>
            s.Contains("notifications/resources/updated", StringComparison.Ordinal));

        // Onay ETİKETLİ — yani etiketleme mekanizması var ve çalışıyor.
        Assert.Contains("subscriptionId", ack, StringComparison.Ordinal);

        // Bizim yayınımız DEĞİL. Açık kalem `McpResourceUpdates` belgesinde.
        Assert.DoesNotContain("subscriptionId", bildirim, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kayıt çıkarılınca yayın gitmiyor</b> (kabul kriteri 4).
    ///
    /// <para>
    /// Sızıntının bu katmandaki hâli: kapanmış bir sunucu defterde kalırsa her
    /// koşum değişiminde kapanmış bir kanala yazmayı deniyor. Ölçüt defterin
    /// <b>sayısı</b> — "bildirim gitmedi" ölçütü, hiç abone olmayan bir
    /// kurulumda da geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kayit_cikarilinca_defter_bosaliyor()
    {
        var updates = new McpResourceUpdates();

        Assert.Equal(0, updates.RegisteredCount);

        await using var services = McpTestServices.ForDiscoveredTools();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services,
            subscriptionsDeliverable: true);

        var toServer = new Pipe();
        var toClient = new Pipe();

        await using var transport = new StreamServerTransport(
            toServer.Reader.AsStream(), toClient.Writer.AsStream(), "olcum", NullLoggerFactory.Instance);

        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, services);

        using (updates.Register(server))
        {
            Assert.Equal(1, updates.RegisteredCount);
        }

        Assert.Equal(0, updates.RegisteredCount);

        // Defter boşken yayın SESSİZ ve hatasız: gönderilecek kimse yok.
        await updates.PublishAsync(RcaRunsResource.Uri, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>KOŞUM BAŞINA KAÇ BİLDİRİM — ölçülen sayı.</b>
    ///
    /// <para>
    /// Koordinatörün açık sorusu: <i>"bildirim sıklığı bir bütçe kalemi mi?"</i>
    /// Cevap bir tahmin değil bu sayı: bir koşumun tam yaşam döngüsü
    /// <b>üç</b> bildirim üretiyor (kabul → terminal), ve <c>TryStartAsync</c>
    /// bağlandığında <b>dört</b> olacak.
    /// </para>
    ///
    /// <para>
    /// Bütçe açısından okunuşu: bildirim <b>içerik taşımıyor</b>, yalnızca adres
    /// (~10 belirteç). Bağlamı şişiren şey bildirimin kendisi değil, abonenin
    /// her bildirimde belgeyi <b>yeniden okuması</b>. Yani sıklık bir bütçe
    /// kalemi <b>dolaylı olarak</b>: koşum başına üç okuma.
    /// </para>
    ///
    /// <para>
    /// Sayının <b>kilitli</b> olması kasıtlı: dördüncü nokta bağlandığında burası
    /// kırmızı yanıyor ve sayıyı güncellemek bilinçli bir hareket oluyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kosum_basina_uc_bildirim()
    {
        var ct = TestContext.Current.CancellationToken;
        var sayac = new SayanDinleyici();

        var admission = new RcaAdmission(
            new InMemoryControlPlaneFactory(),
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System,
            [sayac]);

        var kabul = await admission.AdmitAsync(
            new RcaTriggerRequest
            {
                Source = RcaTriggerSource.Manual,
                Identity = "olcum",
                OwnerGroup = "network/core",
                WindowFrom = DateTimeOffset.UnixEpoch,
                WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(45),
            },
            ct);

        Assert.False(kabul.Existing);
        Assert.Equal(1, sayac.Sayi);

        // `TryStartAsync` BAĞLI DEĞİL (T54 imzasını değiştiriyor) — bu çağrı
        // bilerek buradadır: bağlandığı gün sayı 3'ten 4'e çıkıyor ve aşağıdaki
        // iddia kırmızı yanarak sayıyı güncellemeye zorluyor.
        Assert.True(await admission.TryStartAsync(kabul.Run.Id, ct));
        Assert.Equal(1, sayac.Sayi);

        await admission.StopAsync(kabul.Run.Id, RcaStopReason.OperatorCancelled, "ölçüm", ct);
        Assert.Equal(2, sayac.Sayi);

        var ikinci = await admission.AdmitAsync(
            new RcaTriggerRequest
            {
                Source = RcaTriggerSource.Manual,
                Identity = "olcum-2",
                OwnerGroup = "network/core",
                WindowFrom = DateTimeOffset.UnixEpoch,
                WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(45),
            },
            ct);

        await admission.AttachBundleAsync(ikinci.Run.Id, Guid.NewGuid(), cancellationToken: ct);

        // 2 kabul + 1 stop + 1 attach = 4; koşum başına 2 (bugün), 3 (T54 sonrası).
        Assert.Equal(4, sayac.Sayi);
    }

    /// <summary>
    /// <b>Dinleyicinin hatası koşumu düşürmüyor.</b>
    ///
    /// <para>
    /// Yayın noktası kaydın <b>sonrasında</b>: bir bildirim kanalının kırılması
    /// veritabanına yazılmış bir gerçeği geri almaz. İstisna yukarı çıksaydı
    /// <c>StopAsync</c> düşer ve koşumun terminal duruma geçtiği kayıt
    /// <b>kaybolurdu</b> — bir bildirim arızasının bedeli bir veri kaybı olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dinleyicinin_hatasi_kosumu_dusurmuyor()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new InMemoryControlPlaneFactory();

        var admission = new RcaAdmission(
            factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System,
            [new PatlayanDinleyici()]);

        var kabul = await admission.AdmitAsync(
            new RcaTriggerRequest
            {
                Source = RcaTriggerSource.Manual,
                Identity = "olcum",
                OwnerGroup = "network/core",
                WindowFrom = DateTimeOffset.UnixEpoch,
                WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(45),
            },
            ct);

        await admission.StopAsync(kabul.Run.Id, RcaStopReason.OperatorCancelled, "ölçüm", ct);

        // KAYIT YERİNDE: dinleyici patladı ama koşumun durumu yazıldı.
        await using var db = factory.CreateDbContext();

        var run = await db.RcaRuns.AsNoTracking().FirstAsync(r => r.Id == kabul.Run.Id, ct);

        Assert.Equal(RcaRunState.Cancelled, run.State);
    }

    /// <summary>
    /// Ham JSON-RPC ile zinciri koşturur: <c>subscriptions/listen</c> aç,
    /// istenirse yayın yap, gelen satırları döndür.
    /// </summary>
    private static async Task<(List<string> Satirlar, McpResourceUpdates Updates)> ZincirAsync(
        bool deliverable,
        bool yayinla,
        string? ekIstek = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var updates = new McpResourceUpdates();

        await using var services = McpTestServices.ForDiscoveredTools();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services,
            subscriptionsDeliverable: deliverable);

        var toServer = new Pipe();
        var toClient = new Pipe();

        await using var transport = new StreamServerTransport(
            toServer.Reader.AsStream(), toClient.Writer.AsStream(), "olcum", NullLoggerFactory.Instance);

        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, services);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = server.RunAsync(lifetime.Token);

        using var kayit = updates.Register(server);

        var yaz = toServer.Writer.AsStream();
        using var oku = new StreamReader(toClient.Reader.AsStream(), Encoding.UTF8);

        await GonderAsync(yaz, ListenIstegi, ct);

        var satirlar = new List<string> { await OkuAsync(oku, ct) };

        if (ekIstek is not null)
        {
            await GonderAsync(yaz, ekIstek, ct);
            satirlar.Add(await OkuAsync(oku, ct));
        }

        if (yayinla)
        {
            await updates.PublishAsync(RcaRunsResource.Uri, ct);
            satirlar.Add(await OkuAsync(oku, ct));
        }

        await lifetime.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Beklenen: sunucu döngüsü iptalle kapanıyor.
        }

        return (satirlar, updates);
    }

    private static async Task GonderAsync(Stream yaz, string satir, CancellationToken ct)
    {
        await yaz.WriteAsync(
            Encoding.UTF8.GetBytes(satir.Replace("\n", string.Empty, StringComparison.Ordinal) + "\n"),
            ct);

        await yaz.FlushAsync(ct);
    }

    /// <summary>
    /// Bir satır okur. <b>Zaman aşımı bir iddia değil bir sınır:</b> asılı kalan
    /// bir ölçüm CI'da ürün hakkında hiçbir şey söylemeyen bir iş zaman aşımına
    /// dönüşüyor — M01'de ölçülmüş bir kusur.
    /// </summary>
    private static async Task<string> OkuAsync(StreamReader oku, CancellationToken ct)
    {
        var read = oku.ReadLineAsync(ct).AsTask();
        var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), ct));

        return done == read ? await read ?? "(null)" : "(ZAMAN AŞIMI)";
    }

    private sealed class SayanDinleyici : IRcaRunChangeListener
    {
        public int Sayi { get; private set; }

        public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken)
        {
            Sayi++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PatlayanDinleyici : IRcaRunChangeListener
    {
        public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ölçüm: dinleyici patlıyor");
    }
}

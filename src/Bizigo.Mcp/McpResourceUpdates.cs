using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// <b><c>notifications/resources/updated</c>'ın gönderildiği tek yer.</b>
///
/// <para>
/// Bir kaynak değiştiğinde abonelere haber vermek bir <b>oturum</b> işi: bildirim
/// açık bir kanaldan gidiyor. Ürünün geri kalanı (RCA çekirdeği) oturumu bilmiyor
/// ve bilmemeli, o yüzden haber <see cref="Contracts.IRcaRunChangeListener"/>
/// üzerinden buraya geliyor ve buradan tele iniyor.
/// </para>
///
/// <h3>Neden bir KAYIT DEFTERİ — ve neden HTTP'de boş kalıyor</h3>
///
/// <para>
/// Bildirim göndermek için canlı bir <see cref="McpServer"/> gerekiyor. stdio'da
/// bu <b>tek ve uzun ömürlü</b>: süreç bir sunucu kuruyor ve süreç bitene kadar
/// yaşıyor, dolayısıyla kendini buraya kaydediyor.
/// </para>
///
/// <para>
/// <b>Akışlanabilir HTTP'de öyle değil ve sebebi çivilediğimiz revizyon:</b>
/// <c>2026-07-28</c> (SEP-2567) <c>Mcp-Session-Id</c>'yi <b>kaldırdı</b>, yani o
/// taşımada oturum <b>yok</b> — her istek kendi başına. Tutulacak bir sunucu
/// örneği olmadığı için defter orada boş kalıyor, ve boş kalması bir eksiklik
/// değil <b>taşımanın şekli</b>.
/// </para>
///
/// <para>
/// Bu yüzden <c>resources.subscribe</c> yeteneği <b>taşımaya göre</b> ilan
/// ediliyor (<see cref="BizigoMcpServer.Apply"/>'nin
/// <c>subscriptionsDeliverable</c> parametresi): gönderilemeyecek bir bildirimi
/// ilan etmek, istemciyi <b>hiç sormamaya</b> ikna eder ve sonuç sessizce bayat
/// veridir. Kalıp SDK'nın kendi kararından: o da <c>listChanged</c> bayrağını
/// <i>"sunucunun onurlandırmasının yolu olmadığı"</i> yanıtlarda bastırıyor.
/// </para>
///
/// <h3>ÖLÇÜLEN SINIR — bildirim ETİKETSİZ gidiyor</h3>
///
/// <para>
/// Çivilediğimiz revizyonda abonelik <c>subscriptions/listen</c> üzerinden
/// kuruluyor ve spesifikasyon her bildirimin abonelik kimliğiyle
/// (<c>_meta/io.modelcontextprotocol/subscriptionId</c>) etiketlenmesini istiyor
/// — istemci aynı kanalı paylaşan abonelikleri ancak öyle ayırabiliyor. SDK bu
/// yönlendirmeyi <b>kendi içinde</b> yapıyor ve yüzeyi <c>internal</c>;
/// buradan erişilebilen tek yayın ilkeli oturum geneline yazan
/// <c>SendNotificationAsync</c>.
/// </para>
///
/// <para>
/// Ölçüldü (ham JSON-RPC ile, <c>McpResourceSubscriptionTests</c>): abonelik
/// <b>onaylanıyor</b> (<c>resourceSubscriptions</c> geri dönüyor) ve bildirim
/// istemciye <b>ulaşıyor</b>, ama <c>_meta</c> taşımıyor. Tek aboneliği olan bir
/// istemci için fark yok; aynı kanalda birden fazla abonelik açan bir istemci
/// bunları ayırt edemez.
/// </para>
///
/// <para>
/// Doğru çözüm <c>SubscriptionsListenHandler</c>'ı <b>bizim sahiplenmemiz</b>
/// (<c>McpRequestHandler&lt;SubscriptionsListenRequestParams, EmptyResult&gt;</c>)
/// — o zaman hem etiketleme hem <b>kapsam süzgeci</b> bizde olurdu. Bugün
/// yapılmadı çünkü SDK'nın kendi işleyicisi aynı akışta <c>*/list_changed</c>
/// yayılımını da taşıyor ve onu devralmak M07'nin kapsamının dışında ikinci bir
/// mekanizma yazmak demekti. <b>Yazılı bir açık kalem.</b>
/// </para>
///
/// <h3>İkinci ölçülen sınır — bildirim KAPSAM SÜZGECİNDEN geçmiyor</h3>
///
/// <para>
/// Bildirim içerik taşımıyor, yalnızca adres — ve o adresi okumak kapsam
/// kapısından geçiyor (<c>BizigoMcpResource.ReadAsync</c>). Yani bir abone
/// göremeyeceği bir grubun koşumunu <b>okuyamıyor</b>. Ama bildirimin
/// <i>zamanlaması</i> bir sinyal: kapsamı dışındaki bir grupta bir şey olduğunu
/// öğreniyor. Süzgeci kurmak abonelik başına kimliği bilmek demek ve o bilgi de
/// yukarıdaki işleyicide — aynı açık kalemin ikinci yüzü.
/// </para>
/// </summary>
public sealed class McpResourceUpdates(ILogger<McpResourceUpdates>? logger = null)
{
    private readonly List<McpServer> _servers = [];
    private readonly object _lock = new();

    /// <summary>
    /// Canlı bir sunucuyu deftere yazar ve <b>çıkarmayı çağırana bırakır</b>.
    ///
    /// <para>
    /// Dönen <see cref="IDisposable"/> kaydı siliyor. Kabul kriteri 4'ün
    /// (<i>"abonelik sızdırmıyor"</i>) bu katmandaki hâli: sunucu kapandığında
    /// defterde kalan bir örnek, kapanmış bir kanala yazmayı denemek demek —
    /// ve o deneme her koşum değişiminde tekrarlanırdı.
    /// </para>
    /// </summary>
    public IDisposable Register(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        lock (_lock)
        {
            _servers.Add(server);
        }

        return new Kayit(this, server);
    }

    /// <summary>Deftere yazılı sunucu sayısı — bekçiler bunu ölçüyor.</summary>
    public int RegisteredCount
    {
        get
        {
            lock (_lock)
            {
                return _servers.Count;
            }
        }
    }

    /// <summary>
    /// Bir kaynağın değiştiğini abonelere duyurur.
    ///
    /// <para>
    /// <b>Hiçbir istisna yukarı çıkmıyor.</b> Yayın noktası koşumun durumu
    /// veritabanına <b>yazıldıktan sonra</b>; bir bildirim kanalının kırılması
    /// yazılmış bir gerçeği geri almaz, ve istisnayı yukarı vermek
    /// <c>RcaAdmission.StopAsync</c>'i bir bildirim arızası yüzünden düşürürdü —
    /// yani koşumun terminal duruma geçtiği kaydı <b>kaybettirirdi</b>.
    /// </para>
    /// </summary>
    public async ValueTask PublishAsync(string uri, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        McpServer[] hedefler;

        lock (_lock)
        {
            hedefler = [.. _servers];
        }

        foreach (var server in hedefler)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams { Uri = uri },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Sessiz DEĞİL, ama yukarı da çıkmıyor: kanal kırıldıysa
                // görünür olmalı, koşumu düşürmemeli.
                logger?.LogWarning(
                    error,
                    "MCP kaynak bildirimi gönderilemedi: {Uri}. Koşum kaydı etkilenmedi.",
                    uri);
            }
        }
    }

    private sealed class Kayit(McpResourceUpdates owner, McpServer server) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            lock (owner._lock)
            {
                owner._servers.Remove(server);
            }
        }
    }
}

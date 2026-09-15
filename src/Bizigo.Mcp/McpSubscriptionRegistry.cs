using System.Text.Json;
using System.Text.Json.Nodes;
using Bizigo.Contracts;
using ModelContextProtocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// Açık <c>subscriptions/listen</c> aboneliklerinin defteri — <b>kimliğiyle ve
/// kapsamıyla</b>.
///
/// <para>
/// M07 iki sınırı ölçüp yazılı bıraktı: bildirim abonelik kimliğiyle
/// <b>etiketlenmiyordu</b> ve <b>kapsam süzgecinden geçmiyordu</b>. İkisinin de
/// sebebi aynıydı — abonelik başına kimlik bu katmanda bilinmiyordu. Bu defter
/// onu biliyor.
/// </para>
///
/// <h3>SDK'nın işleyicisi DEVRALINMADI — ve bu kararın kendisi</h3>
///
/// <para>
/// İlk plan <c>Handlers.SubscriptionsListenHandler</c>'ı sahiplenmekti. <b>Elendi
/// ve sebebi ölçüldü:</b> SDK'nın kendi işleyicisi aynı akışta
/// <c>*/list_changed</c> yayılımını da taşıyor (<c>ActiveSubscription</c>,
/// <c>GrantsListChanged</c>, <c>SendListChangedNotificationAsync</c>) ve o
/// yüzeyler <c>internal</c>. Devralmak, katalog bildirimlerinin yayılımını
/// <b>ikinci kez yazmak</b> demekti (§9) — ve o kopya sessizce ayrışırdı:
/// abonelik kazanırken <c>tools/list_changed</c> kaybolurdu.
/// </para>
///
/// <para>
/// Yerine kullanılan şey bir <b>mesaj filtresi</b>: istek SDK'nın işleyicisine
/// gidiyor, biz yalnızca <b>yanından</b> okuyoruz. Filtre isteğin kimliğini ve
/// istenen adresleri görüyor, kapsamı çözüyor, deftere yazıyor, sonra
/// <c>next</c>'i çağırıp akışın SDK'da tutulmasına izin veriyor. Yani
/// mekanizma <b>bir</b> tane kaldı ve `*/list_changed` yayılımı hiç
/// dokunulmadan çalışmaya devam ediyor.
/// </para>
///
/// <para>
/// Bunun bedeli var ve yazılı: <c>subscriptions/listen</c> için SDK
/// <b>istek filtresi</b> sunmuyor (<c>McpRequestFilters</c>'ta karşılığı yok),
/// dolayısıyla filtre <b>mesaj</b> düzeyinde ve JSON-RPC şeklini kendisi
/// okumak zorunda. Tip güvenliği bir basamak düşüyor; karşılığında ikinci bir
/// mekanizma yazılmıyor.
/// </para>
/// </summary>
public sealed class McpSubscriptionRegistry
{
    private readonly Dictionary<(McpServer Server, string Id), Abonelik> _abonelikler = [];
    private readonly object _lock = new();

    /// <summary>Açık abonelik sayısı — bekçiler bunu ölçüyor.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _abonelikler.Count;
            }
        }
    }

    /// <summary>
    /// <b>Kapsam süzgecinin kararı — saf fonksiyon.</b>
    ///
    /// <para>
    /// Ayrı ve <c>static</c> olması bilinçli, kalıp <c>ProductReadTool.ScopeRejection</c>'dan:
    /// karar akışın içine gömülü olsaydı onu ölçmenin tek yolu cevabı testte
    /// <b>tekrar yazmak</b> olurdu — yani testin kendi iddiasını doğrulaması.
    /// Kanıt <b>bu fonksiyonun</b> ölçülmesi, kullanımı ise tek yerde.
    /// </para>
    ///
    /// <para>
    /// <b>Grubu bilinmeyen değişiklik gitmiyor.</b> Boş bir <c>owner_group</c>
    /// <i>"her gruba ait"</i> değil <i>"bilinmiyor"</i> demek, ve bilinmeyeni
    /// göndermek yan kanalı geri açardı. Kalıp <see cref="McpSurface.Unspecified"/>'ınkiyle
    /// aynı: beyansız bir değer bir varsayılan değil bir rettir.
    /// </para>
    /// </summary>
    /// <param name="scope">Abonenin kapsamı.</param>
    /// <param name="ownerGroup">Değişen kaydın sahibi grup.</param>
    public static bool Delivers(AccessScope scope, string? ownerGroup)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(ownerGroup))
        {
            return false;
        }

        return scope.IsUnrestricted || scope.Allows(ownerGroup);
    }

    /// <summary>
    /// Bir aboneliği deftere yazar; dönen <see cref="IDisposable"/> siliyor.
    ///
    /// <para>
    /// Silmeyi çağırana bırakmak <b>sızıntının</b> tek karşı önlemi: akış
    /// kapandığında defterde kalan bir kayıt, kapanmış bir kanala her koşum
    /// değişiminde yazmayı denemek demek. Filtre bunu <c>finally</c> ile
    /// yapıyor, yani iptal ve istisna yollarında da kapanıyor.
    /// </para>
    /// </summary>
    public IDisposable Register(McpServer server, string subscriptionId, IReadOnlyList<string> uris, AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(uris);
        ArgumentNullException.ThrowIfNull(scope);

        var key = (server, subscriptionId);

        lock (_lock)
        {
            _abonelikler[key] = new Abonelik(uris, scope);
        }

        return new Kayit(this, key);
    }

    /// <summary>
    /// <paramref name="uri"/> için bildirim gitmesi gereken abonelikler.
    ///
    /// <para>
    /// Üç süzgeç birden: doğru sunucu, istenen adres, ve <b>kapsam</b>. Kapsamı
    /// buradan çıkarmak yan kanalı geri açardı (ölçümü
    /// <c>McpSubscriptionSideChannelTests</c>).
    /// </para>
    /// </summary>
    public IReadOnlyList<(McpServer Server, string SubscriptionId)> Matches(string uri, string? ownerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        lock (_lock)
        {
            return
            [
                .. _abonelikler
                    .Where(entry => entry.Value.Uris.Contains(uri, StringComparer.Ordinal)
                        && Delivers(entry.Value.Scope, ownerGroup))
                    .Select(static entry => (entry.Key.Server, entry.Key.Id)),
            ];
        }
    }

    /// <summary>
    /// <b>Mesaj filtresi — aboneliği yanından okuyan yer.</b>
    ///
    /// <para>
    /// <c>subscriptions/listen</c> isteğini görünce kimliğini, istediği adresleri
    /// ve çağıranın kapsamını deftere yazıyor, sonra isteği <b>SDK'nın kendi
    /// işleyicisine</b> devrediyor. Akış orada tutuluyor; biz yalnızca kim
    /// olduğunu biliyoruz.
    /// </para>
    ///
    /// <para>
    /// <b>Kimlik çözülemezse abonelik deftere GİRMİYOR.</b> Kapsamsız bir
    /// aboneliğe <c>AccessScope.Denied</c> verip yazmak da "kapalı" olurdu ama
    /// bir kayıt bırakırdı; hiç yazmamak, süzgecin varsayılanını <b>kapalı</b>
    /// tutmanın en dar hâli. İstek yine SDK'ya gidiyor — reddetmek bu filtrenin
    /// işi değil, ve reddetseydi <c>*/list_changed</c> aboneliği de ölürdü.
    /// </para>
    /// </summary>
    public McpMessageFilter Filter() => next => async (context, cancellationToken) =>
    {
        if (context.JsonRpcMessage is not JsonRpcRequest request
            || !string.Equals(request.Method, RequestMethods.SubscriptionsListen, StringComparison.Ordinal))
        {
            await next(context, cancellationToken).ConfigureAwait(false);

            return;
        }

        var uris = ResourceSubscriptions(request.Params);

        if (uris.Count == 0 || context.Server is null || !Kapsam(context, out var scope))
        {
            await next(context, cancellationToken).ConfigureAwait(false);

            return;
        }

        // `using`: akış kapandığında — normal bitişte, iptalde ve istisnada —
        // kayıt siliniyor. Sızıntının tek karşı önlemi bu.
        using var kayit = Register(context.Server, request.Id.ToString(), uris, scope);

        await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>
    /// İstenen kaynak adresleri. <b>Kendi ayrıştırıcısı yok</b>: SDK'nın kendi
    /// tipine (<see cref="SubscriptionsListenRequestParams"/>) deserialize
    /// ediliyor, yoksa istenen adreslerin okunuşu SDK'nınkinden ayrışabilirdi.
    /// </summary>
    private static IReadOnlyList<string> ResourceSubscriptions(JsonNode? parameters)
    {
        if (parameters is null)
        {
            return [];
        }

        try
        {
            var typed = parameters.Deserialize<SubscriptionsListenRequestParams>(
                McpJsonUtilities.DefaultOptions);

            return typed?.Notifications?.ResourceSubscriptions is { Count: > 0 } list ? [.. list] : [];
        }
        catch (JsonException)
        {
            // Bozuk parametre bir istemci hatası; SDK onu kendi yolunda
            // reddediyor. Burada fırlatmak, aboneliği okuyamadığımız için
            // BÜTÜN isteği düşürmek olurdu.
            return [];
        }
    }

    private static bool Kapsam(MessageContext context, out AccessScope scope)
    {
        scope = AccessScope.Denied;

        if (context.User?.Identity?.IsAuthenticated is not true)
        {
            return false;
        }

        // ÇEVRİM İKİNCİ KEZ YAZILMIYOR: REST uçlarının ve araç çağrılarının
        // geçtiği aynı çözücü (`McpCallerScope` da onu kullanıyor). Ayrışan iki
        // çevrim, aynı kişinin iki kanaldan farklı veri görmesi demek (§9).
        var resolver = (context.Services ?? context.Server?.Services)?.GetService<IAccessScopeResolver>();

        if (resolver is null)
        {
            return false;
        }

        scope = resolver.Resolve(context.User);

        return true;
    }

    private sealed record Abonelik(IReadOnlyList<string> Uris, AccessScope Scope);

    private sealed class Kayit(McpSubscriptionRegistry owner, (McpServer Server, string Id) key) : IDisposable
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
                owner._abonelikler.Remove(key);
            }
        }
    }
}

using Bizigo.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Kimliğin MCP oturumundan araca ulaştığı tek yol</b> (M08).
///
/// <para>
/// <b>Kimlik nereden geliyor.</b> SDK'nın <c>RequestContext</c>'i
/// <c>MessageContext.User</c> üzerinden çağrının <see cref="System.Security.Claims.ClaimsPrincipal"/>'ını
/// taşıyor ve SDK belgesi bunu birebir söylüyor: <i>"The user information is
/// automatically populated by the transport layer when processing incoming HTTP
/// requests in ASP.NET Core scenarios."</i> Yani HTTP taşımasında
/// <c>HttpContext.User</c> uca kadar geliyor; <b>stdio'da <c>null</c></b>.
/// </para>
///
/// <para>
/// <b>Neden yapıcıdan enjekte edilmiyor — ÖLÇÜLDÜ.</b> Araçlar
/// <c>AddOptions&lt;McpServerOptions&gt;().Configure&lt;IServiceProvider&gt;</c>
/// içinde <b>kök</b> sağlayıcıdan ve <b>bir kez</b> kuruluyor. Kapsamlı bir
/// <c>ICurrentUser</c> oradan çözülemez; çözülebilseydi de tek bir kullanıcının
/// kimliği bütün oturumlara esir bağımlılık olarak dağılırdı — yani "servis
/// hesabıyla koşan sunucu"nun kılık değiştirmiş hâli. Kimlik <b>çağrı başına</b>
/// taşınmak zorunda ve bu sınıf o taşımayı yapıyor.
/// </para>
///
/// <para>
/// <b>Çevrim burada değil.</b> Claim → <c>owner_group</c> çevrimi REST
/// uçlarının kullandığı <see cref="IAccessScopeResolver"/>'ın kendisi
/// (<c>AccessScopeResolver</c> / <c>GroupMapping</c>). İkinci bir çevrim
/// yazmak §9'un adıyla yasakladığı şey olurdu: ayrışan iki çevrim, aynı kişinin
/// REST'ten ve MCP'den <b>farklı veri görmesi</b> demek.
/// </para>
/// </summary>
public static class McpCallerScope
{
    /// <summary>
    /// stdio taşımasında kimlik olmadığını söyleyen mesaj. Sabit, çünkü bekçi
    /// bunun <b>sebebi söylediğini</b> ölçüyor: "sonuç yok" ile "kimlik yok"
    /// aynı cümleye düşerse istemci ilkini varsayar.
    /// </summary>
    public const string NoIdentityMessage =
        "Bu araç çağıranın kimliğini istiyor ve oturumda kimlik yok. "
        + "Akışlanabilir HTTP taşıması kimliği taşıyor; stdio taşıması MCP yetkilendirme "
        + "spesifikasyonunun dışında ve kimliği ortamdan bekliyor.";

    /// <summary>
    /// Çağrının kapsamı — ya da <b>ret</b>.
    ///
    /// <para>
    /// <b>Boş sonuç dönmüyor, reddediyor.</b> Kimliksiz bir çağrıya
    /// <see cref="AccessScope.Denied"/> verip aracı koşturmak "kapalı" olurdu
    /// ama <b>sessiz</b>: <c>logs.search</c> sıfır satır döndürünce ajan bunu
    /// <i>"eşleşme yok"</i> diye okur ve kimliğin kaybolduğunu hiç görmez.
    /// §7'nin en pahalı sınıfı tam olarak bu.
    /// </para>
    ///
    /// <para>
    /// <b>Jenerik, ve bu M07'de ölçülerek oldu.</b> İmza
    /// <c>RequestContext&lt;CallToolRequestParams&gt;</c> ile yazılmıştı, yani
    /// yalnızca araç kanalına uyuyordu. Kaynak kanalı
    /// (<c>ReadResourceRequestParams</c>) <b>ikinci bir model kanalı</b> ve
    /// kimliği aynı yerden almak zorunda; imzayı jenerikleştirmek yerine orada
    /// ikinci bir çözüm yazmak, bu deponun §9'da adı konmuş hatası olurdu —
    /// ayrışan iki kimlik kapısı, aynı kişinin iki kanaldan <b>farklı veri
    /// görmesi</b> demek. Gövde değişmedi; yalnızca hangi isteklere
    /// uygulanabildiği genişledi.
    /// </para>
    /// </summary>
    /// <typeparam name="TParams">İsteğin parametre tipi — araç çağrısı, kaynak okuması, …</typeparam>
    /// <param name="request">SDK'nın istek bağlamı.</param>
    /// <param name="scope">Çözülen kapsam; ret hâlinde <see cref="AccessScope.Denied"/>.</param>
    /// <returns>Ret sebebi, ya da kapsam çözüldüyse <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Kapsam çözücüsü kayıtlı değil. <b>Araç hatası değil</b>: bu istemcinin
    /// yaptığı bir şey değil, sunucunun yanlış kurulması. Araç hatasına
    /// çevirmek onu <i>"yetkiniz yok"</i> gibi okutur ve yanlış yerde aranır.
    /// Kurulum sırasında da ayrıca yakalanıyor (<c>BizigoMcpServer.Apply</c>);
    /// burası son savunma.
    /// </exception>
    public static McpToolError? Resolve<TParams>(
        RequestContext<TParams> request,
        out AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);

        scope = AccessScope.Denied;

        if (request.User?.Identity?.IsAuthenticated is not true)
        {
            // SEBEP SORULUYOR — ve bu M13'ün eklediği tek satırlık ayrım.
            //
            // HTTP'de kimlik yokluğunun tek sebebi var: istemci belirteç
            // göndermedi. stdio'da İKİ sebep var ve ikisi zıt iş gerektiriyor —
            // ortamda belirteç hiç yok (yapılandırma) ya da vardı ve süresi
            // doldu (yeni belirteç). İkisi aynı cümleye düşerse okuyan kişi
            // yanlış yere bakar.
            //
            // `GetService`, `GetRequiredService` DEĞİL: HTTP tarafında bu servis
            // kayıtlı değil ve olmaması doğru. Bulunmazsa eski cümlede kalıyor.
            var refusal = (request.Services ?? request.Server?.Services)
                ?.GetService<IMcpIdentityRefusal>()
                ?.Reason;

            return new McpToolError(
                McpToolError.Unauthenticated,
                string.IsNullOrWhiteSpace(refusal) ? NoIdentityMessage : refusal);
        }

        var resolver = (request.Services ?? request.Server?.Services)
            ?.GetService<IAccessScopeResolver>()
            ?? throw new InvalidOperationException(MissingResolverMessage);

        scope = resolver.Resolve(request.User);

        return null;
    }

    /// <summary>
    /// Kurulum kapısının ve son savunmanın <b>aynı</b> cümlesi. İki ayrı metin
    /// yazmak, aynı kusurun iki farklı arıza gibi okunması demek olurdu.
    /// </summary>
    internal const string MissingResolverMessage =
        "Kimlik isteyen bir MCP aracı ilan edildi ama `IAccessScopeResolver` DI'ya kaydedilmemiş. "
        + "Kapsam çözücüsüz bir ürün yüzeyi ya her şeyi açar ya hiçbir şeyi döndürmez; ikisi de "
        + "sessizce yanlıştır. `AddBizigoAuthentication` bunu kaydediyor — MCP'yi barındıran süreç "
        + "onu çağırmalı, ya da aracı `RequiresCallerIdentity` ile gerekçeli olarak muaf tutmalı.";
}

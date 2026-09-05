using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Commands.Mcp;

/// <summary>
/// Komut araçlarının bağımlılıkları — <b>tek kayıt, iki host</b>.
///
/// <para>
/// <b>M01'in bıraktığı boşluk buydu:</b> <c>McpCommandHandlers.ServeAsync</c>
/// boş bir <c>ServiceCollection</c> kuruyordu ve <c>server.info</c> bağımlılık
/// istemediği için yetiyordu. Ürün araçları gelince yetmiyor.
/// </para>
///
/// <para>
/// <b>Neden bir uzantı ve neden iki yerde çağrılıyor:</b> stdio host'u
/// (<c>bizigo mcp serve</c>) ve HTTP host'u (<c>Bizigo.Api</c>) <b>aynı</b>
/// araçları ilan ediyor, dolayısıyla aynı grafiğe ihtiyaç duyuyorlar. İki ayrı
/// kayıt yazılsaydı biri güncellenip diğeri eski kalırdı ve bir araç bir
/// taşımada çalışıp diğerinde <c>Instantiate</c> ile patlardı — aynı sunucunun
/// iki farklı gerçeği.
/// </para>
///
/// <para>
/// <b>Kurulamayan araç atlanmıyor, patlıyor</b> (M01 kararı). Yani eksik bir
/// kayıt sessiz değil; ama arıza aracın dosyasında görünür, sebebi burada
/// durur. Bu yorum o mesafeyi kapatmak için.
/// </para>
/// </summary>
public static class CommandToolsSetup
{
    /// <summary>
    /// Komut araçlarının istediği servisleri kaydeder.
    /// </summary>
    /// <remarks>
    /// <see cref="ParserToolbox"/> <b>singleton</b>: kurulumu pahalı (grok
    /// kütüphanesi ve eşleme tabloları diskten okunuyor) ve süreç boyunca
    /// değişmiyor. <c>TryAdd</c> kullanılıyor ki iki host da çağırabilsin ve
    /// bir çağrı diğerinin kaydını sessizce ezmesin.
    /// </remarks>
    public static IServiceCollection AddBizigoCommandTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => ParserToolbox.Create(
            patternDirectory: null,
            mappingDirectory: null));

        return services;
    }
}

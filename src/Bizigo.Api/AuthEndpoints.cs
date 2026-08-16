using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Bizigo.ControlPlane;

namespace Bizigo.Api;

/// <summary>
/// BFF uçları (F1 §10.1.2).
///
/// <para>
/// Tarayıcı hiçbir zaman token görmüyor: <c>/auth/login</c> OIDC akışını
/// başlatıyor, dönüşte oturum çerezi yazılıyor, token sunucuda kalıyor. React
/// tarafı yalnızca <c>/auth/me</c> ile "kimim ve neyi görebilirim" diye soruyor.
/// </para>
///
/// <para>
/// <b>Ters vekil (YARP) henüz yok</b> — UI de yok. Vekil, UI geldiğinde bu
/// şemanın üstüne eklenecek; kimlik tarafı şimdiden doğru kurulduğu için o adım
/// yalnızca yönlendirme işi olacak.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/auth/login", (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) },
            [OpenIdConnectDefaults.AuthenticationScheme]));

        routes.MapPost("/auth/logout", () => Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

        // Kimliğin ve KAPSAMIN tek doğrulama noktası. UI'ın "hangi grupları
        // görebiliyorum" sorusunun cevabı buradan geliyor; claim'leri kendi
        // yorumlaması istenmiyor.
        routes.MapGet("/auth/me", (ICurrentUser user) =>
        {
            var principal = user.Principal;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }

            var scope = user.Scope;

            return Results.Ok(new
            {
                subject = scope.Subject,
                username = principal.FindFirst(BizigoClaims.PreferredUsername)?.Value ?? string.Empty,
                roles = principal.FindAll(BizigoClaims.Roles).Select(c => c.Value).ToArray(),
                idp_groups = principal.FindAll(BizigoClaims.Groups).Select(c => c.Value).ToArray(),
                owner_groups = scope.OwnerGroups.OrderBy(g => g, StringComparer.Ordinal).ToArray(),
                unrestricted = scope.IsUnrestricted,
                // Eşleme eksikse kullanıcı hiçbir veri göremez. Bunu sessiz
                // bırakmak "sistem bozuk" ile "yetkiniz yok"u ayırt edilemez kılar.
                sees_nothing = scope.IsEmpty,
            });
        });

        return routes;
    }

    /// <summary>
    /// Açık yönlendirme (open redirect) koruması: yalnızca uygulama içi göreli
    /// yollar kabul ediliyor.
    /// </summary>
    private static string SafeReturn(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}

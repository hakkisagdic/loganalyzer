using System.Text.Json.Serialization;
using Bizigo.ControlPlane;

namespace Bizigo.Api;

/// <summary>
/// Kimlik yüzeyi — <b>tek uç</b>: <c>/auth/me</c> (K31).
///
/// <para>
/// <c>/auth/login</c> ve <c>/auth/logout</c> buradan <b>kaldırıldı</b>. Tarayıcı
/// akışının tamamı Next.js BFF'inde (<c>ui/src/lib/auth/</c>): OIDC yönlendirmesi,
/// oturum çerezi, token yenileme ve çıkış orada. API artık kimseyi Keycloak'a
/// yönlendirmiyor; her isteği <c>Authorization: Bearer</c> ile karşılıyor.
/// </para>
///
/// <para>
/// <c>/auth/me</c> kalıyor çünkü BFF'in kimlik sorgusu bu: kapsam çözümü
/// (<c>AccessScopeResolver</c>) kontrol düzleminin eşleme tablosunda ve BFF'in
/// onu kopyalaması, iki yerde ayrışabilen ikinci bir yorum demek olurdu.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

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

            return Results.Ok(new AuthMeResponse(
                Subject: scope.Subject,
                Username: principal.FindFirst(BizigoClaims.PreferredUsername)?.Value ?? string.Empty,
                Roles: [.. principal.FindAll(BizigoClaims.Roles).Select(c => c.Value)],
                IdpGroups: [.. principal.FindAll(BizigoClaims.Groups).Select(c => c.Value)],
                OwnerGroups: [.. scope.OwnerGroups.OrderBy(g => g, StringComparer.Ordinal)],
                Unrestricted: scope.IsUnrestricted,
                // Eşleme eksikse kullanıcı hiçbir veri göremez. Bunu sessiz
                // bırakmak "sistem bozuk" ile "yetkiniz yok"u ayırt edilemez kılar.
                SeesNothing: scope.IsEmpty));
        })
        .WithName("AuthMe")
        .Produces<AuthMeResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}

/// <summary>
/// <c>/auth/me</c> gövdesi. Anonim nesne yerine adlandırılmış tip: OpenAPI
/// belgesine şema olarak inmesi gerekiyor, yoksa T14'ün ürettiği TypeScript
/// tarafında <c>unknown</c> kalıyor.
///
/// <para>
/// <c>JsonPropertyName</c> nitelikleri <b>zorunlu</b>: alan adları F1'de yayına
/// giren gövdenin birebir aynısı olmalı. Varsayılan camelCase politikası
/// <c>idp_groups</c>'u <c>idpGroups</c> yapar ve sözleşmeyi sessizce kırar.
/// </para>
/// </summary>
public sealed record AuthMeResponse(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("roles")] string[] Roles,
    [property: JsonPropertyName("idp_groups")] string[] IdpGroups,
    [property: JsonPropertyName("owner_groups")] string[] OwnerGroups,
    [property: JsonPropertyName("unrestricted")] bool Unrestricted,
    [property: JsonPropertyName("sees_nothing")] bool SeesNothing);

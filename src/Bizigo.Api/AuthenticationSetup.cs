using System.Security.Claims;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bizigo.Api;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><see langword="false"/> yalnızca yerel geliştirme içindir.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Keycloak realm adresi (issuer).</summary>
    public string Authority { get; set; } = "http://localhost:8180/realms/bizigo";

    /// <summary>Token'ın <c>aud</c> claim'i. Keycloak varsayılanı <c>account</c>.</summary>
    public string Audience { get; set; } = "account";

    /// <summary>Yerel geliştirmede Keycloak düz HTTP konuşuyor.</summary>
    public bool RequireHttpsMetadata { get; set; }

    public string ClientId { get; set; } = "bizigo-ui";

    public string ClientSecret { get; set; } = string.Empty;
}

public static class BizigoAuthPolicies
{
    public const string Ingest = "bizigo:ingest";
    public const string Read = "bizigo:read";
    public const string Author = "bizigo:author";
    public const string Admin = "bizigo:admin";
}

public static class AuthenticationSetup
{
    /// <summary>
    /// İki şema bir arada (F1 §10.1.2):
    ///
    /// <list type="bullet">
    /// <item><b>JWT Bearer</b> — makineler (collector) ve API istemcileri.
    /// Keycloak'ın imzaladığı erişim token'ı doğrulanıyor; ürün kendi kullanıcı
    /// tablosunu tutmuyor.</item>
    /// <item><b>Cookie + OIDC</b> — tarayıcı. Authorization code + PKCE, token
    /// <b>tarayıcıda saklanmıyor</b>; BFF deseninin bütün amacı bu.</item>
    /// </list>
    ///
    /// <para>
    /// Varsayılan şema Bearer: kimliksiz bir API isteği 401 alıyor, giriş
    /// sayfasına yönlendirilmiyor. Tarayıcı akışı yalnızca açıkça <c>/auth/login</c>
    /// çağrıldığında devreye giriyor.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBizigoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddSingleton<AccessScopeResolver>();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddHttpContextAccessor();

        if (!options.Enabled)
        {
            // Kimlik kapalıyken bile kapsam KAPALI başlıyor: `AccessScope.Denied`.
            // "Kimlik yoksa her şeyi gör" varsayılanı bu üründe yapılabilecek en
            // pahalı hata olurdu (K17).
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie();
            services.AddAuthorization();
            return services;
        }

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                // Claim sözleşmesinin ÇALIŞMASI bu satıra bağlı. Varsayılan
                // `true` iken handler gelen claim'leri Microsoft'un uzun URI
                // şemasına çeviriyor: `sub` → `.../nameidentifier`, `roles` →
                // görünmez oluyor. Aşağıdaki `RoleClaimType`/`NameClaimType`
                // ayarları o durumda hiçbir şeye denk gelmiyor.
                //
                // Ölçülen hâli: collector'ın `roles: ["ingest"]` taşıyan
                // token'ıyla `/v1/logs` 403 dönüyordu ve `/auth/me` `roles: []`
                // gösteriyordu. `sub`'ın yine de bulunması yanıltıcıydı —
                // `AccessScopeResolver` onu `ClaimTypes.NameIdentifier`
                // yedeğinden okuyordu, yani eşleme zaten devredeydi.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Authority,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // Claim sözleşmesi (F1 §10.1.1): rol ve ad claim'leri düz
                    // adlarıyla okunuyor, Microsoft'un uzun URI şemasıyla değil.
                    RoleClaimType = BizigoClaims.Roles,
                    NameClaimType = BizigoClaims.PreferredUsername,
                };
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
            {
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;

                // Authorization code + PKCE. Implicit/hybrid akış YOK.
                oidc.ResponseType = "code";
                oidc.UsePkce = true;

                // Token cookie'nin içinde sunucu tarafında kalıyor; tarayıcıya
                // yalnızca oturum çerezi gidiyor.
                oidc.SaveTokens = true;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                oidc.Scope.Add("email");

                // JWT tarafındaki gerekçenin aynısı: eşleme açıkken `roles` ve
                // `groups` uzun URI'lere çevriliyor ve aşağıdaki claim tipleri
                // hiçbir şeye denk gelmiyor. Tarayıcı akışı bu oturumda uçtan
                // uca koşulmadı, ama iki işleyicinin claim sözleşmesi aynı
                // olmak zorunda — ayrışırlarsa kapsam, kullanıcının hangi yoldan
                // girdiğine göre değişir.
                oidc.MapInboundClaims = false;

                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    RoleClaimType = BizigoClaims.Roles,
                    NameClaimType = BizigoClaims.PreferredUsername,
                };

                // `groups` için ayrı bir claim eşlemesi YOK: realm'deki mapper
                // onu id_token'a da yazıyor (`id.token.claim: true`) ve OIDC
                // işleyicisi id_token claim'lerini olduğu gibi taşıyor. Userinfo
                // eşlemesi eklemek aynı claim'i ikinci kez üretirdi.
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(BizigoAuthPolicies.Ingest, p => p.RequireRole(BizigoRoles.Ingest))
            .AddPolicy(BizigoAuthPolicies.Read, p => p.RequireRole(
                BizigoRoles.Reader, BizigoRoles.Analyst, BizigoRoles.Author, BizigoRoles.Admin))
            .AddPolicy(BizigoAuthPolicies.Author, p => p.RequireRole(
                BizigoRoles.Author, BizigoRoles.Admin))
            .AddPolicy(BizigoAuthPolicies.Admin, p => p.RequireRole(BizigoRoles.Admin));

        return services;
    }
}

/// <summary>İstekteki kimliğin kapsam karşılığı. Uçlar bunu görüyor, claim'leri değil.</summary>
public interface ICurrentUser
{
    Contracts.AccessScope Scope { get; }

    ClaimsPrincipal? Principal { get; }
}

public sealed class HttpContextCurrentUser(
    IHttpContextAccessor accessor,
    AccessScopeResolver resolver) : ICurrentUser
{
    public ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Contracts.AccessScope Scope => resolver.Resolve(Principal);
}

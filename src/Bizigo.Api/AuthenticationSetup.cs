using System.Security.Claims;
using System.Text.Encodings.Web;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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

    // İstemci kimliği ve gizli anahtarı BURADA DEĞİL: K31 ile OIDC akışı Next.js
    // BFF'ine taşındı. `bizigo-ui` gizli anahtarı yalnızca orada duruyor; API
    // kimseyi Keycloak'a yönlendirmiyor, yalnızca gelen token'ı doğruluyor.
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
    /// <b>Tek şema: JWT Bearer.</b> API saf kaynak sunucusu (K31).
    ///
    /// <para>
    /// Cookie ve OIDC işleyicileri buradan <b>kaldırıldı</b>. Tarayıcı akışının
    /// tamamı — authorization code + PKCE, oturum çerezi, token yenileme —
    /// Next.js BFF'inde (<c>ui/</c>). API'ye gelen her istek, insan ya da makine,
    /// <c>Authorization: Bearer</c> taşıyor.
    /// </para>
    ///
    /// <para>
    /// Gerekçe: iki yerde oturum yönetimi, kullanıcının hangi yoldan girdiğine
    /// göre farklı davranan bir kapsam demekti. İki işleyicinin claim sözleşmesi
    /// ayrışırsa aynı kişi tarayıcıdan ve token'la farklı veri görür.
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
            //
            // Eskiden burada bir cookie işleyicisi vardı; tek işi varsayılan şema
            // boş kalmasın diyeydi. Kaldırıldı — yerine kimliksiz kalan ve 401
            // döndüren bir işleyici geldi, çünkü bu üründe artık hiçbir yerde
            // cookie tabanlı kimlik yok (K31) ve "yerel geliştirmede duran"
            // bir cookie işleyicisi o kuralı sessizce deler.
            services
                .AddAuthentication(AnonymousAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AnonymousAuthenticationHandler>(
                    AnonymousAuthenticationHandler.SchemeName, _ => { });
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

/// <summary>
/// <c>Auth:Enabled=false</c> iken devreye giren işleyici: <b>hiçbir zaman kimlik
/// üretmiyor</b>, meydan okuma olarak düz 401 dönüyor.
///
/// <para>
/// Varlık sebebi teknik: varsayılan bir şema kayıtlı olmazsa yetki isteyen bir
/// uç 500 verir ("No authenticationScheme was specified"). Eskiden bu boşluğu
/// bir cookie işleyicisi dolduruyordu; K31 sonrası üründe cookie tabanlı kimlik
/// kalmadığı için yerine bu geldi. Yönlendirme yok — API hiçbir koşulda giriş
/// sayfasına yollamıyor.
/// </para>
/// </summary>
public sealed class AnonymousAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "BizigoAnonymous";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
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

using System.Security.Claims;
using System.Text.Encodings.Web;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Authentication;

namespace Bizigo.Api;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><see langword="false"/> yalnızca yerel geliştirme içindir.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Keycloak realm adresi (issuer).</summary>
    public string Authority { get; set; } = "http://localhost:8180/realms/bizigo";

    /// <summary>
    /// Anahtarların <b>indirileceği</b> adres — issuer'dan ayrı.
    ///
    /// <para>
    /// Boş bırakıldığında handler bunu <see cref="Authority"/>'den türetiyor ve
    /// tek makinede koşan bir kurulumda doğru davranış da bu. Ayrılması
    /// container'lı kurulumun zorunlu kıldığı bir ayrım: Keycloak issuer'ı
    /// <c>KC_HOSTNAME</c> ile <c>http://localhost:8180</c>'e <b>sabitliyor</b>
    /// (tarayıcı oraya gidiyor ve token'da yazan da o), ama API container'ının
    /// içinde <c>localhost:8180</c> Keycloak değil container'ın kendisi.
    /// Metadata adresi ayrılmadan API açılışta anahtarları hiç indiremiyor ve
    /// her token'ı reddediyor.
    /// </para>
    ///
    /// <para>
    /// <b>Issuer doğrulaması bundan etkilenmiyor:</b> <c>ValidIssuer</c> hâlâ
    /// <see cref="Authority"/>. Yani "anahtarı nereden alıyorum" ile "kime
    /// güveniyorum" ayrı sorular ve ikincisinin cevabı değişmedi — aksi hâlde
    /// ağ içinden erişilebilen herhangi bir IdP kabul edilir olurdu.
    /// </para>
    /// </summary>
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>
    /// API'nin kaynak kimliği — token'ın <c>aud</c> claim'i.
    ///
    /// <para>
    /// <b>Varsayılanı yok ve olmamalı.</b> Burada bir zamanlar
    /// <c>= "account"</c> yazıyordu — Keycloak'ın varsayılan kitlesi. Sevk
    /// edilen yapılandırma onu <c>bizigo-api</c> ile eziyordu, yani varsayılan
    /// hiç devreye girmiyordu; ama <c>Auth:Audience</c> yazılmayan bir dağıtım
    /// <b>sessizce</b> Keycloak'ın varsayılan kitlesine doğrular ve hata
    /// vermezdi. Doğru yapılandırılmış olmak, yanlış yapılandırılamayacağı
    /// anlamına gelmiyor (<c>CLAUDE.md</c> §7).
    /// </para>
    ///
    /// <para>
    /// Eksikse <see cref="AuthenticationSetup.AddBizigoAuthentication"/>
    /// <b>açılışta</b> patlıyor — çağrı anında zayıf doğrulama yapmıyor.
    /// </para>
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// <b>MCP sunucusunun kendi kaynak kimliği</b> — <see cref="Audience"/>'tan
    /// ayrı, ve ayrılmasının sebebi RFC 8707.
    ///
    /// <para>
    /// Spesifikasyon token'ın <b>o kaynağa</b> düzenlenmiş olmasını istiyor.
    /// İkisi aynı değer olsaydı, API için basılmış bir token MCP sunucusunda
    /// <b>yeniden kullanılabilirdi</b> — kaynak bağlamanın engellemek istediği
    /// şey tam olarak bu. Ayrı bir değer, MCP oturumu için ayrıca istenmiş bir
    /// token gerektiriyor.
    /// </para>
    ///
    /// <para>
    /// <see cref="Audience"/> gibi varsayılansız: eksikse açılışta patlıyor.
    /// </para>
    /// </summary>
    public string McpResource { get; set; } = string.Empty;

    /// <summary>Yerel geliştirmede Keycloak düz HTTP konuşuyor.</summary>
    public bool RequireHttpsMetadata { get; set; }

    // İstemci kimliği ve gizli anahtarı BURADA DEĞİL: K31 ile OIDC akışı Next.js
    // BFF'ine taşındı. `bizigo-ui` gizli anahtarı yalnızca orada duruyor; API
    // kimseyi Keycloak'a yönlendirmiyor, yalnızca gelen token'ı doğruluyor.
}

/// <summary>
/// <b>Adlandırılmış kimlik şemaları.</b>
///
/// <para>
/// MCP kendi şemasını taşıyor ve varsayılanı <b>değiştirmiyor</b>. Gerekçe
/// ölçüldü: bu API'de <b>45</b> <c>RequireAuthorization</c> çağrısı var, 14 uç
/// dosyasına dağılmış, ve <b>hiçbiri</b> kendi kimlik şemasını belirtmiyor —
/// hepsi <c>DefaultChallengeScheme</c>'e bağlı. SDK'nın önerdiği
/// <c>AddMcp(builder, configure)</c> yolu varsayılanı değiştiriyor, yani o 45
/// ucun tamamının 401 davranışını değiştirir ve BFF'in oturum akışını (K31)
/// ilgilendirir.
/// </para>
///
/// <para>
/// SDK'nın <c>AddMcp(builder, <b>scheme</b>, displayName, configure)</c>
/// aşırı yüklemesi bu ayrımı mümkün kılıyor — ölçüldü, varsayıldı değil.
/// </para>
/// </summary>
public static class BizigoAuthSchemes
{
    /// <summary>MCP kimlik şeması: meydan okumayı üretir, doğrulamayı iletir.</summary>
    public const string Mcp = "bizigo-mcp";

    /// <summary>
    /// MCP'nin token doğrulayıcısı — ayrı bir JWT şeması, çünkü <b>kitlesi
    /// farklı</b> (<see cref="AuthOptions.McpResource"/>). Varsayılan JWT
    /// şeması API'nin kitlesini doğruluyor ve o değişmiyor.
    /// </summary>
    public const string McpBearer = "bizigo-mcp-bearer";
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

        // M08 · MCP araç çağrıları kapsamı BURADAN alıyor.
        //
        // Aynı örnek, ikinci bir yüzey için ikinci bir isim değil: kayıt
        // `sp.GetRequiredService<AccessScopeResolver>()`'a bağlanıyor, yani
        // `ICurrentUser`'ın gördüğü çevrim ile MCP'nin gördüğü çevrim AYNI
        // nesne. `AddSingleton<IAccessScopeResolver, AccessScopeResolver>()`
        // yazsaydık ikinci bir örnek doğardı ve `RefreshAsync` yalnızca birini
        // tazelerdi — eşleme tablosu güncellendiğinde REST ile MCP'nin farklı
        // kapsam vermesi demek, üstelik sessizce (§9: "ikinci kopya yazma").
        services.AddSingleton<Contracts.IAccessScopeResolver>(
            static sp => sp.GetRequiredService<AccessScopeResolver>());

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

        Require(options.Audience, $"{AuthOptions.SectionName}:Audience");
        Require(options.McpResource, $"{AuthOptions.SectionName}:McpResource");

        services
            .AddAuthentication(auth =>
            {
                // DEĞİŞMİYOR. 45 `RequireAuthorization` çağrısının hiçbiri kendi
                // şemasını belirtmiyor, yani buraya dokunmak API'nin TAMAMININ
                // 401 davranışını değiştirir. MCP kendi adlandırılmış şemasını
                // alıyor (`BizigoAuthSchemes`).
                auth.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                auth.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwt => ConfigureBearer(jwt, options, options.Audience))

            // MCP'nin token doğrulayıcısı: AYNI issuer, AYNI claim sözleşmesi,
            // FARKLI kitle. Tek fark bu ve tek olması gerekiyor — ikinci bir
            // doğrulama yolu yazmak §9'un yasakladığı kopya olurdu.
            .AddJwtBearer(
                BizigoAuthSchemes.McpBearer,
                jwt => ConfigureBearer(jwt, options, options.McpResource))

            // Meydan okumayı MCP işleyicisi üretiyor: 401 gövdesine
            // `WWW-Authenticate: Bearer resource_metadata="…"` koyuyor ve
            // `/.well-known/oauth-protected-resource` belgesini kendisi
            // sunuyor (RFC 9728). Doğrulamayı yukarıdaki şemaya İLETİYOR —
            // yani token doğrulama mantığı tek yerde kalıyor.
            .AddMcp(
                BizigoAuthSchemes.Mcp,
                "Bizigo MCP",
                mcp =>
                {
                    mcp.ForwardAuthenticate = BizigoAuthSchemes.McpBearer;
                    mcp.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = options.McpResource,
                        AuthorizationServers = { options.Authority },
                        ScopesSupported = { McpScope },

                        // Realm'de YALNIZCA `bizigo-claims` client scope var ve
                        // yerleşik `profile`/`email` hiç oluşturulmamış:
                        // `openid profile email` canlıda `invalid_scope` alıyor.
                        // Ölçüldü (CLAUDE.md §12) — burada ilan edilen kapsam
                        // istemciyi var olmayan bir scope istemeye göndermemeli.
                        BearerMethodsSupported = { "header" },
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

    /// <summary>
    /// MCP oturumu için istenen kapsam. Realm'de var olan tek client scope
    /// <c>bizigo-claims</c>; <c>openid</c> geçiyor, <c>profile email</c>
    /// canlıda <c>invalid_scope</c> alıyor (ölçüldü).
    /// </summary>
    private const string McpScope = "openid";

    /// <summary>
    /// Yapılandırma eksikse <b>açılışta</b> patlıyor.
    ///
    /// <para>
    /// Emsali M08'in çözücüsü: kayıtlı değilse kurulumda patlıyor, çağrıda
    /// değil. Sessizce zayıf doğrulama yapan bir varsayılan, bu deponun §7'de
    /// tarif ettiği sınıfa giriyor — hata yok, sayaç yok, belirti yok.
    /// </para>
    /// </summary>
    private static void Require(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"`{key}` yapılandırılmadı. Kimlik açıkken bu değerin varsayılanı YOK: " +
                "eksik bırakmak, token'ın kitlesini doğrulamadan kabul etmeye ya da " +
                "sağlayıcının varsayılan kitlesine sessizce güvenmeye yol açardı.");
        }
    }

    /// <summary>
    /// İki JWT şemasının <b>ortak</b> yapılandırması. Aralarındaki tek fark
    /// <paramref name="audience"/> — ve tek fark olması gerekiyor: ikinci bir
    /// doğrulama yolu, iki şemanın claim sözleşmesinin sessizce ayrışması
    /// demek olurdu.
    /// </summary>
    private static void ConfigureBearer(JwtBearerOptions jwt, AuthOptions options, string audience)
    {
        jwt.Authority = options.Authority;
        jwt.Audience = audience;
        jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

        // Yalnızca AÇIKÇA verildiğinde ayarlanıyor. Boş bir değer atamak
        // handler'ın kendi türetmesini bastırır ve tek makinede koşan kurulum
        // anahtarları hiç bulamaz.
        if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
        {
            jwt.MetadataAddress = options.MetadataAddress;
        }

        // Claim sözleşmesinin ÇALIŞMASI bu satıra bağlı. Varsayılan `true` iken
        // handler gelen claim'leri Microsoft'un uzun URI şemasına çeviriyor:
        // `sub` → `.../nameidentifier`, `roles` → görünmez oluyor. Aşağıdaki
        // `RoleClaimType`/`NameClaimType` ayarları o durumda hiçbir şeye denk
        // gelmiyor.
        //
        // Ölçülen hâli: collector'ın `roles: ["ingest"]` taşıyan token'ıyla
        // `/v1/logs` 403 dönüyordu ve `/auth/me` `roles: []` gösteriyordu.
        // `sub`'ın yine de bulunması yanıltıcıydı — `AccessScopeResolver` onu
        // `ClaimTypes.NameIdentifier` yedeğinden okuyordu, yani eşleme zaten
        // devredeydi.
        jwt.MapInboundClaims = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Authority,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            // SAAT KAYMASI TOLERANSI — AÇIKÇA SEÇİLDİ (M19).
            //
            // Buraya kadar ayarsızdı, yani kütüphanenin varsayılanı olan BEŞ
            // DAKİKA geçerliydi ve `ValidateLifetime = true` yazmak bunu
            // görmüyordu. Kusur sayının büyüklüğü değil, kimsenin SEÇMEMİŞ
            // olmasıydı.
            //
            // ÖLÇÜLDÜ: realm'in `accessTokenLifespan` değeri 900 s
            // (`deploy/keycloak/realm-bizigo.json`). Yani beş dakikalık tolerans
            // belirtecin ömrünün ÜÇTE BİRİ kadar — etkin ömrü 900 s'den 1200
            // s'ye çıkarıyor, %33 fazlası. Bu ürün müşterinin log'unu okuyor;
            // süresi dolmuş bir oturumun fazladan beş dakika yaşaması,
            // kimsenin seçmediği bir güvenlik penceresi.
            //
            // NEDEN SIFIR DEĞİL — ve burası stdio'dan AYRILIYOR. Kaymayı kim
            // düzeltiyor sorusunun cevabı: doğrulama `exp`'i (Keycloak yazıyor)
            // API'nin saatiyle karşılaştırıyor, yani ilgili çift
            // Keycloak↔API — istemcinin saati bu hesaba HİÇ girmiyor, o yalnızca
            // belirteci taşıyor. Compose'da ikisi aynı Docker host'unun saatini
            // paylaşıyor; ayrı host'larda NTP saniyenin altında tutuyor.
            //
            // Ama sıfır tolerans yalnızca `exp`'i değil `nbf`'i de sıkıyor ve
            // TEHLİKELİ YÖN ORASI: saati API'den ileri olan bir Keycloak'ın
            // bastığı belirteç "henüz geçerli değil" diye reddedilir, ve
            // yenileme BUNU ÇÖZMEZ — yeni belirteç de aynı sorunu taşır.
            // `exp` tarafında yenileme bir kurtarma yolu (BFF'in `refresh()`'i
            // var, `ui/src/lib/auth/oidc.ts`), `nbf` tarafında yok.
            //
            // 30 s: NTP sınıfı kaymanın yüzlerce katı, ama ömrün otuzda biri.
            // stdio'nun SIFIR'ı ise zorunlu ve gerekçesi ayrı: orada yenileme
            // yok, süreç yeni belirteçle yeniden başlatılıyor, ve bir lütuf
            // penceresi süre sonu ânını gözlemlenemez kılıyor.
            ClockSkew = TimeSpan.FromSeconds(30),

            // Claim sözleşmesi (F1 §10.1.1): rol ve ad claim'leri düz adlarıyla
            // okunuyor, Microsoft'un uzun URI şemasıyla değil.
            RoleClaimType = BizigoClaims.Roles,
            NameClaimType = BizigoClaims.PreferredUsername,
        };
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

using System.Globalization;
using System.Security.Claims;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Bizigo.Cli;

/// <summary>
/// stdio kimliğinin ayarları — <b>ortamdan</b>, çünkü bu süreci bir masaüstü
/// MCP istemcisi başlatıyor ve onun verdiği kanal <c>env</c>.
/// </summary>
/// <param name="Token">Keycloak erişim belirteci.</param>
/// <param name="Authority">
/// Güvenilen issuer. Doğrulanması şart: doğrulanmazsa ağ içinden erişilebilen
/// <b>herhangi</b> bir IdP kabul edilir olur — <c>AuthOptions.MetadataAddress</c>
/// belgesinin uyardığı hâl.
/// </param>
/// <param name="Resource">
/// Beklenen <c>aud</c> — MCP sunucusunun <b>kendi</b> kaynak kimliği
/// (<c>Auth:McpResource</c>, M09). API'nin kitlesiyle aynı değil ve olmaması
/// kararın kendisi.
/// </param>
/// <param name="MetadataAddress">
/// Anahtarların indirileceği adres; boşsa <see cref="Authority"/>'den
/// türetiliyor. Ayrı olmasının gerekçesi API tarafında ölçüldü: container
/// içinde issuer ile erişilebilir adres ayrışıyor.
/// </param>
public sealed record McpStdioIdentitySettings(
    string? Token,
    string? Authority,
    string? Resource,
    string? MetadataAddress)
{
    /// <summary>Belirtecin ortam değişkeni.</summary>
    /// <remarks>
    /// <c>BIZIGO_MCP_API_TOKEN</c>'dan <b>ayrı</b> ve ayrı olması şart: o
    /// <c>McpKeycloakIdentityTests</c>'in <i>istemci</i> belirteci (API için
    /// basılmış, <c>/mcp</c>'de reddedilmesi ölçülen şey), bu ise
    /// <i>sunucunun okuduğu</i> belirteç. Aynı ada koymak ikisini bir sanmak
    /// olurdu.
    /// </remarks>
    public const string TokenVariable = "BIZIGO_MCP_TOKEN";

    /// <summary>Issuer'ın ortam değişkeni.</summary>
    public const string AuthorityVariable = "BIZIGO_MCP_AUTHORITY";

    /// <summary>Beklenen <c>aud</c>'un ortam değişkeni.</summary>
    public const string ResourceVariable = "BIZIGO_MCP_RESOURCE";

    /// <summary>Metadata adresinin ortam değişkeni (isteğe bağlı).</summary>
    public const string MetadataVariable = "BIZIGO_MCP_METADATA";

    public static McpStdioIdentitySettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable(TokenVariable),
        Environment.GetEnvironmentVariable(AuthorityVariable),
        Environment.GetEnvironmentVariable(ResourceVariable),
        Environment.GetEnvironmentVariable(MetadataVariable));

    /// <summary>Ortamda belirteç var mı — kimlik yolunun açılıp açılmayacağı.</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>
    /// Belirteç <b>var</b> ama yanındaki zorunlu ayarlar eksik mi — varsa
    /// adlarıyla söyleyen mesaj.
    ///
    /// <para>
    /// Belirteç <b>yoksa</b> hiçbir şey eksik değil: kimlik yolu bilinçli olarak
    /// kapalı ve bu M13'ün üçüncü şartı — sessiz bir varsayılana düşülmüyor,
    /// bugünkü davranış aynen kalıyor.
    /// </para>
    ///
    /// <para>
    /// Belirteç varken issuer ya da kitle eksikse süreç <b>başlamıyor</b>. İkisi
    /// için de varsayılan uydurmak, belirteci <i>doğrulamadan</i> ya da yanlış
    /// bir IdP'ye güvenerek kabul etmeye giden yol olurdu — <c>Auth:Audience</c>
    /// için API tarafında aynı gerekçeyle varsayılan kaldırılmıştı.
    /// </para>
    /// </summary>
    public string? MissingSetting()
    {
        if (!HasToken)
        {
            return null;
        }

        var missing = new List<string>(2);

        if (string.IsNullOrWhiteSpace(Authority))
        {
            missing.Add(AuthorityVariable);
        }

        if (string.IsNullOrWhiteSpace(Resource))
        {
            missing.Add(ResourceVariable);
        }

        if (missing.Count == 0)
        {
            return null;
        }

        return $"`{TokenVariable}` verildi ama yanındaki zorunlu ayar eksik: "
            + string.Join(", ", missing.Select(static name => $"`{name}`"))
            + $". Belirteç DOĞRULANMADAN kabul edilmiyor: issuer (`{AuthorityVariable}`) "
            + $"olmadan ağ içinden erişilebilen herhangi bir IdP'ye güvenilirdi, kitle "
            + $"(`{ResourceVariable}`) olmadan API için basılmış bir belirteç bu yüzeyde de "
            + "geçerdi (RFC 8707). Belirteci kaldırmak da geçerli bir seçim — o hâlde yüzey "
            + "kimliksiz kalkıyor ve kimlik isteyen araçlar `unauthenticated` dönüyor.";
    }
}

/// <summary>
/// <b>stdio'nun kimliği: ortamdan gelen bir belirteç, DOĞRULANMIŞ hâliyle</b>
/// (M13).
///
/// <h3>Ortamdan TOKEN, ayardan KAPSAM değil</h3>
///
/// <para>
/// Ayrım bu sınıfın varlık sebebi. Bir <c>BIZIGO_MCP_OWNER_GROUP</c> ortam
/// değişkeni <b>kapsamın kendisini</b> taşırdı ve K17'yi ihlal ederdi: veri
/// kapsamı bir süreç ayarı olurdu. Burada taşınan şey <b>belirteç</b>; kapsam
/// hâlâ claim'lerden ve <c>idp_group_mapping</c> üzerinden çıkıyor, yani REST
/// uçlarının geçtiği <b>aynı</b> kapıdan (<c>AccessScopeResolver</c>).
/// </para>
///
/// <h3>Doğrulama API ile AYNI kurallar</h3>
///
/// <para>
/// <c>AddJwtBearer</c> kullanılamıyor — ASP.NET paylaşılan çatısı
/// <c>Bizigo.Cli</c>'ye inince <c>Program</c> çakışması doğuruyor (ölçüldü,
/// <c>Bizigo.Mcp.csproj</c>). Kullanılan kütüphane <b>aynı</b>
/// (<c>Microsoft.IdentityModel</c> 8.19.2, API'nin çözdüğü sürüm) ve
/// parametreler <c>AuthenticationSetup.ConfigureBearer</c>'ın beşiyle birebir:
/// issuer, kitle, ömür, imza anahtarı, ve claim adları
/// <see cref="BizigoClaims"/>'ten — ikinci bir claim sözleşmesi yazılmadı (§9).
/// </para>
///
/// <h3>SÜRE SONU — yazılı bir sınır, yenileme YOK</h3>
///
/// <para>
/// Belirtecin <c>exp</c>'si gerçek bir sınır ve bu yüzey onu <b>aşmıyor</b>:
/// <see cref="Principal"/> süresi dolduğunda <see langword="null"/> dönüyor ve
/// <see cref="Reason"/> sebebi söylüyor. Yani uzun koşan bir stdio süreci
/// belirteci dolduğunda <b>çalışmayı bırakıyor</b> — ve bıraktığını
/// <b>söylüyor</b>.
/// </para>
///
/// <para>
/// <b>Yenileme bilerek yazılmadı.</b> Yenileme bir <c>refresh_token</c> ve bir
/// istemci sırrı ister; ikisini bir masaüstü istemcisinin <c>env</c> bloğuna
/// koymak, uzun ömürlü bir kimlik bilgisini süreç ortamına yazmak olurdu. Sınır
/// yazılı olduğu sürece operatörün yapacağı şey belli; sessiz bir <i>"bir süre
/// sonra çalışmayı bırakıyor"</i> hâli olsaydı belli olmazdı.
/// </para>
/// </summary>
public sealed class McpStdioIdentity : IMcpIdentityRefusal
{
    private readonly ClaimsPrincipal principal;
    private readonly DateTimeOffset expiresAt;
    private readonly TimeProvider time;

    private McpStdioIdentity(ClaimsPrincipal principal, DateTimeOffset expiresAt, TimeProvider time)
    {
        this.principal = principal;
        this.expiresAt = expiresAt;
        this.time = time;
    }

    /// <summary>Belirtecin süresinin dolduğu an — teşhis ve kalkış satırı için.</summary>
    public DateTimeOffset ExpiresAt => expiresAt;

    /// <summary>Süresi doldu mu.</summary>
    public bool IsExpired => IsExpiredAt(expiresAt, time.GetUtcNow());

    /// <summary>
    /// Süre sonu kararının <b>saf</b> hâli.
    ///
    /// <para>
    /// Ayrı ve <c>static</c>, çünkü test edilebilmesi gerekiyor ve bu tipin
    /// yapıcısı <c>private</c>: bir <see cref="McpStdioIdentity"/> yalnızca
    /// <see cref="ValidateAsync"/>'ten çıkıyor, yani doğrulanmamış bir
    /// principal'dan kimlik kurulamıyor. <c>InternalsVisibleTo</c> ile açmak
    /// <b>elendi</b>: bu projede o satır ASP.NET'in `Program` çakışmasını
    /// doğuruyor (ölçüldü, M12).
    /// </para>
    ///
    /// <para>
    /// Sınır <c>&gt;=</c>: <c>exp</c> anında belirteç <b>artık geçerli değil</b>.
    /// Yarı-açık aralık, `AlertSuppression.IsOpen`'ın aynı kuralı.
    /// </para>
    /// </summary>
    public static bool IsExpiredAt(DateTimeOffset expiresAt, DateTimeOffset now) => now >= expiresAt;

    /// <summary>
    /// Süre sonu <b>cümlesi</b> — ya da süre dolmadıysa <see langword="null"/>.
    ///
    /// <para>
    /// Saf olması bu turun dördüncü şartının bekçisini mümkün kılan şey:
    /// <i>"süresi dolan belirtecin hatası, kimliğin YOKLUĞUNDAN ayırt
    /// edilebiliyor mu"</i>. Cümle <c>McpCallerScope.NoIdentityMessage</c>'tan
    /// FARKLI olmalı ve bu ölçülüyor.
    /// </para>
    /// </summary>
    public static string? ExpiryReason(DateTimeOffset expiresAt, DateTimeOffset now) =>
        IsExpiredAt(expiresAt, now)
            ? $"`{McpStdioIdentitySettings.TokenVariable}` belirtecinin SÜRESİ DOLDU "
                + $"({expiresAt.ToString("u", CultureInfo.InvariantCulture)}). Bu yüzey belirteci "
                + "YENİLEMİYOR (M13'ün yazılı sınırı): süreci yeni bir belirteçle yeniden "
                + "başlatın. Kimliğin hiç verilmediği hâlden AYRI bir cümle, çünkü yapılacak "
                + "iş ayrı."
            : null;

    /// <summary>
    /// Çağrı başına kimlik — süresi dolduysa <see langword="null"/>.
    ///
    /// <para>
    /// Sabit bir kimlik döndürmek <c>exp</c>'yi <b>aşmak</b> olurdu: stdio
    /// kimliği hiç sona ermezdi ve süresi geçmiş bir belirteçle veri okunurdu.
    /// </para>
    /// </summary>
    public ClaimsPrincipal? Principal() => IsExpired ? null : principal;

    /// <inheritdoc/>
    /// <remarks>
    /// Kimlik geçerliyken <see langword="null"/>: <c>McpCallerScope</c> yalnızca
    /// kimlik ÜRETİLEMEDİĞİNDE buraya bakıyor.
    /// </remarks>
    public string? Reason => ExpiryReason(expiresAt, time.GetUtcNow());

    /// <summary>
    /// Doğrulama parametreleri — <b>API'nin <c>ConfigureBearer</c>'ıyla birebir.</b>
    ///
    /// <para>
    /// Ayrı ve <c>static</c> olması bir bekçi içindir: beşi de <b>ölçülebilir</b>
    /// olmalı. <c>ValidateAudience</c>'ın <see langword="true"/> olduğu ve
    /// beklenen kitlenin MCP'nin <b>kendi</b> kaynağı olduğu (API'nin kitlesi
    /// değil — M09, RFC 8707) canlı bir Keycloak olmadan ancak buradan
    /// sınanabiliyor.
    /// </para>
    ///
    /// <para>
    /// Claim adları <see cref="BizigoClaims"/>'ten: ikinci bir claim sözleşmesi
    /// yazmak, aynı kişinin REST'ten ve stdio'dan farklı kapsam görmesi demek
    /// olurdu (§9).
    /// </para>
    /// </summary>
    public static TokenValidationParameters ValidationParameters(
        McpStdioIdentitySettings settings,
        IEnumerable<SecurityKey>? signingKeys)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Authority?.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = settings.Resource,
            ValidateLifetime = true,

            // AÇIK otuz saniye — devralınan varsayılan DEĞİL, ve sıfır da değil.
            //
            // Ölçüldü (canlı Keycloak, `Suresi_dolan_belirtec_ayri_sebep_donduruyor`):
            // `ClockSkew` ayarlanmadığında varsayılanı **beş dakika** ve belirteç
            // `exp`'ten beş dakika sonra bile kabul ediliyordu. Kusur toleransın
            // büyüklüğü değil, **kimsenin seçmemiş olması**: `ValidateLifetime = true`
            // yazmak süre sonunun görüldüğünü göstermiyor.
            //
            // İlk karar sıfırdı ve **geri alındı**: `ClockSkew` tek bir düğme, `exp`'i
            // sıktığı gibi `nbf`'i de sıkıyor. Sıfır tolerans, saati bu makineden ileri
            // olan bir IdP'nin bastığı belirteci *"henüz geçerli değil"* diye
            // reddettirir — ve bu yüzeyde **yenileme yok** (M13), yani operatör süreci
            // yeniden başlatır ve **aynı hatayı** alır. `exp` tarafında kurtarma yolu
            // var (yeni belirteç), `nbf` tarafında yok. Risk stdio'da en yüksek:
            // doğrulayan taraf operatörün masaüstü ve masaüstü saatleri sunuculardan
            // daha çok kayıyor. Keycloak'ın `nbf` basıp basmadığı ölçülmedi; karar
            // buna dayanmıyor, çünkü belgelenen geçiş hedefi Entra ID **basıyor**.
            //
            // Otuz saniyenin bedeli yazılı: belgede duran *"süresi doldu → araçlar
            // `unauthenticated` döner"* cümlesi otuz saniye boyunca yanlış. Beş dakika
            // yerine otuz saniye olmasının sebebi süre değil, sınırın **seçilmiş**
            // olması. HTTP yüzeyi de otuz saniyede (`AuthenticationSetup`), ama oraya
            // kopyalanarak değil kendi ölçümünden varıldı ve gerekçesi ayrı.
            ClockSkew = TimeSpan.FromSeconds(30),

            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            RoleClaimType = BizigoClaims.Roles,
            NameClaimType = BizigoClaims.PreferredUsername,
        };
    }

    /// <summary>
    /// Belirteci doğrular. Başarısızlık bir <b>mesaj</b> olarak dönüyor, istisna
    /// olarak değil: bu bir operatör hatası ve yığın izi basmak yanlış yere
    /// bakmaya yol açar.
    /// </summary>
    /// <returns>
    /// Doğrulanmış kimlik, ya da başarısızlık sebebi. Ayarlarda belirteç yoksa
    /// <b>ikisi de <see langword="null"/></b> — kimlik yolu kapalı ve bu bir
    /// hata değil.
    /// </returns>
    public static async Task<(McpStdioIdentity? Identity, string? Failure)> ValidateAsync(
        McpStdioIdentitySettings settings,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.HasToken)
        {
            return (null, null);
        }

        if (settings.MissingSetting() is { } missing)
        {
            return (null, missing);
        }

        var authority = settings.Authority!.TrimEnd('/');

        var metadata = string.IsNullOrWhiteSpace(settings.MetadataAddress)
            ? $"{authority}/.well-known/openid-configuration"
            : settings.MetadataAddress;

        OpenIdConnectConfiguration configuration;

        try
        {
            // `RequireHttps = false`: yerel Keycloak `http://localhost:8180`
            // üzerinde ve API tarafı da aynı ayarı taşıyor
            // (`AuthOptions.RequireHttpsMetadata`). Buraya `true` yazmak
            // geliştirme kurulumunu kimlik olmadan çalışmaya mahkûm ederdi;
            // üretimde adresin kendisi https olur.
            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadata,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = false });

            configuration = await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // MESAJ KIRPILIYOR, ve bu bir ölçümden doğdu: `error.Message`
            // `ConfigurationManager`'da BÜTÜN istisna zincirini ve yığın izini
            // taşıyor (ölçüldü — operatöre ~30 satır çıkıyordu). Bir kalkış
            // reddinin tek işi ne yapılacağını söylemek; okunmayan bir mesaj
            // okunmayan bir kırmızıdır.
            //
            // En İÇTEKİ sebep alınıyor: dış katmanlar "yapılandırma alınamadı"
            // diye tekrar ediyor, asıl bilgi (DNS, bağlantı reddi, 404) içte.
            var innermost = error;

            while (innermost.InnerException is { } inner)
            {
                innermost = inner;
            }

            var reason = innermost.Message.Split('\n')[0].Trim();

            return (null, $"Kimlik sağlayıcısının keşif belgesi okunamadı (`{metadata}`): "
                + $"{reason} — belirteç DOĞRULANMADAN kabul edilmiyor, yüzey başlamıyor. "
                + $"`{McpStdioIdentitySettings.AuthorityVariable}` doğru mu ve bu süreçten "
                + "erişilebiliyor mu?");
        }

        var handler = new JsonWebTokenHandler();

        // PARAMETRELER `AuthenticationSetup.ConfigureBearer` İLE BİREBİR.
        // Beşinin biri gevşetilirse iki yüzey aynı belirteç hakkında farklı
        // karar verir; claim adları da oradan (`BizigoClaims`), ikinci bir
        // sözleşme yazılmadı.
        var result = await handler
            .ValidateTokenAsync(settings.Token, ValidationParameters(settings, configuration.SigningKeys))
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            // KİTLE REDDİ AYRI CÜMLE. M09'un kararı bu: API için basılmış bir
            // belirteç bu yüzeyde de reddedilmeli (RFC 8707), ve reddin sebebi
            // "imza bozuk" ile karışırsa operatör yanlış yerde arar.
            var reason = result.Exception is SecurityTokenInvalidAudienceException
                ? $"belirtecin kitlesi (`aud`) `{settings.Resource}` değil. MCP yüzeyinin kendi "
                    + "kaynak kimliği var (M09): API için basılmış bir belirteç burada GEÇMEZ."
                : result.Exception?.Message ?? "bilinmeyen sebep";

            return (null, $"`{McpStdioIdentitySettings.TokenVariable}` doğrulanamadı: {reason}");
        }

        var time = timeProvider ?? TimeProvider.System;
        var identity = new ClaimsPrincipal(result.ClaimsIdentity);

        // `exp` yoksa belirteç sonsuz sayılmıyor: doğrulama `ValidateLifetime`
        // ile geçmiş olsa bile burada bir sınır GEREKİYOR, yoksa `Principal()`
        // hiç sona ermez. Sınır yoksa reddediliyor — yazılı bir sınırın
        // olmaması, sınırsız bir kimlik demek.
        if (result.SecurityToken?.ValidTo is not { } validTo || validTo == default)
        {
            return (null, $"`{McpStdioIdentitySettings.TokenVariable}` bir süre sonu (`exp`) "
                + "taşımıyor. Sınırsız bir stdio kimliği kabul edilmiyor: bu yüzey belirteci "
                + "yenilemediği için `exp` tek sonlanma noktası.");
        }

        return (new McpStdioIdentity(identity, new DateTimeOffset(validTo, TimeSpan.Zero), time), null);
    }
}

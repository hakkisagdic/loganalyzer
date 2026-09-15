using System.Security.Claims;
using System.Text;
using Bizigo.Api;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>HTTP tarafında belirtecin süresi dolduğunda ne oluyor</b> — M08'in son
/// kabul kriteri (M19).
///
/// <h3>Kriter neden açık kalmıştı</h3>
///
/// <para>
/// M08 <i>"token'ın süresi dolduğunda davranış yazılı ve sınanmış"</i> diyor.
/// M13 bunu <b>stdio</b> için kapattı (<c>McpStdioIdentity.ExpiryReason</c>,
/// <c>IsExpiredAt</c> — saf fonksiyonlar, ayrı cümle). HTTP tarafında ise
/// davranış <c>AddJwtBearer</c>'ın varsayılanı ne yapıyorsa oydu ve
/// <b>ne yaptığı yazılı değildi</b>: <c>ClaimMappingTests</c> yalnızca
/// <c>ValidateLifetime</c>'ın açık olduğunu çiviliyordu — yani
/// <i>"süre kontrol ediliyor"</i>, ama <i>"hangi toleransla"</i> değil.
/// </para>
///
/// <h3>Tolerans AÇIKÇA SEÇİLDİ: 30 saniye</h3>
///
/// <para>
/// Bu bekçi ilk yazıldığında ölçtüğü şey şuydu: <c>ClockSkew</c> ürün
/// tarafından <b>hiç ayarlanmıyor</b>, yani kütüphanenin varsayılanı olan
/// <b>300 saniye</b> geçerliydi ve <c>ValidateLifetime = true</c> yazmak bunu
/// görmüyordu. <b>Kusur sayının büyüklüğü değil, kimsenin seçmemiş olmasıydı</b>
/// — ve aynı boşluk stdio'da da vardı (koordinatör canlı Keycloak'la ölçtü:
/// <c>exp + 1 s</c>'de belirteç hâlâ kabul ediliyordu).
/// </para>
///
/// <para>
/// <b>ÖLÇÜLEN gerekçe:</b> realm'in <c>accessTokenLifespan</c> değeri
/// <b>900 s</b> (<c>deploy/keycloak/realm-bizigo.json</c>). Yani beş dakikalık
/// tolerans belirtecin ömrünün <b>üçte biri</b> — etkin ömrü %33 uzatıyor. Bu
/// ürün müşterinin log'unu okuyor; kimsenin seçmediği beş dakikalık bir
/// gecikme, güvenlik penceresi olarak fazla.
/// </para>
///
/// <para>
/// <b>Neden sıfır değil — ve burada stdio'dan AYRILIYOR.</b> Kaymayı kim
/// düzeltiyor sorusunun cevabı topolojiden geliyor: doğrulama <c>exp</c>'i
/// (Keycloak yazıyor) <b>API'nin</b> saatiyle karşılaştırıyor, yani ilgili çift
/// Keycloak↔API — <b>istemcinin saati bu hesaba hiç girmiyor</b>, o yalnızca
/// belirteci taşıyor. Sıfır tolerans <c>nbf</c>'i de sıkıyor ve tehlikeli yön
/// orası: saati API'den ileri olan bir Keycloak'ın bastığı belirteç
/// <i>"henüz geçerli değil"</i> diye reddedilir ve <b>yenileme bunu çözmez</b> —
/// yeni belirteç aynı sorunu taşır. <c>exp</c> tarafında yenileme bir kurtarma
/// yolu (<c>ui/src/lib/auth/oidc.ts</c> — <c>refresh()</c>), <c>nbf</c>
/// tarafında yok.
/// </para>
///
/// <h3>stdio ile HTTP aynı kriteri FARKLI karşılıyor</h3>
///
/// <list type="table">
/// <listheader><term>Eksen</term><description>stdio ↔ HTTP</description></listheader>
/// <item>
/// <term>Yenileme</term>
/// <description>
/// <b>stdio: yok.</b> Belirteç süreç ortamından (<c>BIZIGO_MCP_TOKEN</c>) geliyor
/// ve süreç ömrü boyunca sabit; yenilemek bir <c>refresh_token</c> ile istemci
/// sırrını <c>env</c> bloğuna koymak olurdu. Süre dolunca çözüm <b>süreci yeni
/// belirteçle yeniden başlatmak</b>.
/// <b>HTTP: var.</b> İstemci her istekte belirteç taşıyor, yenisini kendi
/// getiriyor; sunucu tarafında yapılacak bir şey yok.
/// </description>
/// </item>
/// <item>
/// <term>Tolerans</term>
/// <description>
/// <b>stdio: SIFIR</b> (<c>ClockSkew = TimeSpan.Zero</c>, M19'da koordinatör
/// yazdı) — <c>IsExpiredAt</c> yarı-açık aralık, <c>exp</c> anında belirteç
/// artık geçerli değil. <b>HTTP: 30 s.</b> Fark artık <b>bilinçli</b> ve iki
/// ayrı gerekçesi var: stdio'da yenileme yok, yani lütuf penceresi belgedeki
/// <i>"süresi doldu → `unauthenticated`"</i> cümlesini yanlış yapardı; HTTP'de
/// ise <c>nbf</c> riski toleransı sıfırlamayı pahalı kılıyor.
/// </description>
/// </item>
/// <item>
/// <term>Ayırt edilebilirlik</term>
/// <description>
/// <b>İkisi de ayırt ediyor, farklı kanaldan.</b> stdio'da kod aynı
/// (<c>unauthenticated</c>) ama <b>cümle</b> farklı (<c>IMcpIdentityRefusal</c>).
/// HTTP'de durum aynı (401) ama <c>WWW-Authenticate</c> başlığı
/// <c>error_description</c> taşıyor — <c>IncludeErrorDetails</c> açık olduğu
/// için.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// <b>Bu tablonun yazılı olmasının sebebi:</b> stdio'nun sınırını
/// (<i>"yenileme yok, süreci yeniden başlat"</i>) HTTP'ye taşımak kolay bir
/// hata, ve tersi de — HTTP'nin beş dakikalık toleransını stdio'da varsaymak.
/// İkisi aynı kriteri karşılıyor, aynı mekanizmayla değil.
/// </para>
/// </summary>
public sealed class HttpTokenExpiryTests
{
    private const string Authority = "http://localhost:8180/realms/bizigo";
    private const string Audience = "bizigo-api";

    // 32 bayt: HMAC-SHA256'nın anahtar boyu alt sınırı. Test belirteci imzalamak
    // için — üretimde imza Keycloak'ın asimetrik anahtarıyla ve doğrulaması
    // `ValidateIssuerSigningKey` ile yapılıyor.
    private static readonly SymmetricSecurityKey TestKey =
        new(Encoding.UTF8.GetBytes("m19-http-sure-sonu-olcumu-icin-32b"));

    /// <summary>
    /// <b>Etkin tolerans 300 saniye — ve bu bir ÇİVİ, öneri değil.</b>
    ///
    /// <para>
    /// Değeri ürün ayarlamıyor; kütüphanenin varsayılanı geliyor. Test bunu
    /// <b>üretim DI grafiğinden</b> okuyor, sabitten değil: bir gün
    /// <c>AuthenticationSetup</c> içinde ayarlanırsa bu test o günün değerini
    /// gösterir ve kırmızı yanar — yani değişiklik <b>bilinçli</b> olmak zorunda.
    /// </para>
    /// </summary>
    [Fact]
    public void Etkin_saat_kaymasi_toleransi_otuz_saniye()
    {
        var parameters = EffectiveParameters();

        Assert.True(
            parameters.ValidateLifetime,
            "Süre doğrulaması kapalıysa süresi dolmuş her belirteç kabul edilir.");

        // Değer İSMİYLE tutuluyor: kütüphane sürümü varsayılanı değiştirse bile
        // bu yüzeyin davranışı değişmemeli. Varsayılana güvenmek, M19'un
        // düzelttiği hatanın kendisiydi.
        Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);

        // Özel bir `LifetimeValidator` YOK: yani süre kararını kütüphane veriyor
        // ve yukarıdaki tolerans gerçekten geçerli. Bir validator konsaydı
        // `ClockSkew` sessizce anlamsızlaşırdı — çivi değeri ölçmeye devam eder,
        // davranışı ölçmezdi.
        Assert.Null(parameters.LifetimeValidator);
    }

    /// <summary>
    /// <b>Süresi YENİ dolmuş belirteç KABUL EDİLİYOR</b> — beş dakikalık
    /// toleransın gerçek hâli.
    ///
    /// <para>
    /// Bu testin adı bir arızayı değil <b>ölçülen davranışı</b> anlatıyor. Kabul
    /// ediliyor olması yukarıdaki toleransın yapılandırma değil <b>davranış</b>
    /// olduğunun kanıtı: bayrağı çivilemek <i>"tolerans uygulanıyor"</i>
    /// demiyordu, bu test diyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Suresi_yeni_dolmus_belirtec_tolerans_icinde_kabul_ediliyor()
    {
        var sonuc = await DogrulaAsync(expiresIn: TimeSpan.FromSeconds(-10));

        Assert.True(
            sonuc.IsValid,
            "Süresi 10 saniye önce dolmuş belirteç reddedildi. Tolerans 30 s "
            + "olduğu için KABUL bekleniyordu — tolerans değiştiyse "
            + $"`{nameof(Etkin_saat_kaymasi_toleransi_otuz_saniye)}` de yanmalı.");
    }

    /// <summary>
    /// <b>Tolerans dışında REDDEDİLİYOR, ve sebebi ayırt edilebilir.</b>
    ///
    /// <para>
    /// İki yön birlikte ölçülüyor. Yalnızca <i>"reddediliyor"</i> ölçmek, her
    /// belirteci reddeden bir yapılandırmayı da geçirirdi — yukarıdaki kabul
    /// testi onun karşı-kanıtı. Ve istisna <b>tipinin</b> ölçülmesi süre sonunu
    /// imza hatasından ayırıyor: istemcinin <i>"yenile ve tekrar dene"</i>
    /// diyebilmesi buna bağlı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Tolerans_disinda_dolmus_belirtec_reddediliyor()
    {
        var sonuc = await DogrulaAsync(expiresIn: TimeSpan.FromMinutes(-1));

        Assert.False(
            sonuc.IsValid,
            "Süresi 1 dakika önce dolmuş belirteç kabul edildi. Tolerans 30 s "
            + "olduğu için RED bekleniyordu; eskiden (ayarsız, 300 s) bu belirteç "
            + "KABUL EDİLİYORDU — M19'un düzelttiği davranış tam olarak bu.");

        Assert.IsType<SecurityTokenExpiredException>(sonuc.Exception);
    }

    /// <summary>
    /// <b>Süre sonu istemciye AYIRT EDİLEBİLİR geliyor</b> — M13'ün stdio'daki
    /// "ayrı cümle" kararının HTTP karşılığı.
    ///
    /// <para>
    /// <c>IncludeErrorDetails</c> kapalı olsaydı istemci düz bir 401 görürdü ve
    /// <i>"belirtecim yok"</i> ile <i>"belirtecimin süresi doldu"</i> aynı
    /// görünürdü — ilkinde giriş yapılır, ikincisinde yenileme denenir. İki
    /// farklı iş.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Bu testin göremediği:</b> başlığın gerçekten yazıldığını ölçmüyor,
    /// yalnızca yazılmasını sağlayan bayrağı ölçüyor. Uçtan uca hâli bir HTTP
    /// koşumu ister (<c>ScopeNegativeTests</c>'in yaşadığı yer) ve o paket
    /// container'a bağlı.
    /// </para>
    /// </summary>
    [Fact]
    public void Sure_sonu_sebebi_istemciye_gonderiliyor()
    {
        Assert.True(
            Options().IncludeErrorDetails,
            "Kapalıyken 401'in sebebi istemciye ulaşmıyor: süre sonu ile kimlik "
            + "yokluğu ayırt edilemez ve istemci yenileme yerine giriş dener.");
    }

    /// <summary>
    /// Üretim yapılandırmasından <c>JwtBearerOptions</c>.
    /// </summary>
    private static JwtBearerOptions Options()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = "true",
                ["Auth:Authority"] = Authority,
                ["Auth:Audience"] = Audience,
                ["Auth:McpResource"] = "bizigo-mcp",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBizigoAuthentication(configuration);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static TokenValidationParameters EffectiveParameters() =>
        Options().TokenValidationParameters;

    /// <summary>
    /// Üretimin doğrulama parametreleriyle, yalnızca <b>anahtar malzemesi</b>
    /// değiştirilmiş hâlde bir belirteci doğrular.
    /// </summary>
    /// <remarks>
    /// İmza anahtarını değiştirmek zorunlu (Keycloak'ın özel anahtarı burada
    /// yok), ama <c>ClockSkew</c> ve <c>ValidateLifetime</c> <b>üretimden</b>
    /// geliyor — ölçülen şey bu testin kurduğu bir dünya değil, ürünün kendi
    /// süre kararı.
    ///
    /// <para>
    /// ⚠️ <b>Bu cümle bir kez ÖLÇÜLMEDEN yazılmıştı.</b> §6 ölçümü testi
    /// üretimden kopardı (elle kurulmuş parametre nesnesi) ve <b>hiçbir şey
    /// yanmadı</b>: kütüphanenin varsayılan toleransı da 300 s olduğu için kopuk
    /// test aynı sonucu veriyordu. Yani <i>"üretimden geliyor"</i> iddiası
    /// doğruydu ama <b>ölçülmüyordu</b>. Şimdi metodun içindeki parmak izi
    /// iddiaları onu ölçüyor.
    /// </para>
    /// </remarks>
    private static async Task<TokenValidationResult> DogrulaAsync(TimeSpan expiresIn)
    {
        var parameters = EffectiveParameters().Clone();

        // ⚠️ Bu üç satır §6 ölçümünün BULDUĞU bir boşluğu kapatıyor.
        //
        // Ölçüm şunu denedi: `EffectiveParameters()` yerine elle kurulmuş bir
        // `TokenValidationParameters` koy. Testler YİNE GEÇTİ — çünkü
        // kütüphanenin varsayılan toleransı da 300 s, yani üretimden kopmuş bir
        // test aynı sonucu üretiyordu. Yani aşağıdaki davranış ölçümleri
        // "ürünün toleransı" değil "bir toleransın" ölçümü olabilirdi.
        //
        // Parmak izi: `RoleClaimType` ürüne özgü (F1 §10.1.1 claim sözleşmesi)
        // ve elle kurulmuş bir parametre nesnesinde YOK. Kopma artık burada
        // yanıyor.
        Assert.Equal(BizigoClaims.Roles, parameters.RoleClaimType);
        Assert.Equal(Authority, parameters.ValidIssuer);
        // Değer İSMİYLE tutuluyor: kütüphane sürümü varsayılanı değiştirse bile
        // bu yüzeyin davranışı değişmemeli. Varsayılana güvenmek, M19'un
        // düzelttiği hatanın kendisiydi.
        Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);

        parameters.IssuerSigningKey = TestKey;
        parameters.ValidateIssuerSigningKey = true;

        var handler = new JsonWebTokenHandler();
        var now = DateTime.UtcNow;

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = Audience,
            // `notBefore`'u geriye çekiyoruz: `exp` geçmişte olan bir belirteçte
            // `nbf` varsayılanı "şimdi" olurdu ve red sebebi süre sonu değil
            // "henüz geçerli değil" olurdu — yani ölçüm başka bir şeyi ölçerdi.
            NotBefore = now.Add(expiresIn).AddMinutes(-10),
            IssuedAt = now.Add(expiresIn).AddMinutes(-10),
            Expires = now.Add(expiresIn),
            Subject = new ClaimsIdentity([new Claim("sub", "m19")]),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256),
        });

        return await handler.ValidateTokenAsync(token, parameters);
    }
}

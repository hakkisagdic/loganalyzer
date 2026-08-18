using Bizigo.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>
/// Claim sözleşmesinin <b>fiilen devrede</b> olduğu.
///
/// <para>
/// <c>RoleClaimType</c> ve <c>NameClaimType</c> ayarları tek başına yetmiyor:
/// <c>MapInboundClaims</c> varsayılan <c>true</c> iken işleyici gelen claim'leri
/// Microsoft'un uzun URI şemasına çeviriyor ve o ayarlar hiçbir şeye denk
/// gelmiyor. Ölçülen hâli: collector'ın <c>roles: ["ingest"]</c> taşıyan
/// token'ıyla <c>/v1/logs</c> <b>403</b> dönüyordu ve <c>/auth/me</c>
/// <c>roles: []</c> gösteriyordu — yani kod yorumu doğru şeyi anlatıyor ama
/// anahtar çevrilmemiş durumdaydı.
/// </para>
///
/// <para>
/// <b>K31 sonrası:</b> OIDC işleyicisi API'den kaldırıldı, dolayısıyla "iki
/// işleyici ayrışmasın" iddiası da kalktı — ayrışabilecek ikinci işleyici yok.
/// Yerine daha güçlü bir iddia geldi: API'de <b>hiçbir</b> tarayıcı akışı
/// işleyicisi kayıtlı olmamalı. JWT tarafındaki iddialar aynen duruyor.
/// </para>
/// </summary>
public sealed class ClaimMappingTests
{
    private static ServiceProvider Build(bool authEnabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = authEnabled ? "true" : "false",
                ["Auth:Authority"] = "http://localhost:8180/realms/bizigo",
                ["Auth:Audience"] = "bizigo-api",
                ["Auth:RequireHttpsMetadata"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBizigoAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Jwt_isleyicisi_claim_adlarini_ceviremiyor()
    {
        using var provider = Build();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.False(
            options.MapInboundClaims,
            "MapInboundClaims açıkken `roles` ve `sub` uzun URI'lere çevriliyor; "
            + "RoleClaimType/NameClaimType hiçbir şeye denk gelmiyor.");

        Assert.Equal("roles", options.TokenValidationParameters.RoleClaimType);
        Assert.Equal("preferred_username", options.TokenValidationParameters.NameClaimType);
    }

    /// <summary>
    /// Issuer doğrulaması <b>açık</b> kalmalı. Kapatmak, collector'ın issuer
    /// uyuşmazlığında aldığı 401'i "çözmenin" en kolay ve en yanlış yolu olurdu:
    /// hata kaybolur, başka bir realm'in imzaladığı token kabul edilir hâle
    /// gelirdi.
    /// </summary>
    [Fact]
    public void Issuer_ve_audience_dogrulamasi_acik()
    {
        using var provider = Build();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
    }

    /// <summary>
    /// K31'in bekçisi: API'de tarayıcı oturumu diye bir şey yok.
    ///
    /// <para>
    /// Bu test, "cookie işleyicisini geri koyayım, yerelde işime yarıyor"
    /// hamlesini kırmızıya çevirir. Cookie ya da OIDC şeması kayıtlıysa API
    /// ikinci bir kimlik yolu taşıyor demektir ve o yolun claim sözleşmesi
    /// JWT'ninkinden ayrışabilir — F1'de ölçülen tam da bu risk.
    /// </para>
    ///
    /// <para>
    /// Kimlik <b>kapalıyken</b> de sınanıyor: eski kod o dalda bir cookie
    /// işleyicisi kuruyordu, yani kural yalnızca üretim yapılandırmasında
    /// geçerliydi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Api_tarayici_oturumu_semasi_tasimiyor(bool authEnabled)
    {
        using var provider = Build(authEnabled);
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var registered = (await schemes.GetAllSchemesAsync())
            .Select(s => s.Name)
            .ToArray();

        Assert.DoesNotContain("Cookies", registered);
        Assert.DoesNotContain("OpenIdConnect", registered);

        // Yalnızca beklenen şema kayıtlı olsun — yeni bir tarayıcı akışı
        // eklendiğinde bu satır düşer.
        var expected = authEnabled
            ? JwtBearerDefaults.AuthenticationScheme
            : AnonymousAuthenticationHandler.SchemeName;

        Assert.Equal([expected], registered);
    }
}

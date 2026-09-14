using Bizigo.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M09 — kimliğin bulunması ve bağlanması.</b>
///
/// <para>
/// İki <b>MUST</b> ihlali kapatılıyor: MCP sunucusu kaynak metadata'sını ilan
/// etmiyordu (RFC 9728, istemci Keycloak'ı bulamıyordu) ve kendine ait bir
/// kaynak kimliği yoktu (RFC 8707, API için basılmış bir token
/// <c>/mcp</c>'de de geçerdi).
/// </para>
///
/// <para>
/// <b>Bu paketin asıl işi ikincisi değil üçüncüsü:</b> değişikliğin
/// <c>/mcp</c> <b>dışındaki</b> uçlara dokunmadığını tutmak. Ölçüldü: API'de
/// <b>45</b> <c>RequireAuthorization</c> çağrısı var ve <b>hiçbiri</b> kendi
/// şemasını belirtmiyor — hepsi <c>DefaultChallengeScheme</c>'e bağlı. SDK'nın
/// önerdiği <c>AddMcp(builder, configure)</c> yolu o varsayılanı değiştiriyor,
/// yani 45 ucun tamamının 401 davranışını ve BFF'in oturum akışını (K31)
/// değiştirirdi.
/// </para>
/// </summary>
public sealed class McpIdentityDiscoveryTests
{
    private const string Authority = "http://localhost:8180/realms/bizigo";
    private const string ApiAudience = "bizigo-api";
    private const string McpResource = "http://localhost:5080/mcp";

    private static ServiceProvider Build(
        string? audience = ApiAudience,
        string? mcpResource = McpResource)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Authority"] = Authority,
            ["Auth:Audience"] = audience,
            ["Auth:McpResource"] = mcpResource,
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBizigoAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <b>Patlama yarıçapı kapısı.</b> Varsayılan şemalar JWT Bearer olarak
    /// kalıyor.
    ///
    /// <para>
    /// Bu testin kırmızı yanması *"MCP çalışmıyor"* demek değil, <b>MCP'yi
    /// çalıştırmak için API'nin geri kalanı feda edildi</b> demek. Ayrımı
    /// yazmak gerekiyor çünkü ikisi de aynı dosyaya yapılan bir değişiklikten
    /// doğuyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Varsayilan_semalar_MCP_yuzunden_degismiyor()
    {
        using var provider = Build();
        var options = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, options.DefaultAuthenticateScheme);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, options.DefaultChallengeScheme);
    }

    /// <summary>
    /// MCP kendi <b>adlandırılmış</b> şemasını ve kendi token doğrulayıcısını
    /// alıyor — ikisi de kayıtlı, ikisi de varsayılan değil.
    /// </summary>
    [Fact]
    public async Task MCP_kendi_adlandirilmis_semasini_tasiyor()
    {
        using var provider = Build();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync(BizigoAuthSchemes.Mcp));
        Assert.NotNull(await schemes.GetSchemeAsync(BizigoAuthSchemes.McpBearer));
        Assert.NotNull(await schemes.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme));
    }

    /// <summary>
    /// <b>Kaynak bağlama (RFC 8707).</b> MCP'nin token doğrulayıcısı API'nin
    /// kitlesini değil <b>kendi kaynağını</b> doğruluyor.
    ///
    /// <para>
    /// İkisi eşit olsaydı test yeşil yanardı ama kural çiğnenmiş olurdu, o
    /// yüzden eşit <b>olmadıkları</b> da sınanıyor: asıl güvence bu.
    /// </para>
    /// </summary>
    [Fact]
    public void MCP_kendi_kaynagini_dogruluyor_APInin_kitlesini_degil()
    {
        using var provider = Build();
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var api = monitor.Get(JwtBearerDefaults.AuthenticationScheme);
        var mcp = monitor.Get(BizigoAuthSchemes.McpBearer);

        Assert.Equal(ApiAudience, api.Audience);
        Assert.Equal(McpResource, mcp.Audience);
        Assert.NotEqual(api.Audience, mcp.Audience);

        // Kitleden BAŞKA hiçbir şey ayrışmıyor: issuer ve claim sözleşmesi tek
        // yerden geliyor (`ConfigureBearer`). İki doğrulama yolu ayrışırsa aynı
        // token iki yüzeyde farklı kimlik üretir.
        Assert.Equal(api.Authority, mcp.Authority);
        Assert.Equal(
            api.TokenValidationParameters.RoleClaimType,
            mcp.TokenValidationParameters.RoleClaimType);
        Assert.Equal(
            api.TokenValidationParameters.ValidIssuer,
            mcp.TokenValidationParameters.ValidIssuer);
        Assert.True(mcp.TokenValidationParameters.ValidateAudience);
    }

    /// <summary>
    /// <b>Keşif (RFC 9728).</b> MCP şeması kaynak metadata'sını ilan ediyor:
    /// kaynağın kimliği ve yetkilendirme sunucusunun adresi. Bu belgeyi
    /// sunmak, 401'e <c>WWW-Authenticate: Bearer resource_metadata="…"</c>
    /// koymanın ön şartı — istemci Keycloak'ı bu zincirle buluyor.
    /// </summary>
    [Fact]
    public void MCP_kaynak_metadatasini_ilan_ediyor()
    {
        using var provider = Build();
        var options = provider
            .GetRequiredService<IOptionsMonitor<
                ModelContextProtocol.AspNetCore.Authentication.McpAuthenticationOptions>>()
            .Get(BizigoAuthSchemes.Mcp);

        Assert.NotNull(options.ResourceMetadata);
        Assert.Equal(McpResource, options.ResourceMetadata!.Resource);
        Assert.Contains(Authority, options.ResourceMetadata.AuthorizationServers);

        // Doğrulama İLETİLİYOR: token doğrulama mantığı tek yerde kalıyor,
        // MCP işleyicisi yalnızca meydan okumayı ve keşfi üstleniyor.
        Assert.Equal(BizigoAuthSchemes.McpBearer, options.ForwardAuthenticate);
    }

    /// <summary>
    /// <b>Varsayılansız yapılandırma açılışta patlıyor.</b>
    ///
    /// <para>
    /// <c>Audience</c> bir zamanlar <c>"account"</c> varsayılanını taşıyordu —
    /// Keycloak'ın varsayılan kitlesi. Sevk edilen yapılandırma onu eziyordu,
    /// yani varsayılan hiç devreye girmiyordu; ama <c>Auth:Audience</c>
    /// yazılmayan bir dağıtım <b>sessizce</b> o kitleye doğrular ve hata
    /// vermezdi. <c>CLAUDE.md</c> §7'nin sınıfı: hata yok, sayaç yok, belirti
    /// yok.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, McpResource, "Audience")]
    [InlineData("", McpResource, "Audience")]
    [InlineData(ApiAudience, null, "McpResource")]
    [InlineData(ApiAudience, "   ", "McpResource")]
    public void Eksik_kaynak_kimligi_acilista_patliyor(string? audience, string? resource, string beklenen)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(audience, resource));

        Assert.Contains(beklenen, error.Message, StringComparison.Ordinal);
    }
}

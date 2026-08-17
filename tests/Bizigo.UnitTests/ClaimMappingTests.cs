using Bizigo.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
/// İki işleyici de sınanıyor: ayrışırlarsa kullanıcının kapsamı <b>hangi yoldan
/// girdiğine</b> göre değişir — tarayıcıdan gelen ile token'la gelen aynı kişi
/// farklı veri görür.
/// </para>
/// </summary>
public sealed class ClaimMappingTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = "true",
                ["Auth:Authority"] = "http://localhost:8180/realms/bizigo",
                ["Auth:Audience"] = "bizigo-api",
                ["Auth:RequireHttpsMetadata"] = "false",
                ["Auth:ClientId"] = "bizigo-ui",
                ["Auth:ClientSecret"] = "secret",
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

    [Fact]
    public void Oidc_isleyicisi_ayni_sozlesmede()
    {
        using var provider = Build();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.False(options.MapInboundClaims);
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
}

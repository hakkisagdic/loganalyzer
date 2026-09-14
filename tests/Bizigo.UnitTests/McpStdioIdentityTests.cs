using System.Text.Json;
using Bizigo.Cli;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>stdio kimliği: ortamdan TOKEN, ayardan KAPSAM değil</b> (M13).
///
/// <para>
/// Bu turun taşıyıcı ayrımı bir cümlede: bir <c>BIZIGO_MCP_OWNER_GROUP</c>
/// değişkeni <b>kapsamın kendisini</b> taşırdı ve K17'yi ihlal ederdi — veri
/// kapsamı bir süreç ayarı olurdu. <c>BIZIGO_MCP_TOKEN</c> ise <b>kimliği</b>
/// taşıyor; kapsam hâlâ claim'lerden ve <c>idp_group_mapping</c> üzerinden
/// çıkıyor.
/// </para>
///
/// <h3>Bu paketin ölçEMEDİĞİ şey — beyan</h3>
///
/// <para>
/// Gerçek bir belirtecin doğrulanması <b>canlı bir Keycloak</b> istiyor
/// (keşif belgesi + imza anahtarları), dolayısıyla <c>ValidateAsync</c>'in
/// başarılı yolu burada koşmuyor. Bu paket <b>kararları</b> ölçüyor: hangi
/// ayarlar zorunlu, kitle doğrulanıyor mu, süre sonu cümlesi kimliğin
/// yokluğundan ayırt edilebiliyor mu. Davranış tarafı
/// <c>McpStdioIdentityKeycloakTests</c>'te ve §2 gereği bu turda
/// <b>koşturulmadı</b>.
/// </para>
/// </summary>
public sealed class McpStdioIdentityTests
{
    /// <summary>
    /// <b>Değişken adı <c>BIZIGO_MCP_API_TOKEN</c>'dan AYRI.</b>
    ///
    /// <para>
    /// İkisi farklı şeyler ve aynı ada koymak onları bir sanmaktır:
    /// <c>BIZIGO_MCP_API_TOKEN</c> <c>McpKeycloakIdentityTests</c>'in
    /// <b>istemci</b> belirteci — API için basılmış, <c>/mcp</c>'de
    /// <b>reddedilmesi</b> ölçülen şey. <c>BIZIGO_MCP_TOKEN</c> ise
    /// <b>sunucunun okuduğu</b> belirteç.
    /// </para>
    ///
    /// <para>
    /// Adı bir sabitten okumak da bilinçli: iki dizge iki yerde yazılıydı,
    /// biri değiştiğinde diğeri sessizce eski adı okurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Belirtec_degiskeni_test_istemcisinin_degiskeninden_ayri()
    {
        Assert.Equal("BIZIGO_MCP_TOKEN", McpStdioIdentitySettings.TokenVariable);

        Assert.NotEqual("BIZIGO_MCP_API_TOKEN", McpStdioIdentitySettings.TokenVariable);
        Assert.NotEqual("BIZIGO_MCP_ACCESS_TOKEN", McpStdioIdentitySettings.TokenVariable);
    }

    /// <summary>
    /// <b>Belirteç yoksa hiçbir ayar zorunlu değil</b> — M13'ün üçüncü şartı.
    ///
    /// <para>
    /// Kimlik yolu bilinçli olarak kapalı ve bugünkü davranış aynen kalıyor.
    /// Buraya bir ret koymak, kimlik istemeyen bir kurulumu (yalnızca
    /// <c>server.info</c> kullanan bir istemci, ya da simülatör yüzeyi) hiç
    /// açılamaz yapardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Belirtec_yoksa_ayar_zorunlu_degil()
    {
        var settings = new McpStdioIdentitySettings(null, null, null, null);

        Assert.False(settings.HasToken);
        Assert.Null(settings.MissingSetting());
    }

    /// <summary>
    /// <b>Belirteç varsa issuer ve kitle ZORUNLU — ve adlarıyla söyleniyor.</b>
    ///
    /// <para>
    /// İkisi için de varsayılan uydurmak belirteci <i>doğrulamadan</i> kabul
    /// etmeye giden yol: issuer olmadan ağ içinden erişilebilen herhangi bir
    /// IdP'ye güvenilir, kitle olmadan API için basılmış bir belirteç bu yüzeyde
    /// de geçer (RFC 8707). API tarafında <c>Auth:Audience</c>'ın varsayılanı
    /// aynı gerekçeyle kaldırılmıştı.
    /// </para>
    ///
    /// <para>
    /// Ölçüt <i>"reddetti mi"</i> DEĞİL <b>"hangi ayarı söyledi mi"</b>: tek bir
    /// <c>Assert.NotNull</c>, mesajı <c>"hata"</c>ya çevirdiğimizde de yeşil
    /// kalırdı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, "bizigo-mcp", "BIZIGO_MCP_AUTHORITY")]
    [InlineData("http://localhost:8180/realms/bizigo", null, "BIZIGO_MCP_RESOURCE")]
    [InlineData(null, null, "BIZIGO_MCP_AUTHORITY")]
    public void Belirtec_varsa_eksik_ayar_adiyla_reddediliyor(
        string? authority, string? resource, string beklenenAd)
    {
        var reddedildi = new McpStdioIdentitySettings("e30.e30.imza", authority, resource, null)
            .MissingSetting();

        Assert.NotNull(reddedildi);
        Assert.Contains(beklenenAd, reddedildi, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Karşı-kanıt: ikisi de verildiğinde ret YOK.</b>
    ///
    /// <para>
    /// Olmadan yukarıdaki test her zaman reddeden bir uygulamayla da geçerdi —
    /// ve o uygulama kimlik yolunu hiç açılamaz yapardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayarlar_tamsa_ret_yok() =>
        Assert.Null(new McpStdioIdentitySettings(
            "e30.e30.imza", "http://localhost:8180/realms/bizigo", "bizigo-mcp", null).MissingSetting());

    /// <summary>
    /// <b>KİTLE DOĞRULANIYOR, ve beklenen kitle MCP'nin KENDİ kaynağı.</b>
    ///
    /// <para>
    /// M09'un kararı: <c>bizigo-mcp</c> ayrı bir kitle ve API için basılmış bir
    /// belirteç MCP yüzeyinde <b>geçmemeli</b> (RFC 8707). Davranış tarafı canlı
    /// Keycloak istiyor; burada ölçülen şey <b>parametrenin kendisi</b> —
    /// <c>ValidateAudience</c> kapatılırsa ya da beklenen kitle API'nin
    /// kitlesine çevrilirse bu satır kırmızı yanıyor.
    /// </para>
    ///
    /// <para>
    /// Beş parametrenin beşi birden sınanıyor: biri gevşetildiğinde iki yüzey
    /// aynı belirteç hakkında farklı karar verirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Kitle_ve_issuer_dogrulaniyor()
    {
        var parameters = McpStdioIdentity.ValidationParameters(
            new McpStdioIdentitySettings(
                "e30.e30.imza", "http://localhost:8180/realms/bizigo/", "bizigo-mcp", null),
            signingKeys: null);

        Assert.True(parameters.ValidateAudience);
        Assert.Equal("bizigo-mcp", parameters.ValidAudience);

        Assert.True(parameters.ValidateIssuer);

        // Sondaki eğik çizgi kırpılıyor: Keycloak issuer'ı çizgisiz basıyor ve
        // ortam değişkenine çizgiyle yazmak sessizce her belirteci reddettirirdi.
        Assert.Equal("http://localhost:8180/realms/bizigo", parameters.ValidIssuer);

        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);

        // Claim sözleşmesi ÜRÜNÜN sabitlerinden — ikinci bir sözleşme yok (§9).
        Assert.Equal(BizigoClaims.Roles, parameters.RoleClaimType);
        Assert.Equal(BizigoClaims.PreferredUsername, parameters.NameClaimType);
    }

    /// <summary>
    /// <b>SÜRESİ DOLAN belirteç, kimliğin YOKLUĞUNDAN ayırt edilebiliyor.</b>
    ///
    /// <para>
    /// M13'ün dördüncü şartının bekçisi, ve sorunun kendisi koordinatörden:
    /// <i>"ikisi aynı <c>unauthenticated</c>'e düşüyorsa okuyan kişi yanlış
    /// yere bakar"</i>. Kod <b>aynı</b> kalıyor ve kalması gerekiyor — çağrı
    /// gerçekten kimliksiz — ama <b>cümle</b> ayrı, çünkü yapılacak iş ayrı:
    /// birinde yapılandırma, diğerinde yeni bir belirteç.
    /// </para>
    /// </summary>
    [Fact]
    public void Sure_sonu_cumlesi_kimlik_yokluğundan_ayri()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        var reason = McpStdioIdentity.ExpiryReason(now.AddMinutes(-1), now);

        Assert.NotNull(reason);

        // AYRI CÜMLE — aynı olsaydı ayrım ölçülemezdi.
        Assert.NotEqual(McpCallerScope.NoIdentityMessage, reason);

        // Ve sebebi SÖYLÜYOR: süre sonu, ve yenilemenin olmadığı.
        Assert.Contains("SÜRESİ DOLDU", reason, StringComparison.Ordinal);
        Assert.Contains("YENİLEMİYOR", reason, StringComparison.Ordinal);

        // Süre sonu ANI da yazılı: operatör hangi belirtecin dolduğunu görüyor.
        Assert.Contains("2026-09-14 11:59", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Süresi dolmamış belirteç için sebep YOK.</b>
    ///
    /// <para>
    /// Karşı-kanıt: her zaman bir sebep döndüren bir uygulama yukarıdaki testi
    /// geçer ve geçerli bir kimliği <b>hiç</b> çalıştırmazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Suresi_dolmamis_belirtec_icin_sebep_yok()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(McpStdioIdentity.ExpiryReason(now.AddMinutes(5), now));

        // SINIR: `exp` anında belirteç ARTIK GEÇERLİ DEĞİL (yarı-açık aralık,
        // `AlertSuppression.IsOpen`'ın aynı kuralı). İki yön ölçülüyor — tek yön
        // ölçmek "her zaman geçerli" diyen bir uygulamayı geçirirdi.
        Assert.False(McpStdioIdentity.IsExpiredAt(now.AddTicks(1), now));
        Assert.True(McpStdioIdentity.IsExpiredAt(now, now));
    }

    /// <summary>
    /// <b>Sebep ARACA taşınıyor</b> — cümlenin üretilmesi yetmiyor, kapıdan
    /// geçmesi gerekiyor.
    ///
    /// <para>
    /// <see cref="Sure_sonu_cumlesi_kimlik_yokluğundan_ayri"/> saf cümleyi
    /// ölçüyor. Bu test <b>bağı</b> ölçüyor: <c>McpCallerScope</c> kimlik
    /// üretilemediğinde <see cref="IMcpIdentityRefusal"/>'a sorup o cümleyi
    /// kullanıyor mu. İkisi ayrı, çünkü ayrı kaybediliyor — cümle doğru
    /// üretilirken kapının onu yok saydığı bir hâl mümkün, ve o hâlde araç
    /// yine eski cümleyi döndürür.
    /// </para>
    ///
    /// <para>
    /// Kod <b>aynı</b> (<c>unauthenticated</c>) ve öyle kalması ölçülüyor:
    /// istemcinin dallanacağı şey değişmedi, çağrı gerçekten kimliksiz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sebep_araca_tasiniyor()
    {
        const string refusal = "M13-OLCUM: belirtecin süresi doldu";

        await using var services = new ServiceCollection()
            .AddSingleton<IMcpIdentityRefusal>(new StubRefusal(refusal))
            .AddSingleton<IAccessScopeResolver>(new StubResolver())
            .BuildServiceProvider();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            McpBoundaryDeclaration.Declare(DataBoundary.Internal, "M13 bekçisi: birim testi"),
            [typeof(McpStdioIdentityTests).Assembly],
            services);

        // KİMLİKSİZ oturum — stdio'nun belirteci dolduğu andaki hâli.
        await using var session = await McpTestSession.StartAsync(
            options, services, cancellationToken: TestContext.Current.CancellationToken);

        var result = await session.Client.CallToolAsync(
            "test.needs_identity",
            new Dictionary<string, object?>(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError is true);

        var payload = result.StructuredContent!.Value.GetProperty("error");

        Assert.Equal(McpToolError.Unauthenticated, payload.GetProperty("code").GetString());

        // SEBEP GEÇTİ — ve eski cümle DEĞİL.
        Assert.Equal(refusal, payload.GetProperty("message").GetString());
        Assert.NotEqual(McpCallerScope.NoIdentityMessage, payload.GetProperty("message").GetString());
    }

    private sealed class StubRefusal(string reason) : IMcpIdentityRefusal
    {
        public string? Reason => reason;
    }

    /// <summary>
    /// Çözücü kaydı ŞART: kimlik isteyen bir araç varsa
    /// <c>BizigoMcpServer.Apply</c> onu kurulumda arıyor. Bu sahte hiç
    /// çağrılmıyor — çağrı kimliksiz reddedildiği için oraya ulaşmıyor.
    /// </summary>
    private sealed class StubResolver : IAccessScopeResolver
    {
        public AccessScope Resolve(System.Security.Claims.ClaimsPrincipal? principal) =>
            AccessScope.Denied;
    }

    /// <summary>
    /// Kimlik isteyen bir araç — bu bekçinin öznesi.
    ///
    /// <para>
    /// Ürün araçlarından birini kullanmak, testi onların bağımlılıklarına
    /// bağlardı; ölçülen şey kimlik kapısı, aracın işi değil.
    /// </para>
    /// </summary>
    private sealed class IdentityDemandingTool : BizigoMcpTool
    {
        public override string ToolName => "test.needs_identity";

        public override McpSurface Surface => McpSurface.Product;

        public override string ToolTitle => "Kimlik isteyen";

        public override string ToolDescription => "M13 kimlik kapısının öznesi.";

        public override JsonElement InputSchema { get; } = McpSchema.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        public override JsonElement OutputSchema { get; } = McpSchema.Parse(
            """
            {
              "type": "object",
              "properties": { "ok": { "type": "boolean" } },
              "required": ["ok"],
              "additionalProperties": false
            }
            """);

        protected override ValueTask<McpToolResult> ExecuteAsync(
            McpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(McpToolResult.Structured(new { ok = true }));
        }

        public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(McpToolResult.Structured(new { ok = true }));
        }
    }

    /// <summary>
    /// <b>Kimlik yolu simülatör yüzeyinde hiç açılmıyor.</b>
    ///
    /// <para>
    /// <c>IMcpIdentityRefusal</c> yalnızca kimlik verildiğinde kaydediliyor;
    /// kayıtlı olmadığında <c>McpCallerScope</c> eski cümlede kalıyor. Bu test o
    /// isteğe bağlılığın gerçekten isteğe bağlı olduğunu ölçüyor — zorunlu
    /// olsaydı simülatör yüzeyi kurulamazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Kimlik_verilmeyen_grafikte_ret_servisi_kayitli_degil()
    {
        using var services = McpCommandHandlers.BuildServices(
            McpSurface.Simulator, clickHouse: null, controlPlane: null);

        Assert.Null(services.GetService(typeof(IMcpIdentityRefusal)));
    }
}

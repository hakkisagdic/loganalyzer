using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Contracts;
using Bizigo.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>M08 · Canlı Keycloak kimliğinin MCP aracına ulaştığının ölçümü.</b>
///
/// <para>
/// <b>KOŞTURULMADI.</b> Yazan ajan Docker'a dokunmuyor (§2); bu dosya
/// koordinatörün canlı yığın koşumunda açılıyor.
/// </para>
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlıyor.</b> Birim paketindeki
/// <c>McpIdentityTests</c> kimliğin MCP oturumundan araca <i>taşındığını</i>
/// ölçüyor, ama kimliği kendisi üretiyor: asgari bir şema, elle kurulmuş
/// claim'ler. Bu test o zincirin <b>ölçülmemiş</b> üç halkasını kapatıyor ve
/// üçü de bu depoda daha önce sessizce kırılmış halkalar:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Keycloak gerçekten <c>groups</c> ve <c>sub</c> basıyor mu.</b> Realm'de
/// yalnızca <c>bizigo-claims</c> client scope var; claim'ler oradan geliyor.
/// Mapper bir gün düşerse token sözdizimsel olarak geçerli kalır ve kapsam
/// <b>sessizce boşalır</b> — kullanıcı "kaydım yok" görür.
/// </item>
/// <item>
/// <b><c>MapInboundClaims = false</c> MCP yolunda da geçerli mi.</b> Ölçülmüş
/// bir kusur: varsayılan <see langword="true"/> iken handler <c>sub</c>'ı
/// <c>.../nameidentifier</c>'a, <c>roles</c>'u görünmezliğe çeviriyor.
/// <c>AccessScopeResolver</c> <c>sub</c>'ı yedeğinden bulduğu için kusur
/// <b>yarı görünmezdi</b>; MCP tarafında da aynı yarı görünürlük olurdu.
/// </item>
/// <item>
/// <b>Keycloak'ın baştaki eğik çizgisi (<c>/network/core</c>) gerçek
/// <c>idp_group_mapping</c> satırlarıyla eşleşiyor mu.</b> Normalizasyon
/// <c>GroupMapping</c>'de ve birim testinde ölçülüyor, ama <b>gerçek token</b>
/// ile gerçek tablo hiç karşılaştırılmadı.
/// </item>
/// </list>
///
/// <para>
/// <b>Neden token ortamdan alınıyor, testin içinde üretilmiyor — ÖLÇÜLDÜ.</b>
/// Realm'deki hiçbir istemcide <c>directAccessGrantsEnabled</c> açık değil
/// (<c>deploy/keycloak/realm-bizigo.json</c>: <c>bizigo-ui</c> ve
/// <c>bizigo-collector</c>, ikisi de <see langword="false"/>). Yani parola
/// akışıyla test içinden kullanıcı belirteci alınamıyor; alınabilmesi için
/// realm'i gevşetmek gerekirdi ve bu bir ürün kararı, bir test kolaylığı değil.
/// Belirteç <c>BIZIGO_MCP_ACCESS_TOKEN</c> ile veriliyor — tarayıcı akışından,
/// BFF oturumundan ya da <c>kcadm</c>'den, koordinatörün seçimi.
/// </para>
///
/// <para>
/// Bunun ikinci bir faydası var: MCP yetkilendirme spesifikasyonunun stdio için
/// söylediği yol da tam olarak bu — <i>"retrieve credentials from the
/// environment"</i>. Test o yolu kullanarak koşuyor.
/// </para>
///
/// <para>
/// <b>Nasıl koşturulur:</b>
/// </para>
/// <code>
/// docker compose up -d keycloak
/// export BIZIGO_MCP_KEYCLOAK=http://localhost:8180/realms/bizigo
/// export BIZIGO_MCP_ACCESS_TOKEN="&lt;analyst.core kullanıcısının erişim belirteci&gt;"
/// dotnet test tests/Bizigo.IntegrationTests --filter FullyQualifiedName~McpKeycloakIdentityTests
/// </code>
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class McpKeycloakIdentityTests(DevStackFixture stack)
{
    /// <summary>Realm adresi — yoksa test <b>atlanıyor</b>, kırmızı yanmıyor (§7).</summary>
    private static string? Authority => Environment.GetEnvironmentVariable("BIZIGO_MCP_KEYCLOAK");

    /// <summary>Canlı Keycloak'ın verdiği <b>kullanıcı</b> erişim belirteci.</summary>
    private static string? AccessToken => Environment.GetEnvironmentVariable("BIZIGO_MCP_ACCESS_TOKEN");

    /// <summary>Belirtecin sahibi olan kullanıcının IdP grubu.</summary>
    private static string IdpGroup =>
        Environment.GetEnvironmentVariable("BIZIGO_MCP_IDP_GROUP") ?? "/network/core";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <b>Canlı Keycloak belirtecinin taşıdığı kimlik, MCP aracının eline
    /// beklenen <c>owner_group</c> kümesi olarak geçiyor.</b>
    ///
    /// <para>
    /// Ölçümün üç ayağı var ve üçü de ayrı bir sessiz kırılmayı kapatıyor:
    /// </para>
    /// <list type="number">
    /// <item>Araç <b>koştu</b> — yani kimlik kapısı gerçek belirteci reddetmedi.</item>
    /// <item>Kapsam <b>boş değil</b> — yani claim'ler token'da gerçekten var ve
    /// eşleme tablosuna değdi. Boş bir kapsam da "geçerli" görünürdü.</item>
    /// <item>Kapsam <b>tam olarak</b> beklenen <c>owner_group</c> — yani ne
    /// eksik ne fazla; sınırsız da değil.</item>
    /// </list>
    ///
    /// <para>
    /// Üçüncüsü olmadan test, <c>admin</c> rolüyle alınmış bir belirteçte de
    /// yeşil yanardı — ve o belirteç <c>AccessScope.System</c> ile <b>bütün
    /// kapsam kapılarını</b> atlıyor olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Canli_Keycloak_kimligi_MCP_aracina_ulasiyor()
    {
        Assert.SkipUnless(
            Authority is not null && AccessToken is not null,
            "BIZIGO_MCP_KEYCLOAK ve BIZIGO_MCP_ACCESS_TOKEN gerekiyor — canlı Keycloak koordinatörde (§2).");

        const string ownerGroup = "core";

        var factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync(Ct);

            // Eşleme tablosu ÜRÜNÜN tablosu. Testin kendi sözlüğünü kurması,
            // ölçmek istediğimiz çevrimi atlamak olurdu (§9).
            if (await db.IdpGroupMappings.FindAsync([IdpGroup.TrimStart('/')], Ct) is null)
            {
                db.IdpGroupMappings.Add(new IdpGroupMappingEntity
                {
                    IdpGroup = IdpGroup.TrimStart('/'),
                    OwnerGroup = ownerGroup,
                    Note = "M08 canlı kimlik ölçümü",
                });

                await db.SaveChangesAsync(Ct);
            }
        }

        var tool = new ScopeEchoTool();

        await using var app = BuildHost(factory, tool);
        await app.StartAsync(Ct);

        // Eşleme belleğe alınıyor — üretimde bunu açılış yapıyor.
        await app.Services.GetRequiredService<AccessScopeResolver>().RefreshAsync(Ct);

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, BizigoMcpServer.HttpPath) },
            http,
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            transport, loggerFactory: NullLoggerFactory.Instance, cancellationToken: Ct);

        var result = await client.CallToolAsync(
            ScopeEchoTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.True(
            result.IsError is not true,
            $"Canlı belirteçle araç hata döndürdü: {result.StructuredContent?.GetRawText()}");

        var payload = result.StructuredContent!.Value.Deserialize<EchoPayload>(McpJson.PayloadOptions)!;

        // (1) Araç koştu.
        Assert.True(tool.Executed);

        // (2) `sub` claim'i geldi — `MapInboundClaims = false` MCP yolunda da
        //     geçerli, yoksa burası boş ya da bir URI olurdu.
        Assert.False(string.IsNullOrWhiteSpace(payload.Subject));

        // (3) Kapsam TAM OLARAK beklenen küme, ve sınırsız DEĞİL.
        Assert.False(
            payload.Unrestricted,
            "Belirteç `admin` rolü taşıyor: bu koşum kapsam çevrimini değil kaçış deliğini ölçtü. "
            + "Ölçüm `analyst.core` gibi gruplu bir kullanıcının belirtecini istiyor.");

        Assert.Equal([ownerGroup], payload.OwnerGroups);

        await app.StopAsync(Ct);
    }

    /// <summary>
    /// <b>Aynı ucun canlı realm'de de kimliksiz isteği reddettiği.</b>
    ///
    /// <para>
    /// M01 bunu asgari bir şema ile ölçtü; burada ölçülen şey gerçek
    /// <c>AddJwtBearer</c> yapılandırması. Ayrı bir test, çünkü kaybı ayrı:
    /// yukarıdaki test kimlikli koşuyor ve <c>RequireAuthorization()</c> bir gün
    /// silinse bile <b>geçmeye devam ederdi</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Canli_realmde_kimliksiz_MCP_istegi_401_aliyor()
    {
        Assert.SkipUnless(
            Authority is not null,
            "BIZIGO_MCP_KEYCLOAK gerekiyor — canlı Keycloak koordinatörde (§2).");

        var factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var app = BuildHost(factory, new ScopeEchoTool());
        await app.StartAsync(Ct);

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress(app)) };

        using var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await http.PostAsync(
            new Uri(BizigoMcpServer.HttpPath, UriKind.Relative), body, Ct);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(Ct);
    }

    /// <summary>
    /// Gerçek kimlik yapılandırmasıyla ayağa kalkan asgari bir ürün host'u.
    ///
    /// <para>
    /// <c>WebApplicationFactory&lt;Program&gt;</c> <b>kullanılmıyor</b>:
    /// <c>Program</c> tipi bu derlemede hem <c>Bizigo.Api</c>'den hem
    /// <c>Bizigo.Cli</c>'den görünüyor ve csproj yorumu o günü tarif ediyor
    /// (<i>"o gün `Program`'a takma ad vermek gerekir"</i>). Takma ad vermek bu
    /// ticket'ın işi değil; kimlik yolunu ölçmek için host'u elle kurmak yeterli
    /// ve <c>AddBizigoAuthentication</c> / <c>AddBizigoMcp</c> / <c>MapBizigoMcp</c>
    /// hâlâ <b>üretimin kendi kayıtları</b>.
    /// </para>
    /// </summary>
    private static WebApplication BuildHost(
        IDbContextFactory<ControlPlaneDbContext> factory,
        ScopeEchoTool tool)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AuthOptions.SectionName}:Enabled"] = "true",
            [$"{AuthOptions.SectionName}:Authority"] = Authority,
            [$"{AuthOptions.SectionName}:RequireHttpsMetadata"] = "false",
        });

        builder.Services.AddRouting();
        builder.Services.AddSingleton(factory);

        // ÜRETİMİN kimlik kurulumu — `IAccessScopeResolver` kaydı da buradan.
        builder.Services.AddBizigoAuthentication(builder.Configuration);
        builder.Services.AddBizigoMcp(builder.Configuration);

        // Ölçümün öznesi: kimlik isteyen bir ürün aracı. M04 gelene kadar
        // ürün yüzeyinde böyle bir araç yok (`server.info` gerekçeli muaf),
        // dolayısıyla kapının ölçülebilmesi için bir özne gerekiyor.
        builder.Services.PostConfigure<McpServerOptions>(
            options => options.ToolCollection!.Add(tool));

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBizigoMcp();

        return app;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Urls.FirstOrDefault()
        ?? throw new InvalidOperationException("Host hiç adres bağlamadı.");

    /// <summary>
    /// Birim paketindeki ikizinin canlı hâli. <b>İkinci kopya değil bir
    /// özne:</b> ölçülen şey aracın kendisi değil, aracın eline geçen kapsam —
    /// ve o kapsamı üreten yol ürünün kendi yolu.
    /// </summary>
    private sealed class ScopeEchoTool : BizigoMcpTool
    {
        public const string ToolIdentifier = "test.scope_echo";

        public bool Executed { get; private set; }

        public override string ToolName => ToolIdentifier;

        public override McpSurface Surface => McpSurface.Product;

        public override string ToolTitle => "Kapsamı yankıla";

        public override string ToolDescription => "M08 canlı kimlik ölçümünün öznesi.";

        public override JsonElement InputSchema { get; } = McpSchema.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        public override JsonElement OutputSchema { get; } = McpSchema.Parse(
            """
            {
              "type": "object",
              "properties": {
                "subject":      { "type": "string" },
                "owner_groups": { "type": "array", "items": { "type": "string" } },
                "unrestricted": { "type": "boolean" }
              },
              "required": ["subject", "owner_groups", "unrestricted"],
              "additionalProperties": false
            }
            """);

        protected override ValueTask<McpToolResult> ExecuteAsync(
            McpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Executed = true;

            return ValueTask.FromResult(McpToolResult.Structured(Shape(invocation.Scope)));
        }

        public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(McpToolResult.Structured(Shape(AccessScope.Denied)));
        }

        private static EchoPayload Shape(AccessScope scope) => new(
            scope.Subject,
            [.. scope.OwnerGroups.Order(StringComparer.Ordinal)],
            scope.IsUnrestricted);
    }

    private sealed record EchoPayload(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("owner_groups")] IReadOnlyList<string> OwnerGroups,
        [property: JsonPropertyName("unrestricted")] bool Unrestricted);
}

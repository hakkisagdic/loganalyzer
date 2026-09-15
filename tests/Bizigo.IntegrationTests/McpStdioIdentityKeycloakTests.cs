using Bizigo.Cli;
using Bizigo.Contracts;
using Bizigo.Mcp;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>stdio kimliğinin CANLI Keycloak'a karşı davranışı</b> (M13).
///
/// <para>
/// Birim paketi (<c>McpStdioIdentityTests</c>) <b>kararları</b> ölçüyor: hangi
/// ayarlar zorunlu, kitle doğrulanıyor mu, süre sonu cümlesi ayrı mı. Üçünü de
/// canlı bir IdP olmadan ölçmek mümkün ve doğru. <b>Ölçemediği şey doğrulamanın
/// kendisi</b>: keşif belgesinin inmesi, imza anahtarlarının bulunması ve
/// gerçek bir belirtecin kabul/ret kararı.
/// </para>
///
/// <h3>Öznesi M09'un kararı</h3>
///
/// <para>
/// <c>McpKeycloakIdentityTests</c> API için basılmış bir belirtecin
/// <c>POST /mcp</c>'de <b>401</b> aldığını ölçüyor (RFC 8707). stdio aynı
/// kararın ikinci yüzeyi ve <b>aynı</b> şekilde davranmalı: kitlesi
/// <c>bizigo-mcp</c> olmayan bir belirteç burada da geçmemeli. İki yüzeyin aynı
/// belirteç hakkında farklı karar vermesi, kitle ayrımını anlamsız kılardı.
/// </para>
///
/// <para>
/// ⚠️ <b>§2 — BU TESTLER BU TURDA KOŞTURULMADI</b> ve koşturulamazdı: canlı
/// Keycloak ve gerçek belirteçler gerekiyor, ve bu makinede
/// <c>localhost:8180</c> <i>Connection refused</i> döndü (ölçüldü). <b>Yeşil
/// gösterilmiyor.</b>
/// </para>
///
/// <h3>Belirteçler ortamdan — ve ÜÇÜ AYRI değişken</h3>
///
/// <para>
/// Üçünü aynı değişkene koymak testi sessizce anlamsızlaştırır; aynı gerekçe
/// <c>McpKeycloakIdentityTests</c>'te de yazılı.
/// </para>
/// </summary>
public sealed class McpStdioIdentityKeycloakTests
{
    /// <summary>Realm adresi — issuer.</summary>
    private static string? Authority => Environment.GetEnvironmentVariable("BIZIGO_MCP_KEYCLOAK");

    /// <summary>
    /// <c>bizigo-mcp</c> kitlesiyle basılmış, <b>geçerli</b> belirteç.
    /// </summary>
    private static string? McpToken => Environment.GetEnvironmentVariable("BIZIGO_MCP_ACCESS_TOKEN");

    /// <summary>
    /// <c>bizigo-mcp</c> scope'u <b>istenmeden</b> alınmış belirteç — API'nin
    /// gündelik token'ı. Ölçümün öznesi: bunun <b>reddedilmesi</b>.
    /// </summary>
    private static string? ApiOnlyToken => Environment.GetEnvironmentVariable("BIZIGO_MCP_API_TOKEN");

    /// <summary>
    /// Beklenen <c>aud</c>. <b>Varsayılanı yok ve olmayacak:</b> bir varsayılan
    /// yazmak, realm'in bastığı kitleyi ikinci kez temsil etmek olurdu — ve
    /// ölçülerek bulundu ki iki temsil ayrışmıştı (bekçisi
    /// <c>KeycloakRealmTests.Mcp_kitlesi_realmde_ve_belgede_ayni</c>). Değeri
    /// operatör veriyor; eksikse ölçüm <b>atlanıyor</b>, yanlış bir dizgeyle
    /// koşup <c>unauthenticated</c> ölçmüyor.
    /// </summary>
    private static string? Resource => Environment.GetEnvironmentVariable("BIZIGO_MCP_RESOURCE");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpStdioIdentitySettings Settings(string? token) =>
        new(token, Authority, Resource, MetadataAddress: null);

    /// <summary>
    /// <b>Geçerli belirteç kabul ediliyor ve kapsam BOŞ DEĞİL.</b>
    ///
    /// <para>
    /// Üç ayak, üçü de ayrı bir sessiz kırılmayı kapatıyor — kalıp
    /// <c>McpKeycloakIdentityTests</c>'ten:
    /// </para>
    /// <list type="number">
    /// <item>Doğrulama <b>geçti</b> — yani kurallar gerçek bir belirteci reddetmiyor.</item>
    /// <item>Kimlik <b>üretildi</b> ve <c>Reason</c> <see langword="null"/> — yani
    /// süresi dolmuş sayılmıyor.</item>
    /// <item><c>sub</c> claim'i <b>var</b> — kapsam çevriminin girdisi orada.
    /// Boş bir principal da "geçerli" görünürdü.</item>
    /// </list>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gecerli_belirtec_kabul_ediliyor()
    {
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(Authority)
            && !string.IsNullOrWhiteSpace(McpToken)
            && !string.IsNullOrWhiteSpace(Resource),
            "BIZIGO_MCP_KEYCLOAK, BIZIGO_MCP_ACCESS_TOKEN ve BIZIGO_MCP_RESOURCE gerekiyor — `bizigo-mcp` "
            + "kitlesiyle basılmış geçerli bir belirteç. Canlı Keycloak koordinatörde (§2).");

        var (identity, failure) = await McpStdioIdentity.ValidateAsync(Settings(McpToken), cancellationToken: Ct);

        Assert.Null(failure);
        Assert.NotNull(identity);
        Assert.Null(identity!.Reason);

        var principal = identity.Principal();

        Assert.NotNull(principal);
        Assert.True(principal!.Identity?.IsAuthenticated);

        Assert.False(
            string.IsNullOrWhiteSpace(principal.FindFirst("sub")?.Value),
            "Belirteçte `sub` yok — kapsam çevriminin girdisi eksik ve kapsam boş çıkardı.");

        // SÜRE SONU YAZILI. `exp` taşımayan bir belirteç reddediliyor, çünkü bu
        // yüzey yenileme yapmıyor ve sınırsız bir stdio kimliği kabul edilmiyor.
        Assert.True(identity.ExpiresAt > DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <b>API için basılmış belirteç stdio'da da REDDEDİLİYOR</b> — M13'ün
    /// ikinci şartı, M09'un kararının ikinci yüzeyi (RFC 8707).
    ///
    /// <para>
    /// Ölçüt <i>"reddetti mi"</i> değil <b>"KİTLE yüzünden mi reddetti"</b>:
    /// imzası bozuk bir dizge de reddedilir ve o red bu iddiayı sağlamazdı.
    /// Mesaj <c>aud</c>'u adıyla söylemeli.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Api_icin_basilmis_belirtec_reddediliyor()
    {
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(Authority)
            && !string.IsNullOrWhiteSpace(ApiOnlyToken)
            && !string.IsNullOrWhiteSpace(Resource),
            "BIZIGO_MCP_KEYCLOAK, BIZIGO_MCP_API_TOKEN ve BIZIGO_MCP_RESOURCE gerekiyor — `bizigo-mcp` scope'u "
            + "İSTENMEDEN alınmış bir belirteç. Canlı Keycloak koordinatörde (§2).");

        Assert.NotEqual(McpToken, ApiOnlyToken);

        var (identity, failure) = await McpStdioIdentity.ValidateAsync(
            Settings(ApiOnlyToken), cancellationToken: Ct);

        Assert.Null(identity);
        Assert.NotNull(failure);

        // KİTLE SEBEBİYLE, başka bir sebeple değil.
        Assert.Contains("aud", failure, StringComparison.Ordinal);
        Assert.Contains(Resource, failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Süresi dolan belirtecin reddi, kimliğin yokluğundan AYIRT
    /// EDİLEBİLİYOR</b> — ve bu, birim testinin ölçtüğü cümlenin gerçek bir
    /// belirteç üzerinde doğrulanması.
    ///
    /// <para>
    /// Birim testi <c>ExpiryReason</c>'ı saf olarak ölçüyor; buradaki fark
    /// <c>Principal()</c> ile <c>Reason</c>'ın <b>birlikte</b> döndüğü hâl:
    /// kimlik <see langword="null"/> olurken sebep <b>dolu</b> olmalı. İkisi
    /// ayrışırsa yüzey ya süresi geçmiş bir kimlikle çalışır ya sebebini
    /// söylemez.
    /// </para>
    ///
    /// <para>
    /// Saat <b>ileri alınıyor</b>, beklenmiyor: gerçek bir belirtecin süresinin
    /// dolmasını beklemek testi duvar saatine bağlardı (§6) ve dakikalar
    /// sürerdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Suresi_dolan_belirtec_ayri_sebep_donduruyor()
    {
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(Authority)
            && !string.IsNullOrWhiteSpace(McpToken)
            && !string.IsNullOrWhiteSpace(Resource),
            "BIZIGO_MCP_KEYCLOAK, BIZIGO_MCP_ACCESS_TOKEN ve BIZIGO_MCP_RESOURCE gerekiyor. Canlı Keycloak "
            + "koordinatörde (§2).");

        var (identity, failure) = await McpStdioIdentity.ValidateAsync(Settings(McpToken), cancellationToken: Ct);

        Assert.Null(failure);
        Assert.NotNull(identity);

        // Doğrulama ANINDA geçerli — yoksa aşağıdaki iddia boş bir kimlikte
        // yeşil yanardı (§6).
        Assert.NotNull(identity!.Principal());

        var beyond = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            identity.ExpiresAt.AddSeconds(1));

        var (expired, _) = await McpStdioIdentity.ValidateAsync(
            Settings(McpToken), beyond, Ct);

        // Doğrulamanın KENDİSİ süre sonunu görüyor: `ValidateLifetime` açık,
        // yani bu noktada belirteç zaten reddediliyor. Kimlik üretilmiyor.
        Assert.Null(expired);
    }
}

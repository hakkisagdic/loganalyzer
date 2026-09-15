using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>M14'ün iki aracı GERÇEK Postgres'in üstünde.</b>
///
/// <para>
/// Birim paketi (<c>McpRcaToolTests</c>) <b>bellek içi</b> EF ile koşuyor ve
/// oradaki iddialar geçerli: anahtar sunucudan geliyor, reddedilen satır yükte
/// duruyor, kapsam dışı gruba yazılmıyor. <b>Ölçemediği iki şey</b> var ve
/// ikisi de bu depoda ısırdı:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Kapsam filtresinin SQL'e çevrilmesi.</b> Bellek içinde
/// <c>groups.Contains(r.OwnerGroup)</c> LINQ olarak <i>değerlendiriliyor</i>;
/// Postgres'te <c>= ANY(@groups)</c>'a <b>çevrilmek</b> zorunda. Çeviri
/// kaybolsa bellek içi paket <b>yeşil kalır</b> — T38'de ölçülmüş, M10'da
/// tekrarlanmış boşluk.
/// </item>
/// <item>
/// <b>İdempotency anahtarının benzersizlik davranışı.</b> Bellek içi sağlayıcı
/// kısıtları farklı uyguluyor; aynı anahtarla iki eşzamanlı kabulün <b>tek</b>
/// satır bırakması ilişkisel tarafta ölçülmeli.
/// </item>
/// </list>
///
/// <para>
/// ⚠️ <b>§2 — BU TESTLER BU TURDA KOŞTURULMADI.</b> Docker koordinatörde ve bu
/// makinede yığın ayakta değil. <b>Yeşil gösterilmiyor.</b>
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class McpRcaToolChainTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["net-core"]);

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(Ct);
        await db.Database.MigrateAsync(Ct);

        // Bu sınıfın iddiaları SAYILARA bakıyor; başka bir sınıftan kalan satır
        // "kapsam sızdırdı"ya benzeyen bir sayı üretir ve sebebi bu dosyada
        // aranmaz. Aynı gerekçe `ScopeNegativeTests`'te de yazılı.
        await db.RcaRuns.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// <b>Kapsam filtresi Postgres tarafında eleniyor — ve daha YENİ satır
    /// kapsam dışı.</b>
    ///
    /// <para>
    /// Kurulum bilerek zor: kapsam dışı koşum daha yeni ve <c>limit=1</c>.
    /// Filtre <c>Take</c>'ten sonra uygulansaydı cevap <b>boş</b> dönerdi — ve
    /// boş listenin garantisi (<i>"hiç tetiklenmedi"</i>) sessizce yalan olurdu.
    /// Bellek içi paket bu ayrımı yakalıyor ama <b>çevirinin</b> varlığını
    /// yakalamıyor.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Rca_runs_kapsami_SQL_tarafinda_uyguluyor()
    {
        await using (var db = await _factory.CreateDbContextAsync(Ct))
        {
            db.RcaRuns.Add(Run("net-core", Now));
            db.RcaRuns.Add(Run("net-edge", Now.AddMinutes(5)));

            await db.SaveChangesAsync(Ct);
        }

        var result = await new RcaRunsTool(_factory).ExecuteScopedAsync(
            Invocation(("limit", 1)), Core, Ct);

        var groups = result.Payload.GetProperty("runs").EnumerateArray()
            .Select(static r => r.GetProperty("owner_group").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["net-core"], groups);
    }

    /// <summary>
    /// <b>Aynı anahtar ilişkisel tarafta da TEK satır bırakıyor.</b>
    ///
    /// <para>
    /// İdempotans birim testinde bellek içi sağlayıcıyla ölçüldü; burada
    /// ölçülen şey aynı davranışın <b>gerçek</b> veritabanında sürmesi. Ve
    /// ikinci iddia asıl olan: <c>existing</c> ikinci çağrıda <b>true</b> —
    /// yoksa model aynı cevabı iki kez alıp iki koşum sanardı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Rca_trigger_ayni_anahtarla_tek_satir_biraktiyor()
    {
        var tool = new RcaTriggerTool(Scopes(), new FakeTimeProvider(Now));
        var arguments = Invocation(("owner_group", "net-core"));

        var first = await tool.ExecuteScopedAsync(arguments, Core, Ct);
        var second = await tool.ExecuteScopedAsync(arguments, Core, Ct);

        Assert.False(first.IsError);
        Assert.False(second.IsError);

        Assert.False(first.Payload.GetProperty("existing").GetBoolean());
        Assert.True(second.Payload.GetProperty("existing").GetBoolean());

        Assert.Equal(
            first.Payload.GetProperty("run_id").GetGuid(),
            second.Payload.GetProperty("run_id").GetGuid());

        await using var db = await _factory.CreateDbContextAsync(Ct);

        Assert.Equal(1, await db.RcaRuns.CountAsync(Ct));

        // ANAHTAR SUNUCUDAN — gerçek satırdan okunuyor.
        var run = await db.RcaRuns.SingleAsync(Ct);

        Assert.Equal(
            McpIdempotency.KeyFor(Core.Subject, ["net-core"], Now.AddHours(-24), Now),
            run.IdempotencyKey);

        Assert.Equal(RcaTriggerSource.Agent, run.Source);
    }

    // ---------------------------------------------------------------------

    private IServiceScopeFactory Scopes() =>
        new ServiceCollection()
            .AddSingleton(_factory)
            .AddSingleton<IRcaQuotaGate>(new AlwaysAllowQuotaGate())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<RcaAdmission>>(
                NullLogger<RcaAdmission>.Instance)
            .AddScoped<RcaAdmission>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static RcaRunEntity Run(string ownerGroup, DateTimeOffset requestedAt) => new()
    {
        OwnerGroup = ownerGroup,
        Source = RcaTriggerSource.Agent,
        TriggerIdentity = "mcp:analyst.core",
        State = RcaRunState.Complete,
        CountsAgainstQuota = true,
        RequestedAt = requestedAt,
        WindowFrom = requestedAt.AddHours(-1),
        WindowTo = requestedAt,
    };

    private static Bizigo.Mcp.McpToolInvocation Invocation(params (string Name, object? Value)[] arguments)
    {
        var map = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["owner_group"] = System.Text.Json.JsonSerializer.SerializeToElement("net-core"),
        };

        foreach (var (name, value) in arguments)
        {
            map[name] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        }

        // Kapsam gövdeye PARAMETRE olarak geliyor; çağrı nesnesindeki `Denied`
        // bilerek — gerekçe `McpReadToolFixtures`'ta.
        return new Bizigo.Mcp.McpToolInvocation(map, AccessScope.Denied);
    }
}

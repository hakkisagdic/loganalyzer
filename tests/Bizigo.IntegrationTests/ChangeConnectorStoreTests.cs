using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Connector tablosunun <b>gerçek</b> Postgres'e karşı sözleşmesi (T25).
///
/// <para>
/// Birim testleri bu servisi EF'in bellek içi sağlayıcısıyla sınıyor ve o
/// sağlayıcı <b>benzersizlik indekslerini zorlamıyor</b> — yani "aynı slug iki
/// connector'a verilemiyor" testi orada yalnızca uygulama katmanındaki
/// kontrolü kanıtlıyor. Kısıtın göçte gerçekten kurulduğunu ancak buradaki
/// test gösteriyor; ikisi birlikte "iki katman da tutuyor" demek.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ChangeConnectorStoreTests(DevStackFixture stack) : IAsyncLifetime
{
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.ChangeConnectorRuns.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.ChangeConnectors.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ChangeConnectorEntity Connector(string slug, string ownerGroup = "network/core") => new()
    {
        Slug = slug,
        Name = $"Connector {slug}",
        ConnectorType = ChangeConnectorType.Webhook,
        OwnerGroup = ownerGroup,
        ConfigJson = """{"provider":"github"}""",
        CredentialCipher = "c2FodGUtc2lmcmVsaS1tZXRpbg==",
        Enabled = true,
    };

    [Fact]
    public async Task Slug_benzersizligi_veritabaninda_zorlaniyor()
    {
        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        db.ChangeConnectors.Add(Connector("gh-network"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Slug bir URL parçası: iki connector aynı slug'ı alsaydı webhook
        // çağrısının hangi gruba yazacağı isteğe göre değişirdi. Uygulama
        // katmanı bunu zaten kontrol ediyor, ama iki eşzamanlı kayıt arasında
        // yarış penceresi kalıyor ve onu ancak kısıt kapatıyor.
        db.ChangeConnectors.Add(Connector("gh-network", "network/edge"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Farkli_gruplar_farkli_slug_larla_yan_yana_durabiliyor()
    {
        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        db.ChangeConnectors.Add(Connector("gh-core", "network/core"));
        db.ChangeConnectors.Add(Connector("gh-edge", "network/edge"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.ChangeConnectors.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kosum_gecmisi_saklama_kesimiyle_siliniyor()
    {
        // Saklama temizliğinin sildiği şey: ÇALIŞMA GEÇMİŞİ. `change_events`
        // — RCA'nın F3'te arayacağı asıl veri — bu politikanın dışında ve
        // hiç silinmiyor.
        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var connector = Connector("gh-network");
        db.ChangeConnectors.Add(connector);

        var now = DateTimeOffset.UtcNow;

        db.ChangeConnectorRuns.Add(new ChangeConnectorRunEntity
        {
            ConnectorId = connector.Id,
            StartedAt = now.AddDays(-120),
            FinishedAt = now.AddDays(-120),
            State = ConnectorRunState.Succeeded,
        });

        db.ChangeConnectorRuns.Add(new ChangeConnectorRunEntity
        {
            ConnectorId = connector.Id,
            StartedAt = now.AddDays(-1),
            FinishedAt = now.AddDays(-1),
            State = ConnectorRunState.Succeeded,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var cutoff = now.AddDays(-90);

        var deleted = await db.ChangeConnectorRuns
            .Where(r => r.StartedAt < cutoff)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await db.ChangeConnectorRuns.CountAsync(TestContext.Current.CancellationToken));
    }
}

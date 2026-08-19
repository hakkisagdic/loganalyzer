using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// İdempotans anahtarı — <b>gerçek</b> Postgres'e karşı (T24 kabul kriteri:
/// aynı webhook iki kez gelirse ikinci kayıt oluşmaz).
///
/// <para>
/// Konteyner burada isteğe bağlı bir lüks değil: sınanan şey uygulama kodu değil,
/// veritabanının <b>benzersizlik kısıtı</b>. Bellek içi bir sağlayıcıyla koşan
/// aynı test yalnızca "önce SELECT sonra INSERT" mantığını sınardı — yani tam
/// olarak yarış durumunda çöken parçayı. <see cref="Es_zamanli_teslimatlarin_yalnizca_biri_talebi_aliyor"/>
/// göç dosyasının kısıtı gerçekten kurduğunu doğruluyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ChangeWebhookDeliveryTests(DevStackFixture stack) : IAsyncLifetime
{
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private ChangeWebhookDeliveryLog _log = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);
        _log = new ChangeWebhookDeliveryLog(_factory);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.ChangeWebhookDeliveries.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ChangeWebhookDeliveryEntity Delivery(string key, Guid? changeId = null) => new()
    {
        DeliveryKey = key,
        EndpointId = "gh-network",
        Provider = "github",
        OwnerGroup = "network/core",
        ChangeId = changeId ?? Guid.CreateVersion7(),
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Ilk_teslimat_talebi_aliyor()
    {
        var claim = await _log.ClaimAsync(Delivery("gh-network:d1"), TestContext.Current.CancellationToken);

        Assert.True(claim.Claimed);
    }

    [Fact]
    public async Task Ayni_teslimat_ikinci_kez_kayit_olusturmuyor()
    {
        var first = Delivery("gh-network:d2");

        var one = await _log.ClaimAsync(first, TestContext.Current.CancellationToken);

        // Sağlayıcının yeniden denemesi: aynı teslimat anahtarı, YENİ bir
        // change_id ile geliyor (eşleme her çağrıda yeni bir kimlik üretiyor).
        var two = await _log.ClaimAsync(Delivery("gh-network:d2"), TestContext.Current.CancellationToken);

        Assert.True(one.Claimed);
        Assert.False(two.Claimed);

        // İkinci cevap İLK kaydın kimliğini taşıyor: sağlayıcı iki kez sorduğunda
        // iki farklı cevap almamalı.
        Assert.Equal(first.ChangeId, two.ChangeId);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.ChangeWebhookDeliveries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Es_zamanli_teslimatlarin_yalnizca_biri_talebi_aliyor()
    {
        // Asıl sınanan bu: "önce SELECT sonra INSERT" mantığı burada çöker ve
        // benzersizlik kısıtı devreye girer. Kısıt göçte kurulmamışsa test
        // kırmızı yanar.
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => _log.ClaimAsync(Delivery("gh-network:d3"), TestContext.Current.CancellationToken))
            .ToArray();

        var claims = await Task.WhenAll(attempts);

        Assert.Equal(1, claims.Count(c => c.Claimed));

        // Kaybedenlerin hepsi kazananın kimliğini görüyor.
        var winner = claims.Single(c => c.Claimed).ChangeId;
        Assert.All(claims, c => Assert.Equal(winner, c.ChangeId));

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.ChangeWebhookDeliveries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Farkli_uclar_ayni_govdeyi_ayri_kaydediyor()
    {
        // Anahtarın uç kimliğiyle öneklenmesinin sebebi: iki ayrı ucun aynı
        // gövdeyi alması meşru ve ikisi de kendi grubuna yazmalı.
        Assert.True((await _log.ClaimAsync(Delivery("gh-network:ayni"), TestContext.Current.CancellationToken)).Claimed);
        Assert.True((await _log.ClaimAsync(Delivery("gh-server:ayni"), TestContext.Current.CancellationToken)).Claimed);
    }

    [Fact]
    public async Task Yazma_basarisiz_olursa_talep_geri_aliniyor()
    {
        // Tek bir geçici ClickHouse hatası olayı KALICI olarak kaybettirmemeli:
        // talep durursa sağlayıcının yeniden denemesi sessizce "mükerrer" sayılır.
        Assert.True((await _log.ClaimAsync(Delivery("gh-network:d4"), TestContext.Current.CancellationToken)).Claimed);

        await _log.ReleaseAsync("gh-network:d4", TestContext.Current.CancellationToken);

        Assert.True((await _log.ClaimAsync(Delivery("gh-network:d4"), TestContext.Current.CancellationToken)).Claimed);
    }
}

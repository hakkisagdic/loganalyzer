using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Kanıt paketi gerçek Postgres'e karşı (T36).
///
/// <para>
/// Birim testleri bellek içi sağlayıcıyla koşuyor ve orada <c>jsonb</c> diye
/// bir şey yok — kolon <c>string</c> gibi davranıyor. Yani "paket saklanıyor"
/// iddiasının <b>gerçekten</b> sınandığı tek yer burası: göç uygulanıyor mu,
/// <c>jsonb</c> kolonu Türkçe/CJK gövdeleri bozmadan taşıyor mu, ve indeksler
/// gerçekten kuruluyor mu.
/// </para>
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlayacak:</b> bugün yazılan bir paketin altı ay
/// sonra aynı baytlarla geri okunabildiği — F4'ün "aynı kanıt üzerinde farklı
/// model" karşılaştırmasının tamamı buna dayanıyor. Bellek içi sağlayıcı bunu
/// kanıtlayamaz çünkü serileştirme yolunu hiç kullanmıyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class EvidenceBundleStorageTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // Gerçek göç — testin şemayı kendi kurması, üretimden ayrışmasına yol açardı.
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.EvidenceBundles.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private EvidenceBundleStore Store() => new(_factory);

    private static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
        OwnerGroups = ["network/core"],
    };

    /// <summary>
    /// Gövdesi <b>Türkçe ve CJK</b> olan bir kanıt satırı. Kodlama yolunda bir
    /// sorun varsa hash gidiş-dönüşte değişiyor ve paket sessizce "başka bir
    /// kanıt" oluyor — ingest zaten NFC'ye çeviriyor, dolayısıyla bu satırlar
    /// gerçek veride var.
    /// </summary>
    private static EvidenceBundle Bundle() => new()
    {
        Id = Guid.CreateVersion7(Now),
        GatheredAt = Now,
        Window = Window(),
        Scope = new BundleScope(["network/core"], IsSystem: false),
        Trust = new WindowTrust(1_204, 142),
        Slices =
        [
            new EvidenceSlice
            {
                ProviderId = "logs.first-seen",
                Kind = EvidenceKind.Log,
                Status = EvidenceStatus.Gathered,
                Items =
                [
                    new EvidenceItem(
                        "first-seen:42",
                        "logs.first-seen",
                        EvidenceKind.Log,
                        Now.AddMinutes(2),
                        14,
                        "ilk kez görüldü · kullanıcı girişi başarısız · 用户登录失败",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["signature_hash"] = "14733834131172344067",
                            ["sample_body"] = "deny src=10.0.0.1 \"tırnaklı\" ve {süslü}",
                        }),
                ],
            },
            new EvidenceSlice
            {
                ProviderId = "change.feed",
                Kind = EvidenceKind.Change,
                Status = EvidenceStatus.NeverFed,
                Detail = "Değişiklik akışında hiç kayıt yok — besleme bağlı olmayabilir.",
                OutOfScopeCount = 342,
            },
        ],
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Paket_jsonb_kolonundan_ayni_donuyor()
    {
        var original = Bundle();
        var store = Store();

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(original.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);

        // Hash'in korunması, baytların korunmasından daha güçlü bir iddia:
        // Postgres jsonb anahtar sırasını normalleştiriyor, yani metin birebir
        // aynı olmayabilir ama KANIT aynı olmak zorunda.
        Assert.Equal(original.ContentHash, loaded.ContentHash);
        Assert.Equal(
            "ilk kez görüldü · kullanıcı girişi başarısız · 用户登录失败",
            loaded.Slices.Single(s => s.ProviderId == "logs.first-seen").Items[0].Summary);
        Assert.Equal(EvidenceStatus.NeverFed, loaded.Slices.Single(s => s.ProviderId == "change.feed").Status);
        Assert.Equal(342, loaded.OutOfScopeCount);
        Assert.Equal(142, loaded.Trust.UnreliableTimeEvents);
    }

    /// <summary>
    /// Saklanan paketten üretilen rapor, taze paketten üretilenle <b>birebir
    /// aynı</b>. Bu, "kanıt paketi saklanır → rapor tekrar üretilebilir"
    /// (RCA §2, kural 3) iddiasının çalıştırılabilir hâli.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Saklanan_paketten_ayni_rapor_uretiliyor()
    {
        var original = Bundle();
        var store = Store();

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(original.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(
            DeterministicReport.From(original).ToMarkdown(),
            DeterministicReport.From(loaded).ToMarkdown());
    }

    /// <summary>
    /// Kolon üst verisi <b>gerçekten</b> sorgulanabiliyor — belge açılmadan.
    /// Liste ekranının (T37) her satır için JSON çözmemesinin tamamı bu.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ust_veri_belge_acilmadan_sorgulanabiliyor()
    {
        var store = Store();
        var bundle = Bundle();

        await store.SaveAsync(bundle, TestContext.Current.CancellationToken);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var found = await db.EvidenceBundles
            .Where(b => b.WindowFrom >= Now.AddMinutes(-1) && b.WindowTo <= Now.AddHours(1))
            .Select(b => new { b.Id, b.ContentHash, b.OutOfScopeCount, b.SchemaVersion })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(bundle.Id, found.Id);
        Assert.Equal(bundle.ContentHash, found.ContentHash);
        Assert.Equal(342, found.OutOfScopeCount);
        Assert.Equal(EvidenceBundle.CurrentSchemaVersion, found.SchemaVersion);
    }

    /// <summary>
    /// Aynı pencerenin iki kez toplanması reddedilmiyor — F4'ün karşılaştırma
    /// akışı tam olarak bunu yapıyor. <c>content_hash</c> üzerinde <b>tekil</b>
    /// bir kısıt olsaydı ikinci koşu veritabanı hatasıyla düşerdi.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ayni_hash_iki_kez_yazilabiliyor()
    {
        var store = Store();
        var first = Bundle();

        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        await store.SaveAsync(first with { Id = Guid.CreateVersion7(Now.AddMinutes(1)) },
            TestContext.Current.CancellationToken);

        var rows = await store.ListRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.ContentHash).Distinct(StringComparer.Ordinal));
    }
}

using Bizigo.ControlPlane;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// Kanıt paketinin kalıcılığı (T36).
///
/// <para>
/// Sınanan şey "kaydediliyor mu" değil — o zaten EF'in işi. Sınanan iki şey:
/// <b>geri okunan paket aynı paket mi</b> (yoksa F4'ün karşılaştırması
/// karşılaştırdığı şeyi bilemez) ve <b>okunamayan bir sürüm sessizce boş
/// dönüyor mu</b> (dönerse eksik küme üzerinde çalışılır ve kimse fark etmez).
/// </para>
/// </summary>
public sealed class EvidenceBundleStoreTests : IDisposable
{
    private readonly InMemoryControlPlaneFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private EvidenceBundleStore Store() => new(_factory);

    private static EvidenceBundle Bundle() => EvidenceBundleTests.Bundle(
        EvidenceBundleTests.Slice("logs.first-seen", items: [("a", 3.0), ("b", 1.0)]),
        EvidenceBundleTests.Slice("change.feed", EvidenceStatus.NeverFed, outOfScope: 342));

    [Fact]
    public async Task Gidis_donus_ayni_paketi_veriyor()
    {
        var original = Bundle();
        var store = Store();

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(original.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(original.ContentHash, loaded.ContentHash);
        Assert.Equal(original.Trust, loaded.Trust);

        // Liste alanları kayıt eşitliğinde referansla karşılaştırılıyor; burada
        // sorulan şey içerik.
        Assert.Equal(original.Scope.OwnerGroups, loaded.Scope.OwnerGroups);
        Assert.Equal(original.Scope.IsSystem, loaded.Scope.IsSystem);
        Assert.Equal(2, loaded.Slices.Count);

        // Rapor da aynı çıkıyor: saklanan paketten üretilen rapor, taze
        // paketten üretilenle birebir aynı olmak zorunda — yoksa "aynı kanıt
        // üzerinde karşılaştırma" iddiası raporun kendisinde çöker.
        Assert.Equal(
            DeterministicReport.From(original).ToMarkdown(),
            DeterministicReport.From(loaded).ToMarkdown());
    }

    /// <summary>
    /// Kolondaki üst veri ile belgenin içi <b>ayrışmıyor</b>. İkisi kopya ve
    /// kopyalar ayrışır; tek yazan taraf olması ve bu testin varlığı, ayrışmanın
    /// sessiz kalmamasını sağlıyor.
    /// </summary>
    [Fact]
    public async Task Kolon_ust_verisi_belgeyle_ayni()
    {
        var bundle = Bundle();
        await Store().SaveAsync(bundle, TestContext.Current.CancellationToken);

        await using var db = _factory.CreateDbContext();
        var row = db.EvidenceBundles.Single();
        var document = BundleSerializer.Deserialize(row.Payload);

        Assert.Equal(document.Id, row.Id);
        Assert.Equal(document.ContentHash, row.ContentHash);
        Assert.Equal(document.SchemaVersion, row.SchemaVersion);
        Assert.Equal(document.Window.From, row.WindowFrom);
        Assert.Equal(document.Window.To, row.WindowTo);
        Assert.Equal(document.Window.BaselineFrom, row.BaselineFrom);
        Assert.Equal(document.Window.BaselineTo, row.BaselineTo);
        Assert.Equal(document.OutOfScopeCount, row.OutOfScopeCount);
        Assert.Equal(document.IsPartial, row.IsPartial);
    }

    /// <summary>
    /// Olmayan paket <c>null</c>; <b>okunamayan</b> paket istisna.
    ///
    /// <para>
    /// "Paket yok" ile "paket var ama okuyamıyoruz" farklı şeyler. İkincisini
    /// birincisi gibi göstermek, F4'ün karşılaştırmasını sessizce eksik kümeye
    /// indirger — ve eksikliğin sebebi hiçbir yerde görünmez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Okunamayan_surum_sessizce_bos_donmuyor()
    {
        var store = Store();

        Assert.Null(await store.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        // Gelecekteki bir sürümle yazılmış satır elle kuruluyor: `SaveAsync`
        // tanım gereği bugünkü sürümü yazıyor, dolayısıyla bu durum ancak
        // depoya dışarıdan bir satır konarak üretilebiliyor — ki gerçekte de
        // öyle olacak, sürümü artmış bir kopya aynı veritabanına yazdığında.
        var future = Bundle();

        await using (var db = _factory.CreateDbContext())
        {
            db.EvidenceBundles.Add(new EvidenceBundleEntity
            {
                Id = future.Id,
                GatheredAt = future.GatheredAt,
                SchemaVersion = EvidenceBundle.CurrentSchemaVersion + 1,
                ContentHash = future.ContentHash,
                WindowFrom = future.Window.From,
                WindowTo = future.Window.To,
                BaselineFrom = future.Window.BaselineFrom,
                BaselineTo = future.Window.BaselineTo,
                Payload = BundleSerializer.Serialize(future),
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(future.Id, TestContext.Current.CancellationToken));

        Assert.Contains("sürüm", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Liste JSON <b>açmadan</b> okunuyor — liste ekranı (T37) her satır için
    /// belgeyi çözmek zorunda kalmamalı. Üst verinin kolon olarak durmasının
    /// tamamı bu.
    /// </summary>
    [Fact]
    public async Task Liste_belge_acmadan_okunuyor()
    {
        var store = Store();

        await store.SaveAsync(Bundle(), TestContext.Current.CancellationToken);
        await store.SaveAsync(
            Bundle() with { Id = Guid.NewGuid(), GatheredAt = Bundle().GatheredAt.AddHours(1) },
            TestContext.Current.CancellationToken);

        var rows = await store.ListRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].GatheredAt > rows[1].GatheredAt, "Liste en yeniden eskiye sıralı değil.");
        Assert.All(rows, row => Assert.Equal(342, row.OutOfScopeCount));
        Assert.All(rows, row => Assert.Equal(EvidenceBundle.CurrentSchemaVersion, row.SchemaVersion));
    }

    /// <summary>
    /// Aynı pencerenin iki kez toplanması <b>meşru</b> ve ikisi de saklanıyor:
    /// F4'ün karşılaştırma akışı tam olarak bunu yapıyor. <c>content_hash</c>
    /// tekil olsaydı ikinci koşu reddedilirdi.
    /// </summary>
    [Fact]
    public async Task Ayni_hash_iki_kez_saklanabiliyor()
    {
        var store = Store();
        var first = Bundle();
        var second = first with { Id = Guid.NewGuid() };

        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        await store.SaveAsync(second, TestContext.Current.CancellationToken);

        var rows = await store.ListRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.ContentHash).Distinct(StringComparer.Ordinal));
    }
}

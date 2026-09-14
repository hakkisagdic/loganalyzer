using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>F5 · S1 — envanterin topoloji öznitelikleri, gerçek Postgres'e karşı.</b>
///
/// <para>
/// <b>YAZILDI, KOŞTURULMADI</b> (CLAUDE.md §2 — konteyner gerektiriyor).
/// Koşturulduğunda kanıtlayacağı üç şey, üçü de birim testlerinin
/// <b>göremediği</b> sınıftan:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Göç gerçekten uygulanıyor ve eski satırları bozmuyor.</b> Bu deponun en
/// pahalı <i>önlenmiş</i> hatası bir göçtü (<c>enabled → status</c> her pasif
/// kuralı sessizce açıyordu). Buradaki göç saf ekleme — üç <c>AddColumn</c>,
/// <c>defaultValue: ""</c>, hiçbir <c>UPDATE</c> yok — ve testin işi bunun
/// <b>iddia</b> değil <b>ölçüm</b> olması.
/// </item>
/// <item>
/// <b>Doldurulmamış alan boş dizgi dönüyor, <c>null</c> değil.</b> Ayrım
/// sinyalin tamamı: <c>TopologyProvider</c> boşluğu <i>"bilinmiyor"</i> diye
/// okuyup <see cref="EvidenceStatus.NeverFed"/> üretiyor. Kolon nullable
/// gelseydi sağlayıcı <c>NullReferenceException</c> ile
/// <see cref="EvidenceStatus.Failed"/>'a düşerdi — yani "envanter boş" cevabı
/// "sağlayıcı patladı" diye raporlanırdı.
/// </item>
/// <item>
/// <b>Üç alan kapsam kapısından <u>karışmadan</u> geçiyor.</b> En sinsi risk
/// bu: <see cref="SourceSummary"/> konumsal bir record ve üç alan da
/// <c>string</c>. <c>vlan</c> ile <c>firmware</c> yer değiştirseydi derleme
/// temiz, birim testleri yeşil kalırdı — rapor yalnızca yanlış alan adıyla
/// doğru değeri gösterirdi. Test bu yüzden <b>ayırt edilebilir</b> değerler
/// kullanıyor.
/// </item>
/// </list>
///
/// <para>
/// Kapsam filtresinin kendisi burada yeniden sınanmıyor —
/// <see cref="ScopeNegativeTests"/> onu zaten kuruyor. Buradaki tek kapsam
/// iddiası, yeni alanların o filtreyi <b>atlatan</b> bir yol açmadığı.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class SourceTopologyAttributeTests(DevStackFixture stack) : IAsyncLifetime
{
    private ClickHouseContext _context = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private IScopedQuery _query = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        await new ClickHouseMigrator(_context).MigrateAsync(
            RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // Göç BURADA uygulanıyor — testin birinci iddiası bu satırın
        // patlamaması.
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Sources.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        db.Sources.AddRange(
            new SourceEntity
            {
                SourceId = "sw-dolu",
                OwnerGroup = "net-core",

                // Üçü de birbirinden AYIRT EDİLEBİLİR: yer değiştirmiş bir
                // eşleme ancak böyle görünür hâle geliyor.
                Upstream = "core-01",
                Vlan = "mgmt",
                Firmware = "7.4.1",
            },
            new SourceEntity
            {
                SourceId = "sw-bos",
                OwnerGroup = "net-core",
            });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _query = new ScopedQuery(
            new EventReader(_context),
            new ChangeEventReader(_context),
            new CorrelationReader(_context),
            new EventWriter(_context),
            await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken),
            new NoOpAuditSink());
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Topoloji_alanlari_kapsam_kapisindan_karismadan_geciyor()
    {
        var sources = await _query.SearchSourcesAsync(
            AccessScope.ForGroups("test", ["net-core"]), TestContext.Current.CancellationToken);

        var filled = Assert.Single(sources, s => s.SourceId == "sw-dolu");

        Assert.Equal("core-01", filled.Upstream);
        Assert.Equal("mgmt", filled.Vlan);
        Assert.Equal("7.4.1", filled.Firmware);
    }

    [Fact]
    public async Task Doldurulmamis_alan_bos_dizgi_donuyor_null_degil()
    {
        var sources = await _query.SearchSourcesAsync(
            AccessScope.ForGroups("test", ["net-core"]), TestContext.Current.CancellationToken);

        var empty = Assert.Single(sources, s => s.SourceId == "sw-bos");

        // `Assert.Empty` değil `Assert.Equal(string.Empty, …)`: null da "boş"
        // sayılabilecek bir iddia bırakmak, tam olarak ölçülmek istenen şeyi
        // ölçmemek olurdu.
        Assert.Equal(string.Empty, empty.Upstream);
        Assert.Equal(string.Empty, empty.Vlan);
        Assert.Equal(string.Empty, empty.Firmware);
    }

    [Fact]
    public async Task Yeni_alanlar_kapsam_filtresini_atlatmiyor()
    {
        // Başka bir grubun kapsamı: envanterde satır var ama bu kapsam onu
        // görmemeli. Yeni kolonların projeksiyona girmesi filtreyi
        // etkilememeli — etkilerse "cihaz listesi sızdı" demek.
        var sources = await _query.SearchSourcesAsync(
            AccessScope.ForGroups("test", ["net-edge"]), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sources, s => s.SourceId == "sw-dolu");
        Assert.DoesNotContain(sources, s => s.SourceId == "sw-bos");
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? ".", relative);
    }
}

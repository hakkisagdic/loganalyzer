using System.Text.Json;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T32 · Kapı 1'in dayandığı kolon kümesi gerçekten canlı görünümün kolonları mı.
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlıyor:</b> <c>detections/schema/view-columns.json</c>
/// dosyasındaki liste — ki <c>db/clickhouse/*.sql</c> göçlerinden metin olarak
/// türetiliyor — göçler gerçekten uygulandıktan sonra ClickHouse'un bildirdiği
/// kolon listesiyle <b>aynı sırada ve aynı adlarla</b> örtüşüyor.
/// </para>
///
/// <para>
/// Neden gerekli: Kapı 1 bir Sigma kuralının ürettiği SQL'i "bu kolon görünümde
/// var mı" diye o listeye karşı sınıyor. Liste ile gerçek ayrışırsa kapı sessizce
/// yanlış cevap verir, ve sürüklenmenin tehlikeli yönü sessiz olan: görünümden
/// çıkmış bir kolon listede kalırsa o kolona giden kural kapıdan <b>geçer</b> ve
/// ancak çalışma zamanında kırılır — hata yok, sayaç yok, belirti yok.
/// </para>
///
/// <para>
/// Birim testleri (<c>tools/sigma-build/tests/test_view_columns.py</c>) çıkarıcının
/// <i>metni</i> doğru okuduğunu kanıtlıyor. Kanıtlayamadıkları şey, o okumanın
/// ClickHouse'un aynı SQL'den anladığı şeyle örtüştüğü. Bu test tam olarak o
/// boşluğu kapatıyor; ClickHouse gerektirdiği için koordinatörde koşuyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class SigmaViewColumnsTests(DevStackFixture stack) : IAsyncLifetime
{
    private ClickHouseContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(
            RepoPath("db/clickhouse"),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    // Bu proje boyunca on test sınıfında tekrarlanan yardımcı. Ortak bir yere
    // taşımak on dosyaya dokunmak demek; bu turda başka ajanlar da bu projede
    // çalışıyor ve birleştirme maliyeti kazancından büyük (§5).
    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (Bizigo.sln).")
            : Path.Combine(dir.FullName, relative);
    }

    /// <summary>
    /// Canlı görünümün kolon adları, bildirim sırasıyla.
    /// <c>system.columns</c> satır sırasını garanti etmediği için <c>position</c>
    /// ile açıkça sıralanıyor — sıra karşılaştırmanın parçası.
    /// </summary>
    private async Task<string[]> LiveColumnsAsync(string view)
    {
        var joined = await stack.QueryScalarAsync(
            _context.Options.ConnectionString,
            $"""
             SELECT arrayStringConcat(
                        arrayMap(t -> t.2, arraySort(t -> t.1, groupArray((position, name)))),
                        ',')
             FROM system.columns
             WHERE database = currentDatabase() AND table = '{view}'
             """,
            TestContext.Current.CancellationToken);

        return joined.Length == 0 ? [] : joined.Split(',');
    }

    private static IReadOnlyDictionary<string, string[]> DerivedColumns()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPath("detections/schema/view-columns.json")));

        return document.RootElement
            .GetProperty("views")
            .EnumerateObject()
            .ToDictionary(
                view => view.Name,
                view => view.Value.GetProperty("columns")
                    .EnumerateArray()
                    .Select(column => column.GetString()!)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("events_ocsf")]
    [InlineData("events_otel")]
    [Trait("Category", "Integration")]
    public async Task Turetilen_kolon_kumesi_canli_gorunumle_birebir_ayni(string view)
    {
        var derived = DerivedColumns();
        Assert.True(
            derived.ContainsKey(view),
            $"`{view}` türetilmiş anlık görüntüde yok. " +
            "tools/sigma-build içinde `python -m sigma_build.view_columns --write` çalıştırın.");

        var live = await LiveColumnsAsync(view);

        Assert.NotEmpty(live);
        Assert.Equal(derived[view], live);
    }

    /// <summary>
    /// Anlık görüntüde <b>fazladan</b> bir görünüm de olmamalı: göç bir görünümü
    /// <c>DROP</c> ettiyse çıkarıcı da onu düşürmüş olmalı. Bu yön ayrı test
    /// ediliyor çünkü yukarıdaki teori yalnızca adı verilen görünümlere bakıyor
    /// ve kaybolmuş bir görünümü hiç sormazdı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Anlik_goruntu_var_olmayan_gorunum_uydurmuyor()
    {
        foreach (var view in DerivedColumns().Keys)
        {
            var live = await LiveColumnsAsync(view);
            Assert.True(
                live.Length > 0,
                $"Anlık görüntü `{view}` görünümünü sayıyor ama göçler uygulandıktan sonra ClickHouse'ta yok.");
        }
    }

    /// <summary>
    /// T30'un beşinci tuzağı, kaynağında ölçülüyor: <c>type_uid</c> canlı
    /// görünümde <b>yok</b>. T31'in <c>ocsf_pipeline</c>'ı zincire koymama kararı
    /// buna dayanıyor — o pipeline sınıf ayırıcısını <c>type_uid</c> üzerinden
    /// ekliyor ve K8 gereği kolona yazılan tek OCSF alanı <c>class_uid</c> +
    /// <c>activity_id</c>.
    ///
    /// <para>
    /// Birim testinde de duruyor ama orada <i>metinden</i> okunuyor; burada
    /// ClickHouse'un kendisine soruluyor. Kararın dayanağı bu ikincisi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Events_ocsf_type_uid_kolonu_icermiyor()
    {
        var live = await LiveColumnsAsync("events_ocsf");

        Assert.DoesNotContain("type_uid", live);
        Assert.Contains("class_uid", live);
        Assert.Contains("activity_id", live);
    }
}

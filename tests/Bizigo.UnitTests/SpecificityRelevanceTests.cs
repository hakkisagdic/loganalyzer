using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>specificity</c> bugün <b>hiçbir satırın sonucunu belirlemiyor</b> — ve bu
/// test o durumu ölçüp sabitliyor (T39).
///
/// <para>
/// Ölçüt yazılmamıştı: şema <c>specificity</c>'yi kabul ediyor, katalog
/// dolduruyor, dispatcher sıralıyor, ama bir sayının neden o sayı olduğu hiçbir
/// yerde yazmıyordu. Ölçüt yazmaya çalışmadan önce sorulacak soru şuydu:
/// <b>sıralama gerçekten bir şey belirliyor mu?</b>
/// </para>
///
/// <para>
/// Cevap bugün <b>hayır</b>. Dispatcher "ilk <c>ok</c> kazanır" diyor;
/// dolayısıyla sıralama ancak <b>birden çok aday <c>ok</c> döndüğünde</b> sonucu
/// değiştirir. Altın örneklerin hiçbirinde bu olmuyor — her satır için en fazla
/// bir parser <c>ok</c> dönüyor, yani hangi sırayla denendikleri sonucu
/// değiştirmiyor.
/// </para>
///
/// <para>
/// <b>Bu testin işi ölçütü yazmak değil, ölçütün gerekli hâle geldiği ANI
/// yakalamak.</b> Kırmızı yandığı gün iki aday aynı satırı sahipleniyor demektir
/// ve o gün "hangisi kazanmalı" gerçek bir soru olur — bugün olmadığı için
/// cevabı uydurmak, ölçülmemiş bir gerekçeyi kayda geçirmek olurdu.
/// </para>
/// </summary>
public sealed class SpecificityRelevanceTests
{
    private static CatalogSnapshot Catalog()
    {
        var compiler = new ParserCompiler(
            new GrokCompiler(GrokPatternLibrary.LoadWithOverlay(
                Path.Combine(RepositoryLayout.Root, "catalog", "patterns", "legacy"),
                Path.Combine(RepositoryLayout.Root, "catalog", "patterns", "bizigo-v1"))),
            MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings")));

        var catalog = new ParserCatalog();
        var report = catalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "parsers"), compiler);

        Assert.Empty(report.Errors);

        return catalog.Current;
    }

    /// <summary>Altın örneklerin tamamı — kapsam ölçümüyle aynı küme.</summary>
    private static string[] GoldenLines() =>
        [.. Directory
            .EnumerateFiles(
                Path.Combine(RepositoryLayout.Root, "catalog", "parsers"), "*.log", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(static line => line.Trim().Length > 0 && !line.StartsWith('#'))];

    /// <summary>
    /// Literal ön filtreden geçen adaylar — <c>Dispatcher</c>'ın kademe 2'de
    /// yaptığı hesabın aynısı.
    /// </summary>
    private static CompiledParser[] Candidates(CatalogSnapshot snapshot, string line)
    {
        var matched = snapshot.Automaton.Match(line);

        foreach (var index in snapshot.LiteralFree)
        {
            matched.Add(index);
        }

        return [.. matched.Order().Select(index => snapshot.Parsers[index])];
    }

    /// <summary>
    /// <b>Asıl bekçi.</b> Hiçbir altın örnek satırında birden fazla aday
    /// <c>ok</c> dönmüyor.
    ///
    /// <para>
    /// Dönseydi "ilk <c>ok</c> kazanır" kuralı sıralamaya bağlı hâle gelirdi ve
    /// <c>specificity</c>'nin değeri o satırın hangi parser'a düştüğünü
    /// belirlerdi — ölçütün yazılmamış olması o gün gerçek bir boşluk olur.
    /// </para>
    /// </summary>
    [Fact]
    public void Hicbir_altin_ornekte_birden_fazla_aday_ok_donmuyor()
    {
        var snapshot = Catalog();
        var contested = new List<string>();

        foreach (var line in GoldenLines())
        {
            var winners = Candidates(snapshot, line)
                .Where(parser => parser.Parse(line).Status == ParseStatus.Ok)
                .Select(static parser => parser.Id)
                .ToArray();

            if (winners.Length > 1)
            {
                contested.Add($"{string.Join(" + ", winners)} ← {line[..Math.Min(80, line.Length)]}");
            }
        }

        Assert.True(
            contested.Count == 0,
            "Birden fazla aday aynı satırı `ok` ayrıştırıyor; artık `specificity` sonucu BELİRLİYOR ve "
            + "seçim ölçütünün yazılması gerekiyor (T39):\n  " + string.Join("\n  ", contested));
    }

    /// <summary>
    /// Vendor'lar arası <c>specificity</c> karşılaştırması <b>hiç koşmuyor</b>:
    /// literal ön filtre vendor'ları zaten ayırıyor.
    ///
    /// <para>
    /// Bu, T39'un sorularından birini kapatan ölçüm. `cisco.asa.auth` 95,
    /// `fortinet.fortigate.event` 90 — aralarındaki fark hiçbir satırda
    /// karşılaştırılmıyor, dolayısıyla o iki sayının birbirine göre
    /// gerekçelendirilmesi gereksiz. Ölçüt <b>vendor içi</b>dir; dışı tanımsız
    /// ve tanımsız kalması bir eksiklik değil.
    /// </para>
    /// </summary>
    [Fact]
    public void Vendorlar_arasi_siralama_hic_kosmuyor()
    {
        var snapshot = Catalog();
        var crossVendor = new List<string>();

        foreach (var line in GoldenLines())
        {
            var vendors = Candidates(snapshot, line)
                .Select(static parser => parser.Id.Split('.')[0])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (vendors.Length > 1)
            {
                crossVendor.Add($"{string.Join(", ", vendors)} ← {line[..Math.Min(80, line.Length)]}");
            }
        }

        Assert.True(
            crossVendor.Count == 0,
            "Literal ön filtre iki vendor'ın parser'ını aynı satıra aday yaptı; vendor'lar arası "
            + "`specificity` karşılaştırması artık koşuyor ve tanımsız olması bir eksiklik hâline geldi:\n  "
            + string.Join("\n  ", crossVendor));
    }

    /// <summary>
    /// Ölçümün <b>boş geçmediğinin</b> kanıtı: ön filtre gerçekten birden çok
    /// aday üretiyor.
    ///
    /// <para>
    /// Her satır tek adaya düşseydi yukarıdaki iki iddia her durumda doğru olur
    /// ve hiçbir şey sınamazlardı — <c>GrokPropertyTests</c>'in kendi
    /// <c>backtracking &gt; 0</c> bekçisiyle aynı sebep.
    /// </para>
    /// </summary>
    [Fact]
    public void Olcum_bos_gecmiyor_birden_cok_adayli_satirlar_var()
    {
        var snapshot = Catalog();

        var contestedLines = GoldenLines().Count(line => Candidates(snapshot, line).Length > 1);

        Assert.True(
            contestedLines > 0,
            "Hiçbir satır birden fazla aday üretmedi; sıralama iddiaları boş geçiyor.");
    }
}

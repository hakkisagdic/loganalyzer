using Bizigo.Cli.Fields;
using Bizigo.Parsing.Engine;

namespace Bizigo.UnitTests;

/// <summary>
/// Değer uzayı ölçümünün bekçileri (T39).
///
/// <para>
/// Ölçtüğü şey <b>veri değil şema</b>: bir eşleme tablosu, beslediği kolonun
/// değer uzayını daraltıyor ve daralttığı an o kolona başka bir değer arayan
/// kural <b>hiçbir zaman</b> eşleşemiyor. Örneklemde bir değerin bulunmaması
/// "bugün yok" demek; şemanın onu üretememesi "hiçbir zaman olmayacak". İkisi
/// Kapı 3'ün tablosunda aynı görünüyor ve verdikleri iş emri zıt.
/// </para>
/// </summary>
public sealed class ColumnValueSpaceTests
{
    private static ColumnValueSpace Closed(params string[] values) =>
        new("status", "outcome", ValueSpaceKind.Closed, values, ["test"], []);

    private static ColumnValueSpace Open(params string[] missingIn) =>
        new("activity_name", "action", ValueSpaceKind.Open, [], ["test"], missingIn);

    /// <summary>
    /// <b>Asıl vaka.</b> <c>status</c> kolonu <c>http_status_outcome</c>'dan
    /// besleniyor ve tablo HTTP kodunu <c>success</c>/<c>failure</c>'a
    /// çeviriyor. Kolonda hiçbir zaman bir sayı durmuyor, yani
    /// <c>status|startswith: '5'</c> örneklem düzelse de eşleşemez.
    /// </summary>
    [Fact]
    public void Kapali_uzayin_uretemedigi_deger_erisilemez()
    {
        Assert.False(ColumnValueSpaces.CanSatisfy(Closed("success", "failure"), "startswith", "5"));
        Assert.True(ColumnValueSpaces.CanSatisfy(Closed("success", "failure"), "startswith", "fail"));
        Assert.True(ColumnValueSpaces.CanSatisfy(Closed("success", "failure"), "", "FAILURE"));
    }

    /// <summary>
    /// Açık uzay hakkında bir şey söylenmiyor ve <b>söylememek doğru</b>: kolon
    /// cihazın yazdığını taşıyor.
    /// </summary>
    [Fact]
    public void Acik_uzay_hakkinda_hukum_verilmiyor()
    {
        Assert.True(ColumnValueSpaces.CanSatisfy(Open(), "", "her ne olursa"));
    }

    /// <summary>
    /// <b>Modellenmemiş operatör "erişilemez" demiyor.</b> Aksi hâlde araç
    /// kendi eksikliğini ürünün kusuru gibi gösterirdi — bu turda iki kez
    /// ödenen dersin aynısı.
    /// </summary>
    [Fact]
    public void Bilinmeyen_operator_erisilemez_saymiyor()
    {
        Assert.True(ColumnValueSpaces.CanSatisfy(Closed("success"), "re", "^5"));
        Assert.True(ColumnValueSpaces.CanSatisfy(Closed("success"), "gte", "500"));
    }

    /// <summary>Kolonu hiçbir parser doldurmuyorsa hiçbir değer üretilemez.</summary>
    [Fact]
    public void Doldurulmayan_kolon_hicbir_degeri_uretemiyor()
    {
        var absent = new ColumnValueSpace("status", "outcome", ValueSpaceKind.Absent, [], [], []);
        Assert.False(ColumnValueSpaces.CanSatisfy(absent, "", "success"));
    }

    /// <summary>
    /// Gerçek katalog: <c>status</c> dört vendor'da da <b>kapalı</b> ve yalnızca
    /// <c>success</c>/<c>failure</c> taşıyor. Bu, ölçümün varlık sebebi olan
    /// vakanın kaynaktan doğrulanması.
    /// </summary>
    [Fact]
    public void Gercek_katalogda_status_kapali_ve_iki_degerli()
    {
        var spaces = ColumnValueSpaces.Build(
            RepositoryLayout.CatalogParserDirectory,
            MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings")),
            OcsfViewSchema.Read(Path.Combine(RepositoryLayout.Root, "db", "clickhouse")));

        Assert.Equal(4, spaces.Count);

        foreach (var space in spaces)
        {
            var status = space.Columns["status"];

            Assert.Equal(ValueSpaceKind.Closed, status.Kind);
            Assert.Equal(["failure", "success"], status.Values);

            // Ve bir HTTP kodu asla oraya inmiyor.
            Assert.False(ColumnValueSpaces.CanSatisfy(status, "startswith", "5"));
        }
    }

    /// <summary>
    /// <c>Outputs</c> bilinmeyen tabloda boş dizi dönmüyor, <b>atıyor</b>: boş
    /// dizi "değer uzayı yok" ile "tablo yok"u aynı şeye indirir ve ikincisi
    /// sessizce "hiçbir değer üretilemiyor" diye okunurdu.
    /// </summary>
    [Fact]
    public void Bilinmeyen_tablo_bos_donmuyor_atiyor()
    {
        var tables = MappingTableCatalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

        Assert.Throws<KeyNotFoundException>(() => tables.Outputs("boyle-bir-tablo-yok"));
        Assert.Contains("failure", tables.Outputs("auth_outcome"));
    }
}

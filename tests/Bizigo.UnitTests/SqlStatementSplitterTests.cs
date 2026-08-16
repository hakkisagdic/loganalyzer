using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

/// <summary>
/// Göç dosyalarını ifadelere bölen tarayıcı. Container gerektirmez.
/// Buradaki hata "göçün yarısı uygulandı" gibi teşhisi zor bir arızaya dönüşür.
/// </summary>
public sealed class SqlStatementSplitterTests
{
    [Fact]
    public void Bos_girdi_bos_liste_dondurur()
    {
        Assert.Empty(SqlStatementSplitter.Split(string.Empty));
        Assert.Empty(SqlStatementSplitter.Split("   \n\t  "));
        Assert.Empty(SqlStatementSplitter.Split(";;;"));
    }

    [Fact]
    public void Ifadeleri_noktali_virgulden_ayirir()
    {
        var result = SqlStatementSplitter.Split("SELECT 1; SELECT 2;");

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 1", result[0]);
        Assert.Equal("SELECT 2", result[1]);
    }

    [Fact]
    public void Son_noktali_virgul_zorunlu_degil()
    {
        var result = SqlStatementSplitter.Split("SELECT 1;\nSELECT 2");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Metin_icindeki_noktali_virgul_ayrac_sayilmaz()
    {
        var result = SqlStatementSplitter.Split("INSERT INTO t VALUES ('a;b'); SELECT 1;");

        Assert.Equal(2, result.Count);
        Assert.Contains("'a;b'", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Kacirilmis_tirnak_metni_bitirmez()
    {
        // '' → tek tırnak karakteri; metin devam ediyor.
        var result = SqlStatementSplitter.Split("SELECT 'o''nun; degeri'; SELECT 2;");

        Assert.Equal(2, result.Count);
        Assert.Contains("o''nun; degeri", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Ters_bolu_ile_kacirilmis_tirnak_metni_bitirmez()
    {
        var result = SqlStatementSplitter.Split(@"SELECT 'a\'; b'; SELECT 2;");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Satir_yorumundaki_noktali_virgul_ayrac_sayilmaz()
    {
        var result = SqlStatementSplitter.Split("""
            -- burada bir ; var ama yorum içinde
            SELECT 1;
            """);

        Assert.Single(result);
        Assert.Equal("SELECT 1", result[0]);
    }

    [Fact]
    public void Blok_yorumundaki_noktali_virgul_ayrac_sayilmaz()
    {
        var result = SqlStatementSplitter.Split("/* a; b */ SELECT 1; SELECT 2;");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Ters_tirnakli_tanimlayici_korunur()
    {
        var result = SqlStatementSplitter.Split("SELECT `garip;kolon` FROM t;");

        Assert.Single(result);
        Assert.Contains("`garip;kolon`", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Gercekci_bir_clickhouse_ddl_tek_ifade_kalir()
    {
        const string ddl = """
            -- events tablosu (T02'de gelecek)
            CREATE TABLE events
            (
                ts          DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
                owner_group LowCardinality(String),
                body        String,
                INDEX idx_body body TYPE text(tokenizer = 'sparseGrams') GRANULARITY 1
            )
            ENGINE = MergeTree
            PARTITION BY toYYYYMMDD(ts)
            ORDER BY (owner_group, ts);
            """;

        var result = SqlStatementSplitter.Split(ddl);

        Assert.Single(result);
        Assert.Contains("sparseGrams", result[0], StringComparison.Ordinal);
        Assert.StartsWith("CREATE TABLE", result[0], StringComparison.Ordinal);
    }
}

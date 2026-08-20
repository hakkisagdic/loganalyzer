using Bizigo.Api;
using Bizigo.Contracts;

namespace Bizigo.UnitTests;

/// <summary>
/// T17 kabul kriteri: <b>CSV yüklemesinde tek satır bile kapsam dışıysa hiçbiri
/// yazılmıyor ve kullanıcı hangi satırın reddedildiğini görüyor.</b>
///
/// <para>
/// Yarı yüklenmiş bir envanter, hangi cihazın hangi gruba düştüğünü belirsiz
/// bırakır ve o belirsizlik doğrudan bir kapsam hatasıdır — üstelik fark
/// edilmesi için kimsenin bakmadığı bir yere bakmak gerekir. Bu yüzden kural
/// veritabanından bağımsız bir fonksiyonda ve konteyner gerektirmeden
/// sınanabiliyor.
/// </para>
/// </summary>
public sealed class SourceCsvImportTests
{
    private const string Header = "source_id,owner_group,peer_address,hostname,vendor,product,parser_id,encoding,source_class";

    private static AccessScope CoreOnly() => AccessScope.ForGroups("u-core", ["network/core"]);

    private static string Csv(params string[] rows) => string.Join('\n', [Header, .. rows]);

    [Fact]
    public void Gecerli_satirlar_ayristiriliyor()
    {
        var result = SourceCsvImport.Parse(
            Csv(
                "fg-1,network/core,10.0.0.1,fw-01,fortinet,fortigate,fortinet.traffic,windows-1254,firewall",
                "fg-2,network/core,10.0.0.2,fw-02,cisco,asa,,,"),
            CoreOnly());

        Assert.True(result.Ok);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("fg-1", result.Rows[0].SourceId);
        Assert.Equal("windows-1254", result.Rows[0].Encoding);

        // Boş bırakılan sütunlar varsayılana düşüyor; "" bir kodlama adı değil.
        Assert.Equal("auto", result.Rows[1].Encoding);
        Assert.Equal("default", result.Rows[1].SourceClass);
    }

    [Fact]
    public void Kapsam_disi_tek_satir_butun_dosyayi_reddediyor()
    {
        var result = SourceCsvImport.Parse(
            Csv(
                "fg-1,network/core,,,,,,,",
                "fg-2,network/edge,,,,,,,",
                "fg-3,network/core,,,,,,,"),
            CoreOnly());

        Assert.False(result.Ok);

        // Ya hep ya hiç: geçerli iki satır da yazılmıyor.
        Assert.Empty(result.Rows);

        // Ve kullanıcı hangi satırın neden reddedildiğini görüyor — "geçersiz
        // CSV" tek başına dosyayı düzeltmeye yetmiyor.
        var error = Assert.Single(result.Errors);
        Assert.Contains("satır 3", error, StringComparison.Ordinal);
        Assert.Contains("network/edge", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Sinirsiz_kapsamda_her_grup_kabul_ediliyor()
    {
        // Bugünkü davranış: uç yalnızca `admin` rolüne açık ve admin sınırsız.
        // Kontrol yine de var, çünkü rol tablosu bir gün grup yöneticisi tanırsa
        // yokluğu sessiz bir kapsam deliği olurdu.
        var result = SourceCsvImport.Parse(
            Csv("fg-1,network/edge,,,,,,,"),
            AccessScope.System("admin"));

        Assert.True(result.Ok);
        Assert.Equal("network/edge", result.Rows[0].OwnerGroup);
    }

    [Fact]
    public void Bos_kapsam_hicbir_satiri_kabul_etmiyor()
    {
        // Boş kapsam "her şey" değil "hiçbir şey".
        var result = SourceCsvImport.Parse(
            Csv("fg-1,network/core,,,,,,,"),
            AccessScope.ForGroups("u-yok", []));

        Assert.False(result.Ok);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Sutun_sayisi_tutmayan_satir_numarasiyla_bildiriliyor()
    {
        var result = SourceCsvImport.Parse(Csv("fg-1,network/core"), CoreOnly());

        Assert.False(result.Ok);
        Assert.Contains("satır 2", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_kaynak_iki_kez_gecerse_reddediliyor()
    {
        // Son satırın sessizce kazanması, kullanıcının dosyaya bakarak hangi
        // grubun geçerli olduğunu anlayamaması demekti — ve grup, kapsamın
        // kendisi.
        var result = SourceCsvImport.Parse(
            Csv("fg-1,network/core,,,,,,,", "fg-1,network/core,,,,,,,"),
            CoreOnly());

        Assert.False(result.Ok);
        var error = Assert.Single(result.Errors);
        Assert.Contains("zaten 2. satırda", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Zorunlu_sutun_eksikse_dosya_hic_okunmuyor()
    {
        var result = SourceCsvImport.Parse("source_id,vendor\nfg-1,fortinet", CoreOnly());

        Assert.False(result.Ok);
        Assert.Contains("owner_group", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Yorum_satirlari_ve_bos_satirlar_atlaniyor()
    {
        var result = SourceCsvImport.Parse(
            $"# envanter dışa aktarımı\n{Header}\n\nfg-1,network/core,,,,,,,\n",
            CoreOnly());

        Assert.True(result.Ok);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Yalnizca_baslik_iceren_dosya_reddediliyor()
    {
        // Boş bir dosyayı "0 satır yazıldı" diye kabul etmek, kullanıcının yanlış
        // dosyayı yüklediğini fark etmemesi demek.
        var result = SourceCsvImport.Parse(Header, CoreOnly());

        Assert.False(result.Ok);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Bos_source_id_veya_owner_group_reddediliyor()
    {
        var result = SourceCsvImport.Parse(
            Csv("fg-1,network/core,,,,,,,", ",network/core,,,,,,,"),
            CoreOnly());

        Assert.False(result.Ok);
        Assert.Contains("satır 3", Assert.Single(result.Errors), StringComparison.Ordinal);
    }
}

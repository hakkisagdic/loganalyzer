using System.Text;
using Bizigo.Ingest.Text;

namespace Bizigo.UnitTests;

/// <summary>
/// K4 / F1 §2.4. Bu testlerin çoğu Türkçe karakterler üzerinden yazıldı çünkü
/// tuzakların hepsi orada: <c>ı/İ</c>, <c>ğ</c>, ve windows-1254'ün UTF-8'e
/// benzemesi.
/// </summary>
public sealed class EncodingDetectorTests
{
    private readonly EncodingDetector _detector = new();

    static EncodingDetectorTests() => EncodingDetector.RegisterCodePages();

    [Fact]
    public void Utf8_gecerli_metin_utf8_olarak_cozuluyor()
    {
        var bytes = Encoding.UTF8.GetBytes("bağlantı düştü");

        var result = _detector.Decode(bytes);

        Assert.Equal("utf-8", result.Name);
        Assert.Equal("bağlantı düştü", result.Body);
    }

    [Fact]
    public void Windows1254_bildirilmisse_dogru_cozuluyor()
    {
        var source = "arayüz kapandı: ıIiİ";
        var bytes = Encoding.GetEncoding("windows-1254").GetBytes(source);

        var result = _detector.Decode(bytes, declared: "windows-1254");

        Assert.Equal("windows-1254", result.Name);
        Assert.Equal(source, result.Body);
        Assert.True(result.WasDeclaredHonored);
    }

    [Fact]
    public void Bildirilmemis_windows1254_yedek_kod_sayfasiyla_cozuluyor()
    {
        // 0xFE = 'þ' latin1'de, windows-1254'te 'ş'. Tek başına geçersiz UTF-8.
        var source = "işlem başarısız";
        var bytes = Encoding.GetEncoding("windows-1254").GetBytes(source);

        var result = _detector.Decode(bytes, declared: null, sourceFallback: "windows-1254");

        Assert.Equal("windows-1254", result.Name);
        Assert.Equal(source, result.Body);
    }

    [Fact]
    public void Yanlis_bildirim_utf8_dogrulamasina_dusuyor_ve_isaretleniyor()
    {
        var bytes = Encoding.UTF8.GetBytes("bağlantı düştü");

        // Kaynak "iso-8859-9" diyor ama baytlar UTF-8. iso-8859-9 her baytı kabul
        // ettiği için sessizce mojibake üretirdi; sıralamanın önemi burada.
        var result = _detector.Decode(bytes, declared: "yanlış-kodlama-adı");

        Assert.Equal("utf-8", result.Name);
        Assert.Equal("bağlantı düştü", result.Body);
        Assert.False(result.WasDeclaredHonored);
    }

    [Fact]
    public void Cozulemeyen_baytlar_latin1_ile_kayipsiz_geri_donuyor()
    {
        // 0xC3 tek başına: geçersiz UTF-8 devamı; hiçbir aday çözemez.
        byte[] bytes = [0x41, 0xC3, 0x28, 0xFF];

        var result = _detector.Decode(bytes, declared: null, sourceFallback: null);

        Assert.Equal("iso-8859-1", result.Name);
        Assert.Equal(4, result.Body.Length);

        // Kayıpsızlık şartı: latin1'e geri kodlandığında orijinal baytlar çıkmalı.
        Assert.Equal(bytes, Encoding.Latin1.GetBytes(result.Body));
    }

    [Fact]
    public void Utf8_BOM_atiliyor()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("veri")];

        var result = _detector.Decode(bytes);

        Assert.Equal("veri", result.Body);
    }

    [Fact]
    public void Utf16_BOM_taniniyor()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("ağ")).ToArray();

        var result = _detector.Decode(bytes);

        Assert.Equal("ağ", result.Body);
    }

    [Fact]
    public void Cikti_NFC_normalize_ediliyor()
    {
        // NFD hali: 'g' + U+0306 (birleştirici breve). Normalize edilmezse aynı
        // kelime iki farklı bayt dizilimiyle saklanır ve arama sessizce eşleşmez.
        const string Nfd = "ba\u0067\u0306lant\u0131";
        const string Nfc = "ba\u011Flant\u0131";

        var result = _detector.Decode(Encoding.UTF8.GetBytes(Nfd));

        Assert.True(result.Body.IsNormalized(NormalizationForm.FormC));
        Assert.Equal(Nfc, result.Body);
    }

    [Fact]
    public void Bos_govde_bos_metin_veriyor()
    {
        var result = _detector.Decode(ReadOnlySpan<byte>.Empty);

        Assert.Equal(string.Empty, result.Body);
    }

    [Fact]
    public void Arapca_ve_CJK_utf8_korunuyor()
    {
        const string Source = "الاتصال فشل — 连接失败";
        var bytes = Encoding.UTF8.GetBytes(Source);

        var result = _detector.Decode(bytes);

        Assert.Equal("utf-8", result.Name);
        Assert.Equal(Source, result.Body);
    }
}

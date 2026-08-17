using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Maskeleme sözlüğünün .NET tarafı (T12 / K14 "maskeleme sinerjisi").
///
/// <para>
/// <b>Bu dosyanın ikizi Python'da:</b> <c>sidecar/tests/test_masks.py</c> aynı
/// YAML'ı, aynı <c>golden</c> örnekleriyle koşuyor. İki motorun çıktısı
/// ayrışırsa .NET'in ürettiği imza Drain3'ün kümesine karşılık gelmez ve
/// <c>template_id</c> sessizce yanlış olur. Testin varlık sebebi tam olarak o
/// sessizliği bozmak.
/// </para>
/// </summary>
public sealed class MaskCatalogTests
{
    private static readonly MaskCatalog Catalog = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);

    public static TheoryData<string, string> GoldenSamples
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var sample in Catalog.Golden)
            {
                data.Add(sample.Input, sample.Masked);
            }

            return data;
        }
    }

    /// <summary>
    /// Zaman aşımına <b>hangi</b> maskelerin tabi olduğu yazılı — sınır ima değil,
    /// görünür olsun diye.
    ///
    /// <para>
    /// Geri izlemeye düşen maske <c>MatchTimeout</c> ödüyor, o da duvar saatini
    /// ölçüyor; yüklü makinede zaman aşımı <see cref="MaskCatalog.Signature"/>'ı
    /// <b>boş</b> döndürüyor ve olay sessizce etiketsiz kalıyor. Liste büyürse
    /// bu maruziyet de büyür, dolayısıyla büyümesi bilinçli bir karar olmalı.
    /// </para>
    ///
    /// <para>
    /// Dördü de lookaround taşıyor ve sınırları <c>.</c> karakterini kapsadığı
    /// için <c>\b</c> ile değiştirilemiyorlar (<c>\b</c> daha geçirgen olurdu).
    /// Ayrıca Python tarafıyla birebir aynı kalmak zorundalar.
    /// </para>
    /// </summary>
    [Fact]
    public void Zaman_asimina_tabi_maskeler_yazili()
    {
        Assert.Equal(
            ["IPV6", "IPV4", "BASE16NUM", "NUMBER"],
            Catalog.BacktrackingMasks);
    }

    [Fact]
    public void Sozluk_yukleniyor()
    {
        Assert.True(Catalog.Version >= 1);
        Assert.True(Catalog.Masks.Count >= 8, $"Yalnızca {Catalog.Masks.Count} maske yüklendi.");
        Assert.Equal("<", Catalog.MaskPrefix);
        Assert.Equal(">", Catalog.MaskSuffix);
        Assert.NotEmpty(Catalog.Golden);
    }

    [Theory]
    [MemberData(nameof(GoldenSamples))]
    public void Golden_ornekleri_Python_ile_ayni_ciktiyi_veriyor(string input, string expected)
    {
        Assert.Equal(expected, Catalog.Signature(input));
    }

    [Fact]
    public void Maske_adlari_grok_kutuphanesinde_karsiligi_olan_adlar()
    {
        // Köprünün tamamı bu: `<IPV4>` → `%{IPV4:...}`. Karşılığı olmayan bir ad
        // mined şablonu grok taslağına çevrilemez hale getirir (F4).
        var library = GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);
        var missing = Catalog.Names.Where(name => !library.TryGet(name, out _)).ToArray();

        Assert.True(
            missing.Length == 0,
            "Grok kütüphanesinde karşılığı olmayan maske adı: " + string.Join(", ", missing));
    }

    [Fact]
    public void Ayni_girdi_ayni_imzayi_veriyor()
    {
        const string Line = "denied 10.0.0.5 -> 10.0.0.9 port 443";

        Assert.Equal(Catalog.Signature(Line), Catalog.Signature(Line));
    }

    [Fact]
    public void Degisken_alanlar_ayni_imzada_bulusuyor()
    {
        // Önbelleğin işe yaramasının koşulu: farklı satırlar aynı imzaya düşmeli.
        Assert.Equal(
            Catalog.Signature("Failed password for admin from 10.1.2.3 port 51234 ssh2"),
            Catalog.Signature("Failed password for admin from 192.168.9.9 port 22 ssh2"));
    }

    [Fact]
    public void Token_ici_sayilar_maskelenmiyor()
    {
        Assert.Equal("link eth0 down on sda1 v2", Catalog.Signature("link eth0 down on sda1 v2"));
    }

    [Fact]
    public void Sablondaki_maske_adlari_cikarilabiliyor()
    {
        var names = Catalog.MaskNamesIn("connection from <IPV4> port <NUMBER> failed");

        Assert.Equal(["IPV4", "NUMBER"], names.Order(StringComparer.Ordinal));
    }
}

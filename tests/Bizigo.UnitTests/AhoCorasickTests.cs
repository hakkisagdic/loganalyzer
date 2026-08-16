using Bizigo.Parsing.Dispatch;

namespace Bizigo.UnitTests;

public sealed class AhoCorasickTests
{
    [Fact]
    public void Tek_literal_eslesiyor()
    {
        var automaton = AhoCorasick.Build([("devid=", 1)]);

        Assert.Equal([1], automaton.Match("date=2026-08-16 devid=FG100E"));
    }

    [Fact]
    public void Eslesmeyen_satir_bos_kume_veriyor()
    {
        var automaton = AhoCorasick.Build([("devid=", 1)]);

        Assert.Empty(automaton.Match("%ASA-6-302013: Built connection"));
    }

    [Fact]
    public void Ic_ice_literaller_ikisini_de_veriyor()
    {
        // "abc" eşleşirken "bc" de eşleşmeli — fail zinciri çıktıları birleştirmezse
        // daha kısa literalin sahibi sessizce aday olmaktan çıkardı.
        var automaton = AhoCorasick.Build([("abc", 1), ("bc", 2)]);

        Assert.Equal([1, 2], automaton.Match("xxabcxx").Order());
    }

    [Fact]
    public void Ayni_literal_birden_cok_sahibe_ait_olabiliyor()
    {
        var automaton = AhoCorasick.Build([("%ASA-", 1), ("%ASA-", 2)]);

        Assert.Equal([1, 2], automaton.Match("%ASA-6-302013").Order());
    }

    [Fact]
    public void Eslestirme_buyuk_kucuk_harf_duyarli()
    {
        // Duyarsız eşleştirme tr-TR kültüründe I/ı tuzağına açık olurdu; log
        // formatları zaten sabit.
        var automaton = AhoCorasick.Build([("devid=", 1)]);

        Assert.Empty(automaton.Match("DEVID=FG100E"));
    }

    [Fact]
    public void Cok_sayida_literal_tek_gecisle_taraniyor()
    {
        var patterns = Enumerable.Range(0, 200).Select(i => ($"lit{i}=", i)).ToArray();
        var automaton = AhoCorasick.Build(patterns);

        var matches = automaton.Match("baş lit7= orta lit199= son");

        Assert.Equal([7, 199], matches.Order());
    }

    [Fact]
    public void Bos_literal_yok_sayiliyor()
    {
        var automaton = AhoCorasick.Build([("", 1), ("x", 2)]);

        Assert.Equal(1, automaton.PatternCount);
        Assert.Equal([2], automaton.Match("x"));
    }

    [Fact]
    public void Bos_otomat_hicbir_sey_eslestirmiyor()
    {
        Assert.Empty(AhoCorasick.Build([]).Match("herhangi bir satır"));
    }

    [Fact]
    public void Satir_basindaki_ve_sonundaki_eslesmeler_yakalaniyor()
    {
        var automaton = AhoCorasick.Build([("bas", 1), ("son", 2)]);

        Assert.Equal([1, 2], automaton.Match("bas orta son").Order());
    }

    [Fact]
    public void Turkce_karakterli_literal_eslesiyor()
    {
        var automaton = AhoCorasick.Build([("bağlantı", 1)]);

        Assert.Equal([1], automaton.Match("uyarı: bağlantı düştü"));
    }
}

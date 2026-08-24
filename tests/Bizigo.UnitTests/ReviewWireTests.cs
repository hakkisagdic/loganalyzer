using Bizigo.Api;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Karar değerlerinin <b>tel adları</b> (T38).
///
/// <para>
/// Bu dizgiler sözleşme: ekran onları okuyor, F4 onlara göre karşılaştıracak,
/// ve altın kümedeki geçmiş kayıtlar onlarla yorumlanacak. Enum'a bir değer
/// eklendiğinde telde kendiliğinden belirmemeli — belirirse sözleşme, kimsenin
/// karar vermediği bir anda büyümüş olur.
/// </para>
///
/// <para>
/// Bekçinin şekli fazın kuralı: <b>denetlenen kümeyi yansımayla keşfet</b>, elle
/// kalan tek şey beklenen küme olsun. Enum üyeleri
/// <see cref="Enum.GetValues{TEnum}"/> ile geliyor; eşleme tablosu elle. Yeni bir
/// üye eklendiğinde eşleme eksik kalıyor ve test kırmızı yanıyor.
/// </para>
/// </summary>
public sealed class ReviewWireTests
{
    /// <summary>Telde geçerli olan karar dizgileri — sözleşmenin tamamı.</summary>
    private static readonly Dictionary<string, ReviewVerdict> Verdicts = new(StringComparer.Ordinal)
    {
        ["correct"] = ReviewVerdict.Correct,
        ["wrong"] = ReviewVerdict.Wrong,
        ["incomplete"] = ReviewVerdict.Incomplete,
        ["unknown"] = ReviewVerdict.Unknown,
    };

    private static readonly Dictionary<string, ContradictingEvidenceVerdict> Contradicting =
        new(StringComparer.Ordinal)
        {
            ["not_present"] = ContradictingEvidenceVerdict.NotPresent,
            ["sound"] = ContradictingEvidenceVerdict.Sound,
            ["trivial"] = ContradictingEvidenceVerdict.Trivial,
            ["unknown"] = ContradictingEvidenceVerdict.Unknown,
        };

    /// <summary>
    /// Her enum üyesinin bir tel adı var — ve fazlası yok.
    ///
    /// <para>
    /// <c>Enum.GetValues</c> denetlenen kümeyi veriyor; yukarıdaki tablo beklenen
    /// küme. İkisi ayrıştığı anda burası kırmızı yanıyor, yani enum'a eklenen bir
    /// değer <b>sessizce</b> ne telde belirebiliyor ne de telden kaybolabiliyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_karar_degerinin_bir_tel_adi_var()
    {
        Assert.Equal(
            [.. Enum.GetValues<ReviewVerdict>().OrderBy(v => v)],
            [.. Verdicts.Values.OrderBy(v => v)]);

        Assert.Equal(
            [.. Enum.GetValues<ContradictingEvidenceVerdict>().OrderBy(v => v)],
            [.. Contradicting.Values.OrderBy(v => v)]);
    }

    [Theory]
    [InlineData("correct", ReviewVerdict.Correct)]
    [InlineData("wrong", ReviewVerdict.Wrong)]
    [InlineData("incomplete", ReviewVerdict.Incomplete)]
    [InlineData("unknown", ReviewVerdict.Unknown)]
    public void Gecerli_karar_dizgileri_cozuluyor(string wire, ReviewVerdict expected)
    {
        Assert.True(ReviewWire.TryParseVerdict(wire, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("not_present", ContradictingEvidenceVerdict.NotPresent)]
    [InlineData("sound", ContradictingEvidenceVerdict.Sound)]
    [InlineData("trivial", ContradictingEvidenceVerdict.Trivial)]
    [InlineData("unknown", ContradictingEvidenceVerdict.Unknown)]
    public void Gecerli_celisen_kanit_dizgileri_cozuluyor(
        string wire,
        ContradictingEvidenceVerdict expected)
    {
        Assert.True(ReviewWire.TryParseContradicting(wire, out var parsed));
        Assert.Equal(expected, parsed);
    }

    /// <summary>
    /// Tanınmayan bir dizgi <b>reddediliyor</b> — varsayılana düşmüyor.
    ///
    /// <para>
    /// Düşseydi yazım hatası taşıyan bir istemci sessizce <c>unknown</c> yazardı
    /// ve altın küme, hiç sorulmamış bir soruya verilmiş cevaplarla dolardı.
    /// Kararı <c>unknown</c> olan kayıt "inceleyen bilmiyor" demek; "istemci
    /// yanlış yazdı" demek değil.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("yanlis")]
    [InlineData("partially")]
    [InlineData(null)]
    public void Taninmayan_karar_reddediliyor(string? wire)
    {
        Assert.False(ReviewWire.TryParseVerdict(wire, out _));
    }

    /// <summary>
    /// Büyük harfli ya da ayraçsız yazım <b>kabul edilmiyor</b>.
    ///
    /// <para>
    /// <c>Enum.TryParse(ignoreCase: true)</c> bunları kabul ederdi ve sözleşme
    /// belgede yazandan geniş olurdu — belgelenen ile kabul edilenin ayrışması,
    /// bu depoda tekrar tekrar sessiz hataya dönüşen şeklin ta kendisi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Correct")]
    [InlineData("CORRECT")]
    public void Kalip_disi_yazim_kabul_edilmiyor(string wire)
    {
        Assert.False(ReviewWire.TryParseVerdict(wire, out _));
    }

    [Theory]
    [InlineData("notpresent")]
    [InlineData("NotPresent")]
    public void Celisen_kanitta_da_kalip_disi_yazim_kabul_edilmiyor(string wire)
    {
        Assert.False(ReviewWire.TryParseContradicting(wire, out _));
    }
}

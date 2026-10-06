using Bizigo.Contracts;
using Bizigo.ScenarioPlugin;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Tetikleyici sözlüğü tek listeden besleniyor</b> — ve iki liste
/// ayrıştığında kırmızı yanıyor (T43 × T45 kesişimi).
///
/// <para>
/// T43 <c>ScenarioTriggers.Known</c>'ı beş sabitle bıraktı ve o beş sabit
/// K20'den alıntıydı. T45 tetikleyicileri <see cref="RcaTriggerSource"/> ile
/// getirince alıntının ikinci bir liste olarak yaşaması, iki listenin sessizce
/// ayrışması demek olurdu.
/// </para>
///
/// <para>
/// T43'ün kendi bekçisi (<c>Tetikleyici_kumesi_bes_degerde_sabit</c>) kümenin
/// <b>büyümesini</b> yakalıyor ama <b>ayrışmasını</b> yakalamıyor: enum altı
/// değere çıkıp sözlük beşte kalsaydı iki taraf da kendi içinde tutarlı
/// görünürdü ve ikisini yan yana koyan hiçbir şey olmazdı. Bu dosyanın
/// tamamı o boşluk için.
/// </para>
///
/// <para>
/// Ölçüldüğü hâliyle bu, S04'te bedeli ödenmiş hata sınıfının aynısı:
/// baseline'ın iki gösterimi vardı, sözlük birleştirilmiş <b>predicate
/// birleştirilmemişti</b>, birim paketi sessiz kaldı, kırığı CI gördü.
/// </para>
/// </summary>
public sealed class ScenarioTriggerBindingTests
{
    /// <summary>
    /// Sözlük, kaynak kümesi ile devam kümesinin <b>tam birleşimi</b>.
    ///
    /// <para>
    /// Beklenen küme burada bağımsız olarak yeniden hesaplanıyor. Biri
    /// <c>Known</c>'ı elle yazılmış bir kümeye geri çevirirse, o küme enum'dan
    /// ayrıldığı ilk anda bu test kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sozluk_kaynak_kumesinden_turuyor()
    {
        var expected = Enum.GetValues<RcaTriggerSource>()
            .Select(s => s.ToString().ToLowerInvariant())
            .Concat(ScenarioTriggers.Continuations)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), ScenarioTriggers.Known.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Kaynakların hepsi bir plugin tarafından yazılabiliyor.
    ///
    /// <para>
    /// Üstteki testin somut hâli, ve ayrı duruyor çünkü farklı bir şey
    /// söylüyor: yeni bir kaynak eklendiğinde onu <c>trigger.on</c>'da yazmak
    /// <b>hiçbir ek iş gerektirmemeli</b>. Gerektirseydi, unutulduğu tur
    /// senaryolar o kaynağa hiç bağlanamaz ve yükleyici kelimeyi reddederdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_kaynak_plugin_sozlugunde_var()
    {
        foreach (var source in Enum.GetValues<RcaTriggerSource>())
        {
            Assert.Contains(source.ToWireName(), ScenarioTriggers.Known);
        }
    }

    /// <summary>
    /// Sözlüğün kaynak kümesinden <b>fazlası yalnızca devam kuralı</b>.
    ///
    /// <para>
    /// Bu, sayıların tutmasından fazlasını söylüyor: fazlalığın <b>adı konmuş</b>
    /// olmasını şart koşuyor. Altıncı bir kelime doğrudan <c>Known</c>'a
    /// karışırsa burada kırmızı yanıyor — ve "sözlük neden enum'dan bir fazla"
    /// sorusunun cevabı tek yerde, <c>ScenarioTriggers.Continuations</c>'ta
    /// kalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Fazlalik_yalnizca_devam_kurali()
    {
        var extra = ScenarioTriggers.Known
            .Except(RcaTriggerVocabulary.WireNames, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(ScenarioTriggers.Continuations, extra);
        Assert.Empty(ScenarioTriggers.Continuations);
    }

    /// <summary>
    /// Anomali Sprint 07 (S13) ile birlikte birinci sınıf bir RCA tetikleyici kaynağı oldu.
    /// Sözlükte ve kaynak kümesinde yerini alıyor.
    /// </summary>
    [Fact]
    public void Anomali_kaynak_kumesinde_yer_aldi()
    {
        Assert.Contains(ScenarioTriggers.Anomaly, ScenarioTriggers.Known);
        Assert.Contains(ScenarioTriggers.Anomaly, RcaTriggerVocabulary.WireNames);
        Assert.Contains(
            Enum.GetNames<RcaTriggerSource>(),
            name => name.Equals("Anomaly", StringComparison.OrdinalIgnoreCase));
    }
}

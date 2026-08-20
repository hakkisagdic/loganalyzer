using System.Globalization;
using Bizigo.Cli.Seeding;
using Bizigo.Parsing.Grok;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Süpürmenin ClickHouse'suz ÖN GÖRÜSÜ</b> (T39 / D6).
///
/// <para>
/// <c>DevStackCollection</c>'a ait değil: Docker'a hiç dokunmuyor. Tohumlama
/// planı zaten deterministik ve imza yazma anında hesaplanıyor, dolayısıyla
/// "hangi imza ne zaman görülecek" sorusunun cevabı <b>veritabanına yazmadan
/// önce</b> biliniyor. Süpürmenin tamamı burada küme aritmetiğiyle
/// hesaplanabiliyor.
/// </para>
///
/// <h3>Neden bir kopya değil, bir çapraz kontrol</h3>
///
/// <para>
/// <see cref="BaselineWindowMeasurement"/> aynı soruyu ClickHouse'a SQL ile
/// soruyor; burası aynı soruyu plandan aritmetikle cevaplıyor. İkisi aynı
/// cevabı vermek <b>zorunda</b>. Ayrışırlarsa arıza SQL yolundadır
/// (<c>GetFirstSeenSignaturesAsync</c>, kapsam filtresi, zaman dilimi
/// dönüşümü) ve o arıza tek başına bakıldığında görünmez — düzgün bir eğri
/// üretir, yalnızca yanlış olanı.
/// </para>
///
/// <para>
/// İkinci faydası pratik: fixture'ın <b>okunabilir</b> bir eğri üretip
/// üretmediği Docker'lı koşumdan önce biliniyor. Aksi hâlde tohumlama
/// parametreleri her denemede kırk saniyelik bir konteyner turuna mal olurdu.
/// </para>
///
/// <para>
/// <b>Sınırı:</b> ön görü ürünün yazma yolunu ölçmüyor. Ekilen zamanın
/// <c>ts</c> kolonuna doğru düştüğünü yükleyicinin kendi bekçisi
/// (<c>GoldenSampleSeeder.Verify</c>) ve <c>GoldenSeedClickHouseTests</c>
/// doğruluyor; burada varsayılıyor.
/// </para>
/// </summary>
public sealed class BaselineFixturePreviewTests(ITestOutputHelper output)
{
    /// <summary>
    /// Ön görü de rapora yazılıyor — <c>BaselineWindowMeasurement</c>'ın
    /// <c>t35-baseline.log</c>'u ile aynı sebep: sayıya bakacak kişi test
    /// koşumunun çıktısını değil bir dosyayı okuyor, ve iki tablo yan yana
    /// konulmadan çapraz kontrol yapılamaz.
    /// </summary>
    private static readonly string LogFile =
        Path.Combine(Path.GetTempPath(), "t35-baseline-preview.log");

    /// <summary>
    /// Çapa sabit ve <b>duvar saatinden bağımsız</b>: bu testin geçme sebebinin
    /// koşulduğu anla ilgisi olmamalı. Gerçek ölçüm "şimdi"yi kullanmak
    /// zorunda (yılsız syslog damgaları yılı bugünden çıkarıyor), ama ön görü
    /// yalnızca imza ve zaman aritmetiği yaptığı için o kısıt burada yok —
    /// yeter ki çapa yakın geçmişte olsun.
    /// </summary>
    private static readonly DateTimeOffset Anchor = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(
        BaselineWindowMeasurement.RepoPath(Path.Combine("catalog", "masks", "bizigo-masks.yaml")));

    /// <summary>
    /// Ölçümün seçtiği iki düğme konumu <b>okunabilir</b> eğri üretiyor mu.
    ///
    /// <para>
    /// Reddetme protokolünün beş dalının hiçbirine düşmemeli. Düşerse
    /// Docker'lı koşum da düşerdi — ve orada bunu öğrenmenin bedeli iki
    /// konteyner, iki göç ve 240 bin satırlık tohumlama.
    /// </para>
    /// </summary>
    [Fact]
    public void Iki_fixture_de_okunabilir_egri_uretiyor()
    {
        foreach (var recipe in new[] { BaselineWindowMeasurement.Steep, BaselineWindowMeasurement.Flat })
        {
            var curve = Simulate(recipe);
            var rejection = BaselineSweepVerdict.Reject(curve.WindowSignatures, curve.Rows);

            Assert.True(
                rejection is null,
                $"'{recipe.Label}' fixture'ı okunabilir bir eğri üretmiyor:\n{rejection}\n\n" +
                $"{Render(curve)}");
        }
    }

    /// <summary>
    /// <b>Ölçümün asıl teslim ettiği şey:</b> dirsek sıklık düğmesiyle kayıyor
    /// mu.
    ///
    /// <para>
    /// Bu test bir <b>yön</b> iddia etmiyor — ne "kaymalı" ne "kaymamalı".
    /// İkisi de meşru sonuç ve hangisi olduğu fixture'ın özelliği. İddia
    /// ettiği tek şey, sonucun <b>tutarlı</b> olması:
    /// <see cref="BaselineFixtureComparison.Baseline"/> ancak ve ancak bütün
    /// dirsekler aynıyken doğuyor. Sayıyı basmak yerine ölçmek ve tabloyu
    /// koşum çıktısına yazmak, "ölçtüm" demenin bu depodaki karşılığı.
    /// </para>
    /// </summary>
    [Fact]
    public void Dirsegin_dugmeye_baglilik_durumu_olculuyor()
    {
        var steep = Simulate(BaselineWindowMeasurement.Steep);
        var flat = Simulate(BaselineWindowMeasurement.Flat);

        var comparison = BaselineFixtureVerdict.Compare(steep, flat);

        Say("=== T35 · süpürmenin ClickHouse'suz ön görüsü ===");
        Say(Render(steep));
        Say(Render(flat));
        Say(string.Empty);

        foreach (var (label, elbow) in comparison.Elbows)
        {
            Say(FormattableString.Invariant(
                $"dirsek {label,-12}: {(elbow is null ? "yok" : BaselineSweepVerdict.Describe(elbow.Value))}"));
        }

        Say(string.Empty);
        Say(comparison.Baseline is null
            ? "SEÇİLEBİLİR TABAN YOK."
            : $"SEÇİLEBİLİR TABAN: {BaselineSweepVerdict.Describe(comparison.Baseline.Value)}");
        Say(comparison.Reading);

        var agree = comparison.Elbows.Select(entry => entry.Elbow).Distinct().Count() == 1;

        Assert.Equal(agree, comparison.Baseline is not null);
    }

    private void Say(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(LogFile, line + Environment.NewLine);
    }

    /// <summary>
    /// Planı kurup her olayın imzasını hesaplar, sonra süpürmeyi küme
    /// aritmetiğiyle koşturur.
    ///
    /// <para>
    /// İmza <b>yeniden yazılmış</b> satırdan hesaplanıyor, orijinalinden değil:
    /// maskeleme sözlüğünde ay adı için maske olmadığı için aynı şablon ay
    /// değiştiğinde yeni bir imza alıyor ve orijinal satırdan hesaplamak bu
    /// etkiyi tamamen kaçırırdı — yani ön görü, gerçek ölçümün göreceği
    /// eğriden farklı bir eğri tahmin ederdi.
    /// </para>
    /// </summary>
    private static BaselineCurve Simulate(BaselineFixtureRecipe recipe)
    {
        var samples = GoldenSampleSeeder.ReadSamples(
            BaselineWindowMeasurement.RepoPath(Path.Combine("catalog", "parsers")));

        Assert.NotEmpty(samples);

        var signatures = samples
            .Select(sample => Masks.Compute(sample.Text).Hash)
            .ToList();

        var plan = GoldenSamplePlan.Build(
            signatures,
            GoldenSampleSeeder.Vendors(samples),
            new SeedPlanOptions(
                Anchor,
                BaselineWindowMeasurement.Span,
                recipe.Events,
                recipe.ZipfExponent,
                recipe.Seed));

        // (an, imza) — süpürmenin ihtiyaç duyduğu tek şey.
        var events = new (DateTimeOffset At, ulong Hash)[plan.Count];

        for (var i = 0; i < plan.Count; i++)
        {
            var occurrence = plan[i];
            var text = SampleTimeRewriter.Rewrite(samples[occurrence.LineIndex].Text, occurrence.At).Text;
            events[i] = (occurrence.At, Masks.Compute(text).Hash);
        }

        var to = Anchor;
        var from = to - BaselineWindowMeasurement.WindowLength;

        var windowSignatures = Distinct(events, from, to);
        var rows = new List<BaselineSweepRow>();

        foreach (var length in BaselineWindowMeasurement.BaselineLengths)
        {
            var baselineFrom = from - BaselineWindowMeasurement.Gap - length;
            var probeEnd = baselineFrom + BaselineWindowMeasurement.ProbeSpan(length);

            if (Distinct(events, baselineFrom, probeEnd).Count == 0)
            {
                rows.Add(new BaselineSweepRow(length, BaselineLengthStatus.ArchiveTooShort, 0, 0));
                continue;
            }

            var baseline = Distinct(events, baselineFrom, from - BaselineWindowMeasurement.Gap);
            var firstSeen = windowSignatures.Count(hash => !baseline.Contains(hash));
            var ratio = windowSignatures.Count == 0 ? 0 : (double)firstSeen / windowSignatures.Count;

            rows.Add(new BaselineSweepRow(length, BaselineLengthStatus.Measured, firstSeen, ratio));
        }

        return new BaselineCurve(recipe.Label, recipe.ZipfExponent, windowSignatures.Count, rows);
    }

    /// <summary>
    /// <c>[from, to)</c> aralığındaki ayrı imzalar. İmzasız satır (<c>0</c>)
    /// dışarıda: "imza yok" bir kimlik değil ve SQL tarafı da onu saymıyor.
    /// </summary>
    private static HashSet<ulong> Distinct(
        (DateTimeOffset At, ulong Hash)[] events,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var set = new HashSet<ulong>();

        foreach (var (at, hash) in events)
        {
            if (hash != 0 && at >= from && at < to)
            {
                set.Add(hash);
            }
        }

        return set;
    }

    private static string Render(BaselineCurve curve)
    {
        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"--- ön görü '{curve.Label}' · zipf={curve.ZipfExponent:0.##} · " +
                $"pencere ayrı imza {curve.WindowSignatures} ---"),
            $"{"taban",10} {"ilk-görülen",12} {"yeni oranı",12}",
        };

        foreach (var row in curve.Rows)
        {
            lines.Add(row.Status == BaselineLengthStatus.Measured
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{BaselineSweepVerdict.Describe(row.Length),10} {row.FirstSeen,12} {100.0 * row.NewRatio,11:0.0}%")
                : $"{BaselineSweepVerdict.Describe(row.Length),10} {"—",12} {"arşiv ulaşmıyor",12}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

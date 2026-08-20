using Bizigo.Api;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T34 ve T36'nın çivilediği değişmez, T37'nin iki çıktısında ayrı ayrı.</b>
///
/// <para>
/// <c>Empty</c> · <c>NeverFed</c> · <c>Unavailable</c>/<c>Failed</c> ·
/// <c>NotRegistered</c> — dördü de <b>ayırt edilebilir</b> kalmak zorunda. Tek
/// bir "veri yok" değerine indirgemek, T34 ve T36'nın kurduğu her şeyi tek
/// satırda geri alır ve <b>hiçbir şey haber vermez</b>: hata yok, sayaç yok,
/// belirti yok — yalnızca raporu okuyanın yanlış bir sonuca varması.
/// </para>
///
/// <para>
/// En pahalısı <c>NeverFed</c>: "değişiklik akışı hiç beslenmemiş" cümlesi
/// "değişiklik olmadı" diye görünürse, kullanıcı RCA'nın en güçlü sinyalinin
/// <b>yokluğunu</b> bir bulgu sanar ve kök nedeni başka yerde aramaya başlar.
/// </para>
///
/// <para>
/// <b>Neden iki ayrı test sınıfı gibi iki ayrı bölüm:</b> tel yüzeyi ile export
/// ayrı sorular ve birinde geçip diğerinde kaybolması beklenen kaza. Ekran
/// tarafındaki karşılığı <c>ui/tests/rca-screen.test.tsx</c>.
/// </para>
/// </summary>
public sealed class RcaFourStatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Dört olgunun her biri, gerçekte doğdukları yerden bir örnek.
    /// <c>Empty</c> ayrı duruyor — o "bakıldı" tarafında ve bu ayrımın kendisi
    /// sınanan şey.
    /// </summary>
    private static EvidenceBundle Bundle() => EvidenceBundleTests.Bundle(
        new EvidenceSlice
        {
            ProviderId = "logs.first-seen",
            Kind = EvidenceKind.Log,
            Status = EvidenceStatus.Empty,
            Detail = "Tabanda görülmeyen imza yok.",
        },
        new EvidenceSlice
        {
            ProviderId = "change.feed",
            Kind = EvidenceKind.Change,
            Status = EvidenceStatus.NeverFed,
            Detail = "Değişiklik akışı hiç beslenmemiş — bağlı bir connector yok.",
        },
        new EvidenceSlice
        {
            ProviderId = "logs.silence",
            Kind = EvidenceKind.Log,
            Status = EvidenceStatus.Unavailable,
            Detail = "Kaynak etkinlik yüzeyi kapalı.",
        },
        new EvidenceSlice
        {
            ProviderId = "logs.volume",
            Kind = EvidenceKind.Log,
            Status = EvidenceStatus.Failed,
            Detail = "Sorgu zaman aşımına uğradı.",
        },
        new EvidenceSlice
        {
            ProviderId = "metric.baseline",
            Kind = EvidenceKind.Metric,
            Status = EvidenceStatus.NotRegistered,
            Detail = "Bu tür için sağlayıcı yok — F5.",
        });

    // ---------------------------------------------------------------- tel ---

    /// <summary>
    /// <b>Tel yüzeyi.</b> Dört durum dört <b>farklı</b> dizgi taşıyor ve
    /// <c>silent</c> ile <c>not_consulted</c> ayrı listeler.
    ///
    /// <para>
    /// Ekran bu dizgilere göre dallanıyor; hepsi aynı değere düşseydi ekranın
    /// ayrımı yapabilmesinin hiçbir yolu kalmazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Tel_yuzeyinde_dort_durum_ayirt_edilebiliyor()
    {
        var response = RcaReportResponse.Of(DeterministicReport.From(Bundle()), review: null);

        // "Baktık, yok" ayrı listede: bu bir kanıt, bakılmamışlık değil.
        Assert.Equal(["logs.first-seen"], response.Silent.Select(s => s.ProviderId));
        Assert.Equal(["empty"], response.Silent.Select(s => s.Status));

        var byProvider = response.NotConsulted.ToDictionary(s => s.ProviderId, s => s.Status, StringComparer.Ordinal);

        Assert.Equal("never_fed", byProvider["change.feed"]);
        Assert.Equal("unavailable", byProvider["logs.silence"]);
        Assert.Equal("failed", byProvider["logs.volume"]);
        Assert.Equal("not_registered", byProvider["metric.baseline"]);

        // Dördü gerçekten dört ayrı değer — aynı dizgiye düşen ikisi ayrımı
        // sessizce yok ederdi.
        Assert.Equal(4, byProvider.Values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// <see cref="RcaSliceResponse.Detail"/> her durumda dolu geliyor: durum
    /// etiketi tek başına "neden bakılmadı" sorusunu cevaplamıyor.
    /// </summary>
    [Fact]
    public void Her_bakilmayan_neden_bakilmadigini_tasiyor()
    {
        var response = RcaReportResponse.Of(DeterministicReport.From(Bundle()), review: null);

        Assert.All(response.NotConsulted, slice => Assert.NotEmpty(slice.Detail));
        Assert.All(response.Silent, slice => Assert.NotEmpty(slice.Detail));
    }

    /// <summary>
    /// <b>"Ölçemedik" ile "sıfır" farklı</b> — ve ikincisi "sorun yok" diye
    /// okunuyor. Tel üzerinde <c>measured</c> ayrı bir alan, oran ise
    /// ölçülmediyse <see langword="null"/>, sıfır değil.
    /// </summary>
    [Fact]
    public void Olculemeyen_zaman_sifirdan_ayirt_edilebiliyor()
    {
        var unmeasured = RcaTrustResponse.Of(WindowTrust.Unmeasured);
        var measuredClean = RcaTrustResponse.Of(new WindowTrust(1_204, 0));

        Assert.False(unmeasured.Measured);
        Assert.Null(unmeasured.UnreliableRatio);

        Assert.True(measuredClean.Measured);
        Assert.Equal(0d, measuredClean.UnreliableRatio);

        // İkisi de "0 güvenilmez olay" gösteriyor; ayıran tek şey `measured`.
        Assert.Equal(unmeasured.UnreliableTimeEvents, measuredClean.UnreliableTimeEvents);
        Assert.NotEqual(unmeasured.Measured, measuredClean.Measured);
    }

    // ------------------------------------------------------------- export ---

    /// <summary>
    /// <b>Export.</b> Aynı ayrım Markdown'da da duruyor — ekranda geçip
    /// export'ta kaybolması tam olarak beklenen kaza, ve o kaza sessiz: PDF'i
    /// okuyan kişi eksik olanı göremez.
    /// </summary>
    [Fact]
    public void Export_dort_durumu_ayri_etiketlerle_yaziyor()
    {
        var markdown = DeterministicReport.From(Bundle()).ToMarkdown();

        Assert.Contains("besleme yok", markdown, StringComparison.Ordinal);
        Assert.Contains("kapalı", markdown, StringComparison.Ordinal);
        Assert.Contains("hata", markdown, StringComparison.Ordinal);
        Assert.Contains("sağlayıcı yok", markdown, StringComparison.Ordinal);

        // Her sağlayıcının gerekçesi de metinde: etiket "ne oldu"yu söylüyor,
        // gerekçe "neden"i.
        Assert.Contains("Değişiklik akışı hiç beslenmemiş", markdown, StringComparison.Ordinal);
        Assert.Contains("Bu tür için sağlayıcı yok", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Empty</c> export'ta <b>bakılmayanlar</b> bölümüne düşmüyor.
    ///
    /// <para>
    /// Yukarıdaki testin tersi ve ayrı yazılması şart: yalnızca "dört etiket
    /// metinde var" demek, <c>Empty</c>'nin de aralarına karışmadığını
    /// göstermiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Export_bakildi_ama_bos_u_bakilmayanlardan_ayiriyor()
    {
        var markdown = DeterministicReport.From(Bundle()).ToMarkdown();

        var silentSection = Section(markdown, "## Bakıldı, kanıt çıkmadı");
        var notConsultedSection = Section(markdown, "## Bakılmayanlar");

        Assert.Contains("logs.first-seen", silentSection, StringComparison.Ordinal);
        Assert.DoesNotContain("logs.first-seen", notConsultedSection, StringComparison.Ordinal);

        Assert.Contains("change.feed", notConsultedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("change.feed", silentSection, StringComparison.Ordinal);
    }

    /// <summary>
    /// Export ekranla <b>aynı</b> paketi anlatıyor: iki çıktıda görünen
    /// sağlayıcı kümesi birebir aynı.
    ///
    /// <para>
    /// Bu testin işi biçim değil <b>kapsama</b>: bir gün biri tele yeni bir
    /// durum ekleyip export'u güncellemeyi unutursa, ya da tersi, burada
    /// kırmızı yanıyor. İki çıktının ayrışmasını kovalayacak başka bir şey yok.
    /// </para>
    /// </summary>
    [Fact]
    public void Ekran_ile_export_ayni_saglayicilari_gosteriyor()
    {
        var report = DeterministicReport.From(Bundle());
        var response = RcaReportResponse.Of(report, review: null);
        var markdown = report.ToMarkdown();

        var onWire = response.Silent.Concat(response.NotConsulted).Select(s => s.ProviderId).Order(StringComparer.Ordinal);

        foreach (var providerId in onWire)
        {
            Assert.Contains(providerId, markdown, StringComparison.Ordinal);
        }

        Assert.Equal(5, response.Silent.Count + response.NotConsulted.Count);
    }

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{heading}' bölümü raporda yok.");

        var next = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }
}

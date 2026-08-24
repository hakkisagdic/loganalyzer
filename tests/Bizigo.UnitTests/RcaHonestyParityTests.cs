using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// Raporun <b>dürüstlük satırları</b> — export tarafı (T37).
///
/// <para>
/// Aynı satırları iki ayrı uygulama üretiyor: export'ta
/// <c>DeterministicReport.AppendHonesty</c>, ekranda <c>honestyLines</c>
/// (<c>ui/src/lib/rca/report.ts</c>). İkisi arasında derleyicinin kovaladığı
/// bir bağ <b>yok</b> — tel adlarındaki durumun aynısı, ve çözüm de aynı: dil
/// sınırının iki yanına birer çivi. Ekran tarafındaki ikizi
/// <c>rca-screen.test.tsx > dürüstlük satırları</c>.
/// </para>
///
/// <para>
/// <b>Ayrışmanın bedeli tek yönlü kötü:</b> ekranı okuyan kısıtı görür, PDF'i
/// okuyan görmez. Olay sonrası paylaşılan şey rapor, ekran değil — ticket'ın
/// kabul kriteri de bunu söylüyor: <i>"kapsam dışı ve zaman uyarıları export'ta
/// da var"</i>.
/// </para>
///
/// <para>
/// <c>DeterministicReportTests</c> tek tek satırların metnini sınıyor; burada
/// sınanan şey <b>küme</b>: hangi cinsler var, hangileri birbirini dışlıyor, ve
/// sıfır olan bir sayının satır <b>üretmediği</b>.
/// </para>
/// </summary>
public sealed class RcaHonestyParityTests
{
    private const string Heading = "## Bu raporu okurken";

    private static EvidenceBundle Bundle(
        WindowTrust trust,
        long outOfScope = 0,
        bool partial = false)
    {
        var slice = new EvidenceSlice
        {
            ProviderId = "logs.first-seen",
            Kind = EvidenceKind.Log,
            Status = partial ? EvidenceStatus.Failed : EvidenceStatus.Empty,
            Detail = partial ? "Sorgu patladı." : "Bu pencerede eşleşme yok.",
            OutOfScopeCount = outOfScope,
        };

        return EvidenceBundleTests.Bundle(slice) with { Trust = trust };
    }

    private static string Honesty(EvidenceBundle bundle)
    {
        var markdown = DeterministicReport.From(bundle).ToMarkdown();
        var start = markdown.IndexOf(Heading, StringComparison.Ordinal);

        if (start < 0)
        {
            return string.Empty;
        }

        var next = markdown.IndexOf("\n## ", start + Heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    /// <summary>
    /// Dört uyarı cinsi, ve <b>ölçülemedi</b> ile <b>güvenilmez</b> birbirini
    /// dışlıyor — ekran tarafı da aynı dördü aynı biçimde üretiyor.
    /// </summary>
    [Fact]
    public void Dort_uyari_cinsi_export_ta_da_var()
    {
        var unmeasured = Honesty(Bundle(WindowTrust.Unmeasured, outOfScope: 342, partial: true));

        Assert.Contains("342", unmeasured, StringComparison.Ordinal);
        Assert.Contains("ölçülemedi", unmeasured, StringComparison.Ordinal);
        Assert.Contains("Kanıt **eksik**", unmeasured, StringComparison.Ordinal);

        // Ölçüldüyse "ölçülemedi" satırı yok; yerine oran satırı geliyor.
        var unreliable = Honesty(Bundle(new WindowTrust(1_000, 142)));

        Assert.DoesNotContain("ölçülemedi", unreliable, StringComparison.Ordinal);
        Assert.Contains("142", unreliable, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sıfır kapsam dışı kayıtta satır hiç yazılmıyor.</b> Her raporda duran
    /// bir uyarı hiçbir şey söylemez — ve bir gün gerçekten 342 olduğunda okuyan
    /// kişi onu her zamanki gürültü sanar.
    /// </summary>
    [Fact]
    public void Kapsam_disi_sifirken_export_ta_satir_yok()
    {
        var honesty = Honesty(Bundle(new WindowTrust(1_204, 0), outOfScope: 0));

        Assert.DoesNotContain("Kapsamınız dışında", honesty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Uyarısı olmayan raporda <b>bölüm hiç açılmıyor</b>: boş bir "bu raporu
    /// okurken" başlığı, okuyana bakılacak bir kısıt olduğunu ima ederdi.
    /// </summary>
    [Fact]
    public void Uyari_yokken_bolum_hic_acilmiyor()
    {
        Assert.Equal(string.Empty, Honesty(Bundle(new WindowTrust(1_204, 0))));
    }

    /// <summary>
    /// Kapsam dışı sayısı <b>olduğu gibi</b> yazılıyor — yuvarlanan bir sayı,
    /// ekranla export'un ayrışmasının en sessiz yolu olurdu.
    /// </summary>
    [Fact]
    public void Kapsam_disi_sayisi_yuvarlanmiyor()
    {
        var honesty = Honesty(Bundle(new WindowTrust(1_204, 0), outOfScope: 1_204_337));

        Assert.Contains("1204337", honesty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kapsam dışı satırı <b>yalnızca sayı</b> veriyor — grup adı da bir
    /// sızıntı (K17, RCA §3.2). Ekran tarafında da aynı iddia var.
    /// </summary>
    [Fact]
    public void Kapsam_disi_satiri_grup_adi_sizdirmiyor()
    {
        var honesty = Honesty(Bundle(new WindowTrust(1_204, 0), outOfScope: 342));

        Assert.Contains("342", honesty, StringComparison.Ordinal);

        // Paketin kapsamı `network/core`; cümle bilinçli olarak belirsiz.
        Assert.DoesNotContain("network/core", honesty, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Uyarılar en üstte.</b> Raporun sonunda duran bir kısıt okunmuyor, ve
    /// okunmayan bir kısıt hiç yazılmamış gibi.
    /// </summary>
    [Fact]
    public void Uyarilar_bulgulardan_once_geliyor()
    {
        var markdown = DeterministicReport
            .From(Bundle(WindowTrust.Unmeasured, outOfScope: 342))
            .ToMarkdown();

        Assert.True(
            markdown.IndexOf(Heading, StringComparison.Ordinal)
            < markdown.IndexOf("## Bulgular", StringComparison.Ordinal),
            "Dürüstlük satırları bulguların altına düşmüş.");
    }
}

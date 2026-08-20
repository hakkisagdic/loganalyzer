using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// LLM'siz raporun kabul kriterleri (T36, K22).
///
/// <para>
/// <b>K22'nin tek sınavı:</b> model kapalıyken rapor okunabiliyor ve işe
/// yarıyor. "İşe yarıyor" burada ölçülebilir bir şey: kullanıcı hiçbir model
/// koşmadan pencerede <i>ne yeni oldu</i>, <i>ne değişti</i>, <i>ne sustu</i>,
/// <i>neye bakılmadı</i> ve <i>neye ne kadar güvenilebilir</i> sorularının
/// cevabını raporun metninde bulabilmeli.
/// </para>
/// </summary>
public sealed class DeterministicReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private static EvidenceItem Item(string providerId, string id, double weight, int offsetSeconds, string summary) =>
        new(id, providerId, EvidenceKind.Log, Now.AddSeconds(offsetSeconds), weight, summary,
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static EvidenceSlice Gathered(string providerId, params EvidenceItem[] items) => new()
    {
        ProviderId = providerId,
        Kind = providerId.StartsWith("change", StringComparison.Ordinal) ? EvidenceKind.Change : EvidenceKind.Log,
        Status = EvidenceStatus.Gathered,
        Items = items,
    };

    private static EvidenceBundle Bundle(WindowTrust? trust = null, params EvidenceSlice[] slices) =>
        EvidenceBundleTests.Bundle(slices) with { Trust = trust ?? new WindowTrust(1_204, 0) };

    /// <summary>
    /// <b>K22'nin sınavı.</b> Rapor tek başına, modelsiz, beş soruyu da
    /// cevaplıyor.
    ///
    /// <para>
    /// Testin metne bakması bilerek: rapor bir <b>okunacak şey</b>, ve
    /// alanların doğru dolması onun okunabilir olduğunu kanıtlamıyor. RCA
    /// belgesinin sözü de metin üzerineydi — "pencerede ilk kez şu 3 imza
    /// göründü, öncesinde şu config değişti, şu 12 cihaz sustu".
    /// </para>
    /// </summary>
    [Fact]
    public void Model_kapaliyken_rapor_bes_soruyu_cevapliyor()
    {
        var bundle = Bundle(
            new WindowTrust(1_204, 0),
            Gathered("logs.first-seen", Item("logs.first-seen", "fs:42", 14, 120, "ilk kez görüldü · 14 kaynak · BGP_PEER_DOWN")),
            Gathered("change.feed", Item("change.feed", "chg:1", 1.0, -600, "acl-push · core-sw-02 · aktör: m.yilmaz")),
            Gathered("logs.silence", Item("logs.silence", "sil:1", 3, 240, "edge-rtr-07 · 12 cihaz sustu")),
            new EvidenceSlice
            {
                ProviderId = "logs.volume",
                Kind = EvidenceKind.Log,
                Status = EvidenceStatus.Empty,
                Detail = "Tabana göre anlamlı hacim sapması yok.",
            },
            new EvidenceSlice
            {
                ProviderId = "(metric)",
                Kind = EvidenceKind.Metric,
                Status = EvidenceStatus.NotRegistered,
                Detail = "Bu kanıt türü için sağlayıcı yok — F5.",
            });

        var markdown = DeterministicReport.From(bundle).ToMarkdown();

        // 1 · Ne yeni oldu · 2 · Ne değişti · 3 · Ne sustu
        Assert.Contains("BGP_PEER_DOWN", markdown, StringComparison.Ordinal);
        Assert.Contains("acl-push", markdown, StringComparison.Ordinal);
        Assert.Contains("12 cihaz sustu", markdown, StringComparison.Ordinal);

        // 4 · Neye bakıldı ama bir şey çıkmadı · neye HİÇ bakılmadı
        Assert.Contains("Bakıldı, kanıt çıkmadı", markdown, StringComparison.Ordinal);
        Assert.Contains("logs.volume", markdown, StringComparison.Ordinal);
        Assert.Contains("Bakılmayanlar", markdown, StringComparison.Ordinal);
        Assert.Contains("sağlayıcı yok", markdown, StringComparison.Ordinal);

        // 5 · Zaman çizelgesi: değişiklik ilk-görülenden ÖNCE duruyor.
        var timeline = DeterministicReport.From(bundle).Timeline;
        Assert.Equal("change.feed", timeline[0].Item.ProviderId);

        // Ve hiçbir yerde model yok.
        Assert.Contains("model kullanılmadı", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kabul kriteri:</b> change beslemesi boşsa rapor "değişiklik verisi
    /// yok" diyor, "değişiklik yok" demiyor.
    ///
    /// <para>
    /// İkisi farklı ve karıştırmak <b>yanlış güven</b> üretir: RCA'nın en güçlü
    /// sinyali hiç bağlanmamışken rapor "bu pencerede bir şey değişmedi" derse,
    /// okuyan kök nedeni başka yerde aramaya başlar.
    /// </para>
    /// </summary>
    [Fact]
    public void Besleme_yokken_rapor_degisiklik_yok_demiyor()
    {
        var neverFed = Bundle(null, new EvidenceSlice
        {
            ProviderId = "change.feed",
            Kind = EvidenceKind.Change,
            Status = EvidenceStatus.NeverFed,
            Detail = "Değişiklik akışında hiç kayıt yok — besleme bağlı olmayabilir. "
                + "Bu, 'değişiklik olmadı' demek DEĞİL.",
        });

        var markdown = DeterministicReport.From(neverFed).ToMarkdown();

        Assert.Contains("Bakılmayanlar", markdown, StringComparison.Ordinal);
        Assert.Contains("besleme yok", markdown, StringComparison.Ordinal);
        Assert.Contains("'değişiklik olmadı' demek DEĞİL", markdown, StringComparison.Ordinal);

        // Ve "bakıldı" tarafında görünmüyor — orada olması, ölçülmemiş bir şeyi
        // ölçülmüş gibi göstermek olurdu.
        Assert.DoesNotContain("change.feed", DeterministicReport.From(neverFed).Silent.Select(s => s.ProviderId));
    }

    /// <summary>
    /// Aynı sağlayıcı <b>boş</b> döndüğünde rapor bunu "bakıldı" tarafına
    /// yazıyor — yukarıdakinin tersi. İkisi ayrı yazılmasaydı test tek yönlü
    /// olurdu ve ayrımın gerçekten yapıldığını göstermezdi.
    /// </summary>
    [Fact]
    public void Bakildi_ama_bos_ile_besleme_yok_farkli_bolumlerde()
    {
        var empty = Bundle(null, new EvidenceSlice
        {
            ProviderId = "change.feed",
            Kind = EvidenceKind.Change,
            Status = EvidenceStatus.Empty,
            Detail = "Bu pencerede kayıtlı değişiklik yok.",
        });

        var report = DeterministicReport.From(empty);

        Assert.Contains("change.feed", report.Silent.Select(s => s.ProviderId));
        Assert.Empty(report.NotConsulted);
        Assert.Contains("Bu pencerede kayıtlı değişiklik yok", report.ToMarkdown(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kabul kriteri:</b> penceresinde <c>time_source != parsed</c> olan olay
    /// varsa rapor bunu söylüyor — ve uyarı <b>en üstte</b> duruyor.
    /// </summary>
    [Fact]
    public void Guvenilmez_zaman_raporun_ustunde_soyleniyor()
    {
        var markdown = DeterministicReport
            .From(Bundle(new WindowTrust(1_204, 142), Gathered("logs.propagation")))
            .ToMarkdown();

        Assert.Contains("142", markdown, StringComparison.Ordinal);
        Assert.Contains("zamanı cihazdan", markdown, StringComparison.Ordinal);

        // Uyarı bulgulardan önce: sonda duran bir kısıt okunmuyor.
        Assert.True(
            markdown.IndexOf("Bu raporu okurken", StringComparison.Ordinal)
                < markdown.IndexOf("## Bulgular", StringComparison.Ordinal),
            "Zaman uyarısı bulguların altında kalmış.");
    }

    /// <summary>
    /// <b>Bekçinin kırmızı yanabildiği yön:</b> zamanların hepsi güvenilirken
    /// uyarı <b>yok</b>. Her raporda duran bir uyarı hiçbir şey söylemez.
    /// </summary>
    [Fact]
    public void Guvenilir_zamanlarda_uyari_yok()
    {
        var markdown = DeterministicReport
            .From(Bundle(new WindowTrust(1_204, 0), Gathered("logs.propagation")))
            .ToMarkdown();

        Assert.DoesNotContain("zamanı cihazdan", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ölçülemedi</b> ile <b>sıfır</b> raporda da farklı: ölçülemeyen bir şey
    /// "sorun yok" diye yazılmıyor.
    /// </summary>
    [Fact]
    public void Olculemeyen_zaman_bilinmiyor_diye_yaziliyor()
    {
        var markdown = DeterministicReport
            .From(Bundle(WindowTrust.Unmeasured, Gathered("logs.propagation")))
            .ToMarkdown();

        Assert.Contains("ölçülemedi", markdown, StringComparison.Ordinal);
        Assert.Contains("'sorun yok' değil", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kapsam dışı satır sayı veriyor, <b>içerik vermiyor</b> (RCA §3.2).
    /// </summary>
    [Fact]
    public void Kapsam_disi_satiri_sayi_veriyor_icerik_vermiyor()
    {
        var bundle = Bundle(null, EvidenceBundleTests.Slice(
            "change.feed", EvidenceStatus.Gathered, outOfScope: 342, items: [("chg", 1.0)]));

        var markdown = DeterministicReport.From(bundle).ToMarkdown();

        Assert.Contains("342", markdown, StringComparison.Ordinal);
        Assert.Contains("Kapsamınız dışında", markdown, StringComparison.Ordinal);
    }

    /// <summary>Kapsam dışı yokken o satır hiç yazılmıyor.</summary>
    [Fact]
    public void Kapsam_disi_yokken_satir_yok()
    {
        var markdown = DeterministicReport.From(Bundle(null, Gathered("logs.first-seen"))).ToMarkdown();

        Assert.DoesNotContain("Kapsamınız dışında", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rapor deterministik: aynı paket her zaman aynı metni üretiyor. Aksi
    /// halde "aynı kanıt, farklı model" karşılaştırmasında raporun kendisi bir
    /// değişken olurdu.
    /// </summary>
    [Fact]
    public void Ayni_paket_ayni_metni_uretiyor()
    {
        var bundle = Bundle(
            null,
            Gathered("logs.first-seen", Item("logs.first-seen", "a", 3, 10, "a")),
            Gathered("logs.volume", Item("logs.volume", "b", 9, 20, "b")));

        Assert.Equal(
            DeterministicReport.From(bundle).ToMarkdown(),
            DeterministicReport.From(bundle).ToMarkdown());
    }

    /// <summary>
    /// Hiçbir sinyal bir şey bulmadığında rapor bunu <b>söylüyor</b>. Boş bir
    /// bölüm bırakmak, okuyanın raporun yarım kaldığını sanmasına yol açardı.
    /// </summary>
    [Fact]
    public void Bulgu_yokken_rapor_bunu_soyluyor()
    {
        var markdown = DeterministicReport.From(Bundle(null)).ToMarkdown();

        Assert.Contains("Hiçbir sinyal bu pencerede kanıt üretmedi", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ham log gövdesindeki <c>|</c> Markdown tablosunu bozmuyor.
    ///
    /// <para>
    /// Teorik bir risk değil: kanıt özetleri ham log satırı taşıyor ve syslog
    /// gövdelerinde boru işareti sık. Kaçırılmasaydı tablo sessizce bozulur,
    /// yani <b>rapor okunamaz hâle gelir</b> — üstelik yalnızca bazı olaylarda.
    /// </para>
    /// </summary>
    [Fact]
    public void Govdedeki_boru_isareti_tabloyu_bozmuyor()
    {
        var bundle = Bundle(null, Gathered(
            "logs.first-seen",
            Item("logs.first-seen", "a", 1, 0, "deny src=10.0.0.1|dst=10.0.0.2\nikinci satır")));

        var markdown = DeterministicReport.From(bundle).ToMarkdown();
        // Satır hem bulgularda hem zaman çizelgesinde geçiyor; ikisi de aynı
        // kaçırmadan geçmek zorunda.
        var rows = markdown.Split('\n').Where(line => line.Contains("deny src", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, rows.Length);
        var row = rows[0];

        // Kaçırılmış borular çıkarıldığında geriye tablonun kendi 5 sınırı kalmalı
        // (baş, üç ayraç, son). Gövdeden gelen boru bir hücre daha açsaydı 6 olurdu.
        Assert.Contains("\\|", row, StringComparison.Ordinal);
        Assert.Equal(5, row.Replace("\\|", string.Empty, StringComparison.Ordinal).Count(c => c == '|'));

        // Satır sonu da kaçırılıyor: gövdedeki `\n` tabloyu ikiye bölerdi.
        Assert.Contains("ikinci satır", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eksik kanıt raporun üstünde söyleniyor: kırpılmış bir liste "hepsi bu"
    /// gibi okunur ve rapor eksik kanıta tam kanıt muamelesi yapar.
    /// </summary>
    [Fact]
    public void Eksik_kanit_soyleniyor()
    {
        var bundle = Bundle(null, Gathered("logs.first-seen") with { Truncated = true });

        Assert.True(DeterministicReport.From(bundle).IsPartial);
        Assert.Contains("Kanıt **eksik**", DeterministicReport.From(bundle).ToMarkdown(), StringComparison.Ordinal);
    }
}

using Bizigo.Alerting;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Manifest → <c>alert_rules</c> senkronunun bekçileri (T33).
///
/// <para>
/// Karar mantığı saf olduğu için burada, Docker'sız sınanıyor. Varlığın içine
/// gömülseydi yalnızca Testcontainers'lı bir koşumda ölçülebilirdi — yani ayda
/// birkaç kez, yani pratikte hiç.
/// </para>
/// </summary>
public sealed class SigmaRuleSyncTests
{
    private static SigmaManifestRule Written(string sha) => new()
    {
        RuleId = "r1",
        Status = SigmaRuleSync.StatusWritten,
        SourceSha = "sha256:kaynak",
        OutputSha = sha,
    };

    private static SigmaManifestRule Gated(string column, string remedy = "schema") => new()
    {
        RuleId = "r2",
        Status = SigmaRuleSync.StatusGated,
        SourceSha = "sha256:kaynak",
        Blockers = [new SigmaManifestBlocker
        {
            Column = column,
            Kind = "unknown_column",
            Remedy = remedy,
            Message = $"`{column}` bu şemada eşlenemiyor",
        }],
    };

    /// <summary>
    /// <b>Yeni kural `Disabled` iniyor, `Enabled` DEĞİL.</b>
    ///
    /// <para>
    /// Derleme hattı bir kuralı üretebilir ama kullanıcı adına açamaz. Sigma
    /// korpusu 269 kurala çıktığında hepsinin kendiliğinden etkinleşmesi,
    /// kullanıcının hiç istemediği bir gürültü seli olurdu — ve o seli kimse
    /// "senkron açtı" diye okumazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Yeni_kural_kullanici_adina_ACILMIYOR()
    {
        var decision = SigmaRuleSync.Decide(Written("sha256:cikti"), existing: null);

        Assert.Equal(SigmaSyncAction.Create, decision.Action);
        Assert.Equal(AlertRuleStatus.Disabled, decision.Status);
    }

    /// <summary>Yeni ve koşamayan kural doğrudan <c>Gated</c> iniyor.</summary>
    [Fact]
    public void Yeni_gated_kural_Gated_iniyor_ve_sebebini_tasiyor()
    {
        var decision = SigmaRuleSync.Decide(Gated("dns_query_name"), existing: null);

        Assert.Equal(AlertRuleStatus.Gated, decision.Status);
        // Sebep NE YAPILACAĞINI söylüyor: `remedy` ve `column` birlikte, çünkü
        // "şema bekliyor" tek başına hangi alanı bekliyor demiyor.
        Assert.Contains("[schema]", decision.GatedReason, StringComparison.Ordinal);
        Assert.Contains("dns_query_name", decision.GatedReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>`written` ölçütü `output_sha`, `source_sha` DEĞİL.</b>
    ///
    /// <para>
    /// Pipeline değiştiğinde kaynak kural aynı kalır ama kuralın <b>ne
    /// yakaladığı</b> oynar. Kaynağa bakan bir ölçüt o durumda sessiz kalır ve
    /// kullanıcı, açtığı kuralın hâlâ aynı şeyi yakaladığını sanır — bu depoda
    /// beş kez ısıran şeklin aynısı.
    /// </para>
    /// </summary>
    [Fact]
    public void Written_kuralda_olcut_output_sha()
    {
        var existing = new SigmaRuleSync.SigmaExistingRule(
            AlertRuleStatus.Enabled, "sha256:eski");

        var changed = SigmaRuleSync.Decide(Written("sha256:yeni"), existing);
        Assert.Equal(SigmaSyncAction.Changed, changed.Action);

        var same = SigmaRuleSync.Decide(Written("sha256:eski"), existing);
        Assert.Equal(SigmaSyncAction.Unchanged, same.Action);

        // Ve kullanıcının açma kararı KORUNUYOR: SQL değişti diye kural
        // kapatılmıyor, yalnızca değiştiği görünüyor.
        Assert.Equal(AlertRuleStatus.Enabled, changed.Status);
    }

    /// <summary>
    /// <b>`gated` kuralda `output_sha` YOK</b> — ölçüt kaynak artı engeller.
    ///
    /// <para>
    /// Koşulsuz <c>output_sha</c> okunsaydı bu kurallar sonsuza kadar
    /// "değişmemiş" görünürdü: boş dizge her zaman boş dizgeye eşit.
    /// </para>
    /// </summary>
    [Fact]
    public void Gated_kuralda_olcut_kaynak_ARTI_engeller()
    {
        Assert.Null(Gated("dns_query_name").OutputSha);

        var existing = new SigmaRuleSync.SigmaExistingRule(
            AlertRuleStatus.Gated,
            SigmaRuleSync.GatedFingerprint(Gated("dns_query_name")));

        var same = SigmaRuleSync.Decide(Gated("dns_query_name"), existing);
        Assert.Equal(SigmaSyncAction.Unchanged, same.Action);

        // Kaynak AYNI ama engel değişti: kural artık başka bir kolonda takılıyor.
        // Kullanıcıya gösterilen sebep bayatlamamalı.
        var moved = SigmaRuleSync.Decide(Gated("answer"), existing);
        Assert.Equal(SigmaSyncAction.Changed, moved.Action);
    }

    /// <summary>
    /// <c>Gated</c>, kullanıcının açma kararını <b>eziyor</b> — koşamayan bir
    /// kural açık gösterilemez.
    /// </summary>
    [Fact]
    public void Gated_kullanicinin_ACIK_kararini_eziyor()
    {
        var existing = new SigmaRuleSync.SigmaExistingRule(
            AlertRuleStatus.Enabled, "sha256:eski");

        var decision = SigmaRuleSync.Decide(Gated("dns_query_name"), existing);

        Assert.Equal(AlertRuleStatus.Gated, decision.Status);
        Assert.Equal(SigmaSyncAction.Changed, decision.Action);
    }

    /// <summary>
    /// Engel kalktığında kural <c>Disabled</c>'a dönüyor, <c>Enabled</c>'a değil.
    ///
    /// <para>
    /// Bir kuralın açılması kullanıcının kararı; engelin kalkması onu
    /// kullanıcının istediği anlamına getirmez. <c>Enabled</c>'a çevirmek,
    /// kullanıcının hiç vermediği bir kararı onun adına vermek olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Engel_kalkinca_kural_Disabled_a_donuyor_Enabled_a_degil()
    {
        var existing = new SigmaRuleSync.SigmaExistingRule(
            AlertRuleStatus.Gated, SigmaRuleSync.GatedFingerprint(Gated("dns_query_name")));

        var decision = SigmaRuleSync.Decide(Written("sha256:cikti"), existing);

        Assert.Equal(AlertRuleStatus.Disabled, decision.Status);
        Assert.Equal(SigmaSyncAction.Changed, decision.Action);
    }

    /// <summary>
    /// Engelsiz bir <c>gated</c> kural manifestin kendi tutarsızlığı ve
    /// <b>görünür kalıyor</b>. Boş bir sebep yazmak onu gizlerdi.
    /// </summary>
    [Fact]
    public void Engelsiz_gated_kural_sessizce_gecmiyor()
    {
        var rule = new SigmaManifestRule
        {
            RuleId = "r3",
            Status = SigmaRuleSync.StatusGated,
            SourceSha = "sha256:kaynak",
        };

        var reason = SigmaRuleSync.DescribeBlockers(rule);

        Assert.Contains("tutarsızlık", reason, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, reason);
    }

    /// <summary>
    /// Gerçek manifest ayrıştırılıyor — kurgu JSON'a değil, üretilen dosyaya karşı.
    ///
    /// <para>
    /// İkisi ayrı: ayrıştırıcı doğru olup manifest biçimi değişmiş olabilir, ve
    /// o zaman senkron sessizce boş bir liste okur. Kurallar 6'nın çıktısı;
    /// biçimi değişirse burası kırmızı yanmalı.
    /// </para>
    /// </summary>
    [Fact]
    public void Gercek_manifest_ayristiriliyor()
    {
        var path = Path.Combine(RepositoryLayout.Root, "detections", "sigma", "manifest.json");

        Assert.True(File.Exists(path), $"manifest bulunamadı: {path}");

        var manifest = SigmaRuleSync.Parse(File.ReadAllText(path));

        Assert.NotEmpty(manifest.Rules);

        foreach (var rule in manifest.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.RuleId), "kural kimliği boş");
            Assert.False(string.IsNullOrWhiteSpace(rule.SourceSha), $"{rule.RuleId}: kaynak özeti boş");

            if (string.Equals(rule.Status, SigmaRuleSync.StatusWritten, StringComparison.Ordinal))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(rule.OutputSha),
                    $"{rule.RuleId}: `written` ama `output_sha` yok — değişiklik ölçütü çalışmaz");
            }

            if (string.Equals(rule.Status, SigmaRuleSync.StatusGated, StringComparison.Ordinal))
            {
                // `gated` bir kuralda `output_sha` OLMAMALI: SQL yok, özeti de yok.
                Assert.Null(rule.OutputSha);
                Assert.NotEmpty(rule.Blockers);
            }
        }
    }

    /// <summary>
    /// <c>failed</c> bir kural durumu değil, <b>bizim build'imizin durumu</b> —
    /// kurallara yazılmıyor.
    /// </summary>
    [Fact]
    public void Failed_bir_kural_durumu_degil()
    {
        var path = Path.Combine(RepositoryLayout.Root, "detections", "sigma", "manifest.json");
        var manifest = SigmaRuleSync.Parse(File.ReadAllText(path));

        Assert.DoesNotContain(
            manifest.Rules,
            r => string.Equals(r.Status, SigmaRuleSync.StatusFailed, StringComparison.Ordinal));
    }
}

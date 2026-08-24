using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Alerting;

/// <summary>Bir senkron koşumunun sonucu — sayılar ve <b>ne değiştiği</b>.</summary>
/// <param name="Created">Yeni inen kural sayısı.</param>
/// <param name="Changed">Ürettiği SQL ya da engeli değişen kural sayısı.</param>
/// <param name="Unchanged">Dokunulmayan kural sayısı.</param>
/// <param name="ChangedRuleIds">
/// Değişenlerin kimlikleri. Sayı tek başına *"bir şey oynadı"* diyor; kullanıcıya
/// gösterilecek olan <b>hangisi</b>.
/// </param>
public sealed record SigmaSyncResult(
    int Created,
    int Changed,
    int Unchanged,
    IReadOnlyList<string> ChangedRuleIds);

/// <summary>
/// Manifest kararlarını <c>alert_rules</c>'a <b>uygulayan</b> taraf (T33).
///
/// <para>
/// Karar <see cref="SigmaRuleSync.Decide"/>'da ve saf; burada yalnızca yazma
/// var. Ayrım bilinçli: karar mantığı veritabanı istemediği için Docker'sız
/// sınanıyor, yazma yolu ise EF InMemory ile.
/// </para>
///
/// <para>
/// <b>Kapsam açıkça veriliyor, türetilmiyor.</b> <c>AlertRuleEntity</c>
/// sınırsız kapsamlı kural kabul etmiyor (§8) ve senkronla inen bir Sigma
/// kuralının doğal bir sahibi yok. Manifestteki <c>logsource</c>'tan türetmek
/// akla geliyor ama <b>vendor ile grup aynı şey değil</b>: bir ekibin FortiGate
/// cihazları başka bir ekibin de FortiGate'i olduğu anlamına gelmiyor.
/// </para>
///
/// <para>
/// Bu yüzden komutu koşturan taraf kimin kuralları olduğunu <b>beyan ediyor</b>.
/// Beyanı zorunlu kılmak, "sınırsız kural yok" değişmezini tek satırda delmeyi
/// engelliyor — ve o değişmez delindiğinde bunu yapan kişi çoğu zaman fark
/// etmiyor.
/// </para>
/// </summary>
public sealed class SigmaRuleSyncService(IDbContextFactory<ControlPlaneDbContext> factory)
{
    /// <summary>
    /// Manifesti kurallara yazar.
    /// </summary>
    /// <param name="manifest">T32'nin ürettiği manifest.</param>
    /// <param name="ownerSubject">Kuralları kaydeden kimlik (denetim için).</param>
    /// <param name="ownerGroups">
    /// Kuralların koşacağı <c>owner_group</c> kümesi. <b>Boş olamaz</b>:
    /// kapsamsız bir kural, "bir ekibin kuralı başka ekibin olaylarını saymıyor"
    /// değişmezini deler.
    /// </param>
    public async Task<SigmaSyncResult> SyncAsync(
        SigmaManifest manifest,
        string ownerSubject,
        IReadOnlyCollection<string> ownerGroups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSubject);
        ArgumentNullException.ThrowIfNull(ownerGroups);

        if (ownerGroups.Count == 0 || ownerGroups.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Kapsam boş olamaz. Sigma kuralları da bir `owner_group` kümesinde koşar; " +
                "kapsamsız bir kural, bir ekibin kuralının başka ekibin olaylarını " +
                "saymamasını güvenceleyen değişmezi deler (§8).",
                nameof(ownerGroups));
        }

        var groups = string.Join(',', ownerGroups.Where(g => !string.IsNullOrWhiteSpace(g)));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var existing = await db.AlertRules
            .Where(r => r.Source == AlertRuleSource.Sigma)
            .ToDictionaryAsync(r => r.SigmaRuleId, cancellationToken);

        var created = 0;
        var changed = 0;
        var unchanged = 0;
        var changedIds = new List<string>();

        foreach (var rule in manifest.Rules)
        {
            // `failed` bir kural durumu DEĞİL, bizim build'imizin durumu:
            // pipeline kırık demek ve CI onu geçirmiyor. Kurallara yazılsaydı
            // kullanıcı bizim hatamızı bir kapsam sınırı sanardı.
            if (string.Equals(rule.Status, SigmaRuleSync.StatusFailed, StringComparison.Ordinal))
            {
                continue;
            }

            existing.TryGetValue(rule.RuleId, out var stored);

            var decision = SigmaRuleSync.Decide(
                rule,
                stored is null
                    ? null
                    : new SigmaRuleSync.SigmaExistingRule(stored.Status, Fingerprint(stored, rule)));

            if (stored is null)
            {
                db.AlertRules.Add(Materialise(rule, decision, ownerSubject, groups));
                created++;
                continue;
            }

            if (decision.Action == SigmaSyncAction.Unchanged)
            {
                unchanged++;
                continue;
            }

            Apply(stored, rule, decision);

            // Damga KALICI: kullanıcı senkron koşarken orada değil ve dönüş
            // değeri koşum bitince kayboluyor.
            stored.SigmaChangedAt = DateTimeOffset.UtcNow;
            changed++;
            changedIds.Add(rule.RuleId);
        }

        // KOŞUMUN KENDİSİ kayda giriyor.
        //
        // CLI çıktısı bir terminalde yaşıyor ve orada ölüyor — `ChangedRuleIds`
        // ile aynı sınıf, bir katman yukarıda. Bir DAĞITIM hareketinin izinin
        // yalnızca terminalde olması, altı ay sonra "bu 269 kural nereden
        // geldi" sorusunun cevapsız kalması demek.
        //
        // Yeni bir tablo açılmıyor: `audit_log` zaten "kim, hangi kapsam, ne
        // yaptı" sorusunun yeri ve ikinci bir kopya yazmak §9'un yasakladığı şey.
        db.AuditLog.Add(new AuditLogEntity
        {
            Subject = ownerSubject,
            Action = "sigma.sync",
            Resource = "alert_rules",
            Scope = groups,
            RowCount = created + changed,
            Details = changedIds.Count == 0
                ? $"yeni {created} · değişti {changed} · aynı {unchanged}"
                : $"yeni {created} · değişti {changed} · aynı {unchanged} · "
                  + $"değişenler: {string.Join(",", changedIds)}",
        });

        await db.SaveChangesAsync(cancellationToken);

        return new SigmaSyncResult(created, changed, unchanged, changedIds);
    }

    /// <summary>
    /// Kayıttaki parmak izi — <b>manifestteki duruma göre</b> okunuyor.
    ///
    /// <para>
    /// Kayıttan hangi alanın okunacağı, kuralın bugünkü durumuna bağlı:
    /// <c>gated</c> bir kuralın <c>SigmaOutputSha</c>'sı boş ve boş dizge her
    /// zaman boş dizgeye eşit olurdu — yani kural sonsuza kadar "değişmemiş"
    /// görünürdü.
    /// </para>
    /// </summary>
    private static string Fingerprint(AlertRuleEntity stored, SigmaManifestRule rule) =>
        string.Equals(rule.Status, SigmaRuleSync.StatusGated, StringComparison.Ordinal)
            ? stored.GatedFingerprint
            : stored.SigmaOutputSha;

    private static AlertRuleEntity Materialise(
        SigmaManifestRule rule,
        SigmaSyncDecision decision,
        string ownerSubject,
        string groups)
    {
        var entity = new AlertRuleEntity
        {
            Name = string.IsNullOrWhiteSpace(rule.Title) ? rule.RuleId : rule.Title,
            Description = rule.SourcePath,
            OwnerSubject = ownerSubject,
            OwnerGroups = groups,
            Source = AlertRuleSource.Sigma,
            SigmaRuleId = rule.RuleId,

            // Sigma kuralının eşiği **1**: bir satır dönerse tetikler.
            //
            // O zaman "kaç satır döndü" ile "kaç kez tetiklenirdi" AYNI sayı,
            // yani T23'ün önizleme sözleşmesi dokunulmadan kalıyor. Eşik
            // yapılandırılabilir ama varsayılan ekranda GÖRÜNÜR: gizlenirse
            // ayrıştıkları an kimse fark etmez ve önizleme sessizce başka bir
            // şey ölçer.
            Threshold = 1,
            Comparison = AlertComparison.GreaterThanOrEqual,
        };

        Apply(entity, rule, decision);

        return entity;
    }

    private static void Apply(
        AlertRuleEntity entity, SigmaManifestRule rule, SigmaSyncDecision decision)
    {
        entity.Status = decision.Status;
        entity.GatedReason = decision.GatedReason;
        entity.SigmaSourceSha = rule.SourceSha;
        entity.SigmaOutputSha = rule.OutputSha ?? string.Empty;
        entity.GatedFingerprint =
            string.Equals(rule.Status, SigmaRuleSync.StatusGated, StringComparison.Ordinal)
                ? SigmaRuleSync.GatedFingerprint(rule)
                : string.Empty;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

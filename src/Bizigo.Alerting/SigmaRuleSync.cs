using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting;

/// <summary>Manifestten okunan tek bir kural (T32 çıktısı, T33 girdisi).</summary>
public sealed record SigmaManifestRule
{
    [JsonPropertyName("rule_id")] public string RuleId { get; init; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    [JsonPropertyName("source_path")] public string SourcePath { get; init; } = string.Empty;

    [JsonPropertyName("source_sha")] public string SourceSha { get; init; } = string.Empty;

    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    /// <summary>
    /// <b>Yalnızca <c>written</c> kurallarda var.</b> <c>gated</c> bir kuralın SQL'i
    /// yok, dolayısıyla özeti de yok — ve olmaması doğru.
    ///
    /// <para>
    /// Koşulsuz okunsaydı <c>gated</c> kurallar ya sonsuza kadar "değişmemiş"
    /// görünürdü (boş dizge hep boş dizgeye eşit) ya alan yokluğundan patlardı.
    /// Bu yüzden değişiklik ölçütü duruma göre ayrışıyor.
    /// </para>
    /// </summary>
    [JsonPropertyName("output_sha")] public string? OutputSha { get; init; }

    [JsonPropertyName("blockers")] public SigmaManifestBlocker[] Blockers { get; init; } = [];
}

/// <summary>Bir kuralın neden koşamadığı — T32'nin <c>remedy</c> sözlüğüyle.</summary>
public sealed record SigmaManifestBlocker
{
    [JsonPropertyName("column")] public string Column { get; init; } = string.Empty;

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("remedy")] public string Remedy { get; init; } = string.Empty;

    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

/// <summary>Manifestin tamamı.</summary>
public sealed record SigmaManifest
{
    [JsonPropertyName("rules")] public SigmaManifestRule[] Rules { get; init; } = [];
}

/// <summary>Bir kural için senkronun ne yapacağı.</summary>
public enum SigmaSyncAction
{
    /// <summary>Yeni kural — kayda eklenecek.</summary>
    Create = 0,

    /// <summary>Var ve değişmemiş — dokunulmayacak.</summary>
    Unchanged = 1,

    /// <summary>Var ve <b>ürettiği SQL değişmiş</b> — kullanıcı görmeli.</summary>
    Changed = 2,
}

/// <summary>Tek bir kural için senkron kararı.</summary>
/// <param name="RuleId">Yukarı akış kimliği.</param>
/// <param name="Action">Ne yapılacağı.</param>
/// <param name="Status">Kuralın alacağı durum.</param>
/// <param name="GatedReason"><c>Gated</c> ise sebebi, değilse boş.</param>
public sealed record SigmaSyncDecision(
    string RuleId,
    SigmaSyncAction Action,
    AlertRuleStatus Status,
    string GatedReason);

/// <summary>
/// <b>Derleme hattının manifestini alarm kurallarına yazan yol</b> (T33).
///
/// <para>
/// Yön bilinçli: manifest <b>dosya</b>, sözleşmesi çivili ve <c>--check</c> ile
/// korunuyor; alarm servisi ise hâlâ değişiyor. Bağımlılığı sabit olan tarafa
/// kurmak, değişen tarafa kurmaktan iyi. Ayrıca kural kaydı alarm tarafının
/// işi, derleme hattının değil.
/// </para>
///
/// <para>
/// <b>Karar ile uygulama ayrı.</b> <see cref="Decide"/> saf: veritabanı
/// istemiyor, dolayısıyla Docker'sız sınanıyor. Aksi hâlde bu mantık yalnızca
/// Testcontainers'lı bir koşumda ölçülebilirdi — yani pratikte hiç.
/// </para>
/// </summary>
public static class SigmaRuleSync
{
    /// <summary>Manifestte <c>written</c> durumunun adı.</summary>
    public const string StatusWritten = "written";

    /// <summary>Manifestte <c>gated</c> durumunun adı.</summary>
    public const string StatusGated = "gated";

    /// <summary>
    /// Manifestte <c>failed</c>: <b>bizim build'imiz kırık</b> demek, bir kural
    /// durumu değil. Sabit sıfır bekleniyor ve CI onu geçirmiyor.
    ///
    /// <para>
    /// Kurallara yazılmıyor: yazılsaydı kullanıcı bizim hatamızı bir kapsam
    /// sınırı sanardı.
    /// </para>
    /// </summary>
    public const string StatusFailed = "failed";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SigmaManifest Parse(string json) =>
        JsonSerializer.Deserialize<SigmaManifest>(json, Json)
        ?? throw new InvalidOperationException("Manifest ayrıştırılamadı.");

    /// <summary>
    /// <c>gated</c> bir kuralın <b>değişiklik parmak izi</b>.
    ///
    /// <para>
    /// <c>output_sha</c> yok, dolayısıyla ölçüt <c>source_sha</c> <b>artı
    /// engeller</b>. Yalnızca kaynağa bakmak yetmezdi: pipeline değişip kural
    /// başka bir kolonda takılmaya başlarsa kaynak aynı kalır ama kuralın neden
    /// koşamadığı değişir — ve kullanıcıya gösterilen sebep sessizce bayatlar.
    /// </para>
    /// </summary>
    public static string GatedFingerprint(SigmaManifestRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var blockers = rule.Blockers
            .Select(b => $"{b.Kind}|{b.Column}|{b.Remedy}")
            .Order(StringComparer.Ordinal);

        return string.Join(";", [rule.SourceSha, .. blockers]);
    }

    /// <summary>
    /// Kullanıcıya gösterilecek sebep — <b>ne yapılacağını</b> söylüyor.
    ///
    /// <para>
    /// Sessiz bir "kapalı" rozeti listeyi çöp kutusuna çevirir: kullanıcı neyin
    /// kapatacağını göremezse liste hiç boşalmaz. <c>remedy</c> ile
    /// <c>column</c> birlikte duruyor çünkü *"şema bekliyor"* tek başına hangi
    /// alanı bekliyor demiyor.
    /// </para>
    /// </summary>
    public static string DescribeBlockers(SigmaManifestRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Blockers.Length == 0)
        {
            // Engelsiz bir `gated` kural manifestin kendi tutarsızlığı. Boş
            // sebep yazmak onu gizlerdi; görünür kalıyor.
            return "Kural koşamıyor ama manifest bir engel bildirmedi — derleme hattında tutarsızlık.";
        }

        return string.Join(
            " · ",
            rule.Blockers.Select(b => $"[{b.Remedy}] {b.Column}: {b.Message}".Trim()));
    }

    /// <summary>
    /// Bir kural için ne yapılacağı — <b>saf</b>, veritabanı istemiyor.
    /// </summary>
    /// <param name="rule">Manifestteki kural.</param>
    /// <param name="existing">
    /// Kayıttaki hâli, yoksa <c>null</c>. Yalnızca üç alan gerekiyor ve o üçü
    /// de manifestten geliyor: durum, çıktı özeti, engel parmak izi.
    /// </param>
    public static SigmaSyncDecision Decide(SigmaManifestRule rule, SigmaExistingRule? existing)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var gated = string.Equals(rule.Status, StatusGated, StringComparison.Ordinal);
        var status = gated ? AlertRuleStatus.Gated : AlertRuleStatus.Disabled;
        var reason = gated ? DescribeBlockers(rule) : string.Empty;

        if (existing is null)
        {
            // Yeni kural `Disabled` iniyor, `Enabled` DEĞİL.
            //
            // Derleme hattı bir kuralı üretebilir ama kullanıcı adına AÇAMAZ.
            // Sigma korpusu 269 kurala çıktığında hepsinin kendiliğinden
            // etkinleşmesi, kullanıcının hiç istemediği bir gürültü seli olurdu.
            return new SigmaSyncDecision(rule.RuleId, SigmaSyncAction.Create, status, reason);
        }

        // Ölçüt duruma göre ayrışıyor ve ayrışması şart:
        //   written → `output_sha` (üretilen SQL değişti mi)
        //   gated   → `source_sha` + engeller (SQL yok, özeti de yok)
        //
        // `written` tarafında ölçüt `source_sha` OLSAYDI, pipeline değiştiğinde
        // kaynak aynı kalır ve kuralın ne yakaladığı sessizce oynardı — bu
        // depoda beş kez ısıran şeklin aynısı.
        var fingerprint = gated ? GatedFingerprint(rule) : rule.OutputSha ?? string.Empty;
        var changed = !string.Equals(fingerprint, existing.Fingerprint, StringComparison.Ordinal);

        // Durum değişikliği de bir değişiklik: `gated` bir kural açıldıysa ya da
        // koşan bir kural `gated`'a düştüyse kullanıcı bunu görmeli.
        changed |= existing.Status != status && existing.Status != AlertRuleStatus.Enabled;
        changed |= gated != (existing.Status == AlertRuleStatus.Gated);

        // Kullanıcının açma/kapama kararı KORUNUYOR — ama `gated` onu eziyor,
        // çünkü koşamayan bir kural açık gösterilemez.
        var resolved = gated
            ? AlertRuleStatus.Gated
            : existing.Status == AlertRuleStatus.Gated
                ? AlertRuleStatus.Disabled
                : existing.Status;

        return new SigmaSyncDecision(
            rule.RuleId,
            changed ? SigmaSyncAction.Changed : SigmaSyncAction.Unchanged,
            resolved,
            reason);
    }

    /// <summary>
    /// Senkronun karar vermek için ihtiyaç duyduğu <b>tek</b> şey.
    ///
    /// <para>
    /// Bütün varlık yerine bu üçlü: karar mantığı EF'e bağımlı olmasın ve
    /// Docker'sız sınanabilsin diye.
    /// </para>
    /// </summary>
    /// <param name="Status">Kayıttaki durum — kullanıcının kararını taşıyor.</param>
    /// <param name="Fingerprint">
    /// Kayıttaki parmak izi: <c>written</c> için <c>output_sha</c>, <c>gated</c>
    /// için <see cref="GatedFingerprint"/>.
    /// </param>
    public sealed record SigmaExistingRule(AlertRuleStatus Status, string Fingerprint);
}

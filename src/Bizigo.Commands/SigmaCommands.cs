using Bizigo.Alerting;

namespace Bizigo.Commands;

/// <param name="ManifestPath">Okunan manifest.</param>
/// <param name="TotalRules">Manifestteki kural sayısı — <b>plandan ayrı</b>.</param>
/// <param name="Decisions">Senkronun ne yapacağı.</param>
public sealed record SigmaPlanOutcome(
    string ManifestPath,
    int TotalRules,
    IReadOnlyList<SigmaSyncDecision> Decisions);

/// <summary>
/// Sigma komutlarının çekirdeği.
///
/// <para>
/// <b>M02'de ikiye bölündü ve bu bir komut bölmek değil, zaten ayrı olan saf
/// yarıyı ilan etmek.</b> <c>SigmaSyncCommandHandler.Plan</c> bu depoda
/// <b>zaten</b> saftı: veritabanına dokunmuyor ve değer döndürüyordu. Ölçüm
/// önerilen şeklin bu depodaki tek örneğini orada buldu; M02 onu genelledi ve
/// ilan etti.
/// </para>
///
/// <para>
/// <c>sigma.plan</c> araç (okuma), <c>sigma.sync</c> muaf (yazma). Muafiyet
/// kabiliyeti değil <b>yalnızca yazmayı</b> kapsıyor.
/// </para>
/// </summary>
public static class SigmaCommands
{
    /// <summary>Manifestin varsayılan yeri — T32'nin ürettiği dosya.</summary>
    public const string DefaultManifest = "detections/sigma/manifest.json";

    /// <summary>
    /// Manifesti okur ve <b>üç ayrı arızayı ayrı ayrı</b> adlandırır.
    ///
    /// <para>
    /// <b>Tek yükleyici, iki tüketici.</b> <c>sigma plan</c> ve <c>sigma sync</c>
    /// aynı manifesti aynı kurallarla okuyor; ikinci bir yükleyici yazmak, biri
    /// düzeltilip diğeri eski kalınca *"plan ne diyorsa sync onu yapar"*
    /// iddiasını sessizce yanlış yapardı.
    /// </para>
    /// </summary>
    public static async Task<CommandOutcome<SigmaManifest>> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            // Yol yanlışsa SESSİZCE SIFIR KURAL senkronlamak "senkron koştu"
            // diye okunur ve kimse manifeste bakmaz.
            return CommandOutcome<SigmaManifest>.Failed(
                CommandFailureKind.NotFound,
                $"Manifest bulunamadı: {manifestPath}. " +
                "Önce `python -m sigma_build.compile --write` koşturun.");
        }

        SigmaManifest manifest;

        try
        {
            manifest = SigmaRuleSync.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOutcome<SigmaManifest>.Failed(
                CommandFailureKind.InvalidArgument,
                $"Manifest ayrıştırılamadı: {exception.Message}. " +
                "Biçim derleme hattının (T32); değiştiyse senkron güncellenmeli.");
        }

        if (manifest.Rules.Length == 0)
        {
            // Boş manifest bir CEVAP değil bir ARIZA: sıfır kural senkronlamak
            // "kural yok" diye okunur, oysa derleme hattı hiç koşmamış olabilir.
            return CommandOutcome<SigmaManifest>.Failed(
                CommandFailureKind.InvalidArgument,
                $"Manifest BOŞ: {manifestPath}. Derleme hattı koşmamış olabilir; " +
                "sıfır kural senkronlamak 'kural yok' diye okunur.");
        }

        return CommandOutcome<SigmaManifest>.Success(manifest);
    }

    /// <summary>
    /// <b>Hiçbir şey yazmadan</b> manifestin ne getireceğini söyler.
    ///
    /// <para>
    /// Var olan kayıt bilinmediği için her kural "yeni" sayılıyor ve bu
    /// <b>bilerek</b>: planın cevapladığı soru <i>"manifest ne getiriyor"</i>,
    /// <i>"ne değişecek"</i> değil. İkisini karıştırmak, planı gerçek koşumun
    /// tahmini gibi göstermek olurdu.
    /// </para>
    /// </summary>
    public static async Task<CommandOutcome<SigmaPlanOutcome>> PlanAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        if (!loaded.Ok)
        {
            return CommandOutcome<SigmaPlanOutcome>.Failed(loaded.Failure.Kind, loaded.Failure.Message);
        }

        return CommandOutcome<SigmaPlanOutcome>.Success(new SigmaPlanOutcome(
            manifestPath,
            loaded.Payload.Rules.Length,
            Plan(loaded.Payload)));
    }

    /// <summary>
    /// Saf plan — <b>M02 öncesinde de saftı</b> ve genellemenin modeli bu
    /// fonksiyondu.
    /// </summary>
    public static IReadOnlyList<SigmaSyncDecision> Plan(SigmaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return
        [
            .. manifest.Rules
                .Where(r => !string.Equals(r.Status, SigmaRuleSync.StatusFailed, StringComparison.Ordinal))
                .Select(r => SigmaRuleSync.Decide(r, existing: null)),
        ];
    }
}

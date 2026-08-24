using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo sigma sync</c> — derleme hattının manifestini alarm kurallarına yazar.
///
/// <para>
/// <b>Neden CLI, API ucu değil.</b> Senkron bir <b>dağıtım</b> hareketi: kural
/// seti çivili bir commit'ten geliyor ve manifest üretilmiş bir dosya. Bir
/// kullanıcının ürün içinden tetiklemesi gereken bir şey değil.
/// </para>
///
/// <para>
/// Kapsam beyanı da burada doğal duruyor. API ucu olsaydı *"kim hangi kapsamla
/// tetikliyor"* sorusunu her çağrıda yeniden sormak gerekirdi ve o soru,
/// cevabı unutulduğunda sessizce yanlış kapsamlı kurallar üretirdi.
/// </para>
///
/// <para>
/// Karar geri alınabilirlik üzerinden verildi: CLI'yı bir gün bir uçla
/// sarmalamak kolay, ters yön değil.
/// </para>
/// </summary>
public static class SigmaSyncCommandHandler
{
    /// <summary>Manifestin varsayılan yeri — T32'nin ürettiği dosya.</summary>
    public const string DefaultManifest = "detections/sigma/manifest.json";

    public static async Task<int> RunAsync(
        string manifestPath,
        string ownerSubject,
        string[] ownerGroups,
        string? connectionString,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            // Yol yanlışsa **sessizce sıfır kural** senkronlamak, "senkron
            // koştu" diye okunur ve kimse manifeste bakmaz.
            await Console.Error.WriteLineAsync(
                $"Manifest bulunamadı: {manifestPath}\n" +
                "Önce `python -m sigma_build.compile --write` koşturun.").ConfigureAwait(false);
            return 2;
        }

        SigmaManifest manifest;

        try
        {
            manifest = SigmaRuleSync.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Manifest ayrıştırılamadı: {exception.Message}\n" +
                "Biçim derleme hattının (T32); değiştiyse senkron güncellenmeli.")
                .ConfigureAwait(false);
            return 2;
        }

        if (manifest.Rules.Length == 0)
        {
            // Boş manifest bir CEVAP değil bir arıza: sıfır kural senkronlamak
            // "kural yok" diye okunur, oysa derleme hattı hiç koşmamış olabilir.
            await Console.Error.WriteLineAsync(
                $"Manifest BOŞ: {manifestPath}. Derleme hattı koşmamış olabilir; "
                + "sıfır kural senkronlamak 'kural yok' diye okunur.").ConfigureAwait(false);
            return 3;
        }

        if (dryRun)
        {
            Report(Plan(manifest), manifest.Rules.Length, applied: false);
            return 0;
        }

        var connection = connectionString
            ?? Environment.GetEnvironmentVariable("BIZIGO_CONTROLPLANE");

        if (string.IsNullOrWhiteSpace(connection))
        {
            await Console.Error.WriteLineAsync(
                "Veritabanı adresi yok: `--connection` verin ya da `BIZIGO_CONTROLPLANE` "
                + "ortam değişkenini kurun.").ConfigureAwait(false);
            return 2;
        }

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connection)
            .Options;

        // Tek koşumluk bir komut: havuz gerekmiyor, basit bir fabrika yetiyor.
        var service = new SigmaRuleSyncService(new SingleContextFactory(options));

        SigmaSyncResult result;

        try
        {
            result = await service
                .SyncAsync(manifest, ownerSubject, ownerGroups, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        Console.WriteLine(
            $"✓ {result.Created} yeni · {result.Changed} değişti · {result.Unchanged} aynı");

        if (result.ChangedRuleIds.Count > 0)
        {
            // Sayı "bir şey oynadı" diyor; kullanıcıya gösterilecek olan HANGİSİ.
            Console.WriteLine("\nÜrettiği SQL ya da engeli değişenler:");

            foreach (var id in result.ChangedRuleIds)
            {
                Console.WriteLine($"  {id}");
            }
        }

        if (result.Created > 0)
        {
            // Yeni kuralların KAPALI indiği açıkça söyleniyor: sessiz kalsaydı
            // operatör senkronun kuralları açtığını sanabilir ve alarm
            // beklerken hiçbir şey gelmezdi.
            Console.WriteLine(
                $"\n{result.Created} yeni kural PASİF indi — derleme hattı kuralı üretir, "
                + "kullanıcı adına AÇMAZ.");
        }

        return 0;
    }

    /// <summary>
    /// Kuru koşum planı — <b>veritabanına dokunmadan</b>.
    ///
    /// <para>
    /// Var olan kayıt bilinmediği için her kural "yeni" sayılıyor ve bu
    /// **bilerek**: kuru koşumun cevapladığı soru *"manifest ne getiriyor"*,
    /// *"ne değişecek"* değil. İkisini karıştırmak, kuru koşumu gerçek
    /// koşumun tahmini gibi göstermek olurdu.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SigmaSyncDecision> Plan(SigmaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.Rules
            .Where(r => !string.Equals(r.Status, SigmaRuleSync.StatusFailed, StringComparison.Ordinal))
            .Select(r => SigmaRuleSync.Decide(r, existing: null))
            .ToList();
    }

    /// <summary>
    /// Tek koşumluk komutun fabrikası. Havuz kurmak, ömrü saniyelerle ölçülen
    /// bir süreçte kazanç sağlamıyor.
    /// </summary>
    private sealed class SingleContextFactory(DbContextOptions<ControlPlaneDbContext> options)
        : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() => new(options);
    }

    private static void Report(IReadOnlyList<SigmaSyncDecision> plan, int total, bool applied)
    {
        var gated = plan.Count(d => d.Status == AlertRuleStatus.Gated);

        Console.WriteLine(
            $"KURU KOŞUM — hiçbir şey yazılmadı.\n"
            + $"  manifest {total} kural · {plan.Count} senkronlanacak · {gated} koşamaz\n"
            + $"  {plan.Count - gated} kural PASİF inecek (kullanıcı açacak)");

        if (total != plan.Count)
        {
            // `failed` atlanıyor ve atlandığı SÖYLENİYOR: sessiz bir fark,
            // "manifest 24 dedi, senkron 21 yazdı" sorusunu cevapsız bırakır.
            Console.WriteLine(
                $"  {total - plan.Count} kural atlandı (`failed`) — bu bir kural durumu "
                + "değil, derleme hattının kırık olduğunu söyler.");
        }
    }
}

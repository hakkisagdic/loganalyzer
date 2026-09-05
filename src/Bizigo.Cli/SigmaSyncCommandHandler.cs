using Bizigo.Alerting;
using Bizigo.Commands;
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
    /// <summary>
    /// <c>bizigo sigma plan</c> — <b>hiçbir şey yazmadan</b> manifestin ne
    /// getireceğini çizer.
    ///
    /// <para>
    /// M02'de <c>--dry-run</c> bayrağından kendi komutuna çıktı. Bayrak olarak
    /// kalsaydı MCP tarafında ilan edilebilecek tek şey <b>yazan</b> komut
    /// olurdu; okuma yarısı zaten saftı ve ilan edilmemesi için sebep yoktu.
    /// </para>
    /// </summary>
    public static async Task<int> PlanAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SigmaCommands.PlanAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            await Console.Error.WriteLineAsync(outcome.Failure.Message).ConfigureAwait(false);
            return outcome.Failure.Kind == CommandFailureKind.NotFound ? 2 : 3;
        }

        Report(outcome.Payload.Decisions, outcome.Payload.TotalRules);
        return 0;
    }

    public static async Task<int> RunAsync(
        string manifestPath,
        string ownerSubject,
        string[] ownerGroups,
        string? connectionString,
        CancellationToken cancellationToken = default)
    {
        // Manifest yükleme ÇEKİRDEKTE ve `sigma plan` ile aynı yerden geliyor.
        // İkinci bir yükleyici, biri düzeltilip diğeri eski kalınca "plan ne
        // diyorsa sync onu yapar" iddiasını sessizce yanlış yapardı.
        var loaded = await SigmaCommands.LoadManifestAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);

        if (!loaded.Ok)
        {
            await Console.Error.WriteLineAsync(loaded.Failure.Message).ConfigureAwait(false);
            return loaded.Failure.Kind == CommandFailureKind.NotFound ? 2 : 3;
        }

        var manifest = loaded.Payload;

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
    /// Tek koşumluk komutun fabrikası. Havuz kurmak, ömrü saniyelerle ölçülen
    /// bir süreçte kazanç sağlamıyor.
    /// </summary>
    private sealed class SingleContextFactory(DbContextOptions<ControlPlaneDbContext> options)
        : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() => new(options);
    }

    private static void Report(IReadOnlyList<SigmaSyncDecision> plan, int total)
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

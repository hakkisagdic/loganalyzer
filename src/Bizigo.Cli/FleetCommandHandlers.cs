using Bizigo.ControlPlane;
using Bizigo.Simulators;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo fleet apply</c> — filo tanımını kontrol düzlemine yazar (S05).
///
/// <para>
/// <b>Var olma sebebi bir silme.</b> Uçtan uca harness bugüne kadar kapsam
/// eşlemesini ve kaynak envanterini <b>elle SQL</b> ile kuruyordu; yani ekran
/// görüntüsü koşumunun gördüğü envanter, ürünün ürettiği envanter değildi.
/// Aradaki fark sessiz: ekran dolu görünür ve doldurabilme iddiası hiç
/// sınanmamış olur.
/// </para>
///
/// <para>
/// <b>Neden ClickHouse'tan türetmiyor:</b> eski <c>seedInventory</c> envanteri
/// olay tablosundan çıkarıyordu — yani "hangi kaynaklar var" sorusunu
/// <i>gelmiş veriden</i> cevaplıyordu. O yol, veri gelmeyen bir kaynağı
/// envantere hiç yazmıyor ve <b>sessizlik alarmının</b> sınanmasını imkânsız
/// kılıyor: susan cihaz envanterde yoksa sustuğu görülemez. Filo dosyası
/// "hangi cihazlar var" sorusunun <b>bağımsız</b> cevabı.
/// </para>
/// </summary>
public static class FleetCommandHandlers
{
    /// <summary>
    /// Filoyu uygular: IdP eşlemeleri ve kaynak envanteri.
    /// </summary>
    /// <returns>Çıkış kodu — sıfırdan farklıysa harness durmalı.</returns>
    public static async Task<int> ApplyAsync(
        string profileDirectory,
        string repositoryRoot,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var loaded = FleetStore.Load(profileDirectory, repositoryRoot);

        // Kısmen geçerli bir filo UYGULANMIYOR. Uygulansaydı kapsam yayılımı
        // yarım kurulur ve ekran "kapsam çalışıyor" görüntüsü verirken aslında
        // eksik veri gösterirdi — yarım kapsam, kapsamsızlıktan tehlikeli.
        if (loaded.Errors.Count > 0)
        {
            Console.Error.WriteLine("Filo tanımı geçersiz:");

            foreach (var error in loaded.Errors)
            {
                Console.Error.WriteLine($"  · {error}");
            }

            return 1;
        }

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        ControlPlaneServiceCollectionExtensions.Configure(options, connectionString);

        await using var db = new ControlPlaneDbContext(options.Options);

        var profiles = FleetStore.Profiles(loaded.Fleet, profileDirectory, repositoryRoot);

        await ApplyMappingsAsync(db, loaded.Fleet, cancellationToken);
        await ApplySourcesAsync(db, profiles, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        Console.WriteLine(
            $"filo uygulandı: {loaded.Fleet.IdpMappings.Count} eşleme, {profiles.Count} kaynak, " +
            $"{profiles.Select(p => p.OwnerGroup).Distinct(StringComparer.Ordinal).Count()} grup");

        return 0;
    }

    private static async Task ApplyMappingsAsync(
        ControlPlaneDbContext db,
        FleetDefinition fleet,
        CancellationToken cancellationToken)
    {
        foreach (var mapping in fleet.IdpMappings)
        {
            var existing = await db.IdpGroupMappings
                .FirstOrDefaultAsync(m => m.IdpGroup == mapping.IdpGroup, cancellationToken);

            if (existing is null)
            {
                db.IdpGroupMappings.Add(new IdpGroupMappingEntity
                {
                    IdpGroup = mapping.IdpGroup,
                    OwnerGroup = mapping.OwnerGroup,
                    Note = mapping.Note,
                });

                continue;
            }

            existing.OwnerGroup = mapping.OwnerGroup;
            existing.Note = mapping.Note;
        }
    }

    private static async Task ApplySourcesAsync(
        ControlPlaneDbContext db,
        IReadOnlyList<SimulatorProfile> profiles,
        CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            var existing = await db.Sources
                .FirstOrDefaultAsync(s => s.SourceId == profile.Id, cancellationToken);

            if (existing is null)
            {
                db.Sources.Add(new SourceEntity
                {
                    SourceId = profile.Id,
                    Hostname = profile.Hostname,
                    OwnerGroup = profile.OwnerGroup,
                    Vendor = profile.Vendor,
                    Product = profile.Product,

                    // Bağsız kaynak MEŞRU: `lb-web-01`'in `parser_id`'si yok ve
                    // bu, bağlama oranının anlamlı olmasının sebebi (FS §6).
                    // Boş bırakmak yerine uydurma bir parser yazmak, oranı
                    // ölçülemez yapardı.
                    ParserId = profile.ParserId ?? string.Empty,
                    Encoding = profile.Encoding,
                    SourceClass = "simulator",
                    Enabled = true,
                });

                continue;
            }

            existing.Hostname = profile.Hostname;
            existing.OwnerGroup = profile.OwnerGroup;
            existing.Vendor = profile.Vendor;
            existing.Product = profile.Product;
            existing.ParserId = profile.ParserId ?? string.Empty;
            existing.Encoding = profile.Encoding;
        }
    }
}

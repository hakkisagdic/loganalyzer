using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

/// <summary>
/// Kontrol düzlemi (K23): envanter, parser kataloğu, IdP grup eşlemesi, ham arşiv
/// manifesti, audit. Değişken (mutable) operasyonel durum burada durur — ClickHouse'ta
/// değil.
/// </summary>
public class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
    : DbContext(options)
{
    public const string MigrationsHistoryTable = "__bizigo_migrations";
    public const string Schema = "bizigo";

    public DbSet<SourceEntity> Sources => Set<SourceEntity>();
    public DbSet<IdpGroupMappingEntity> IdpGroupMappings => Set<IdpGroupMappingEntity>();
    public DbSet<ParserEntity> Parsers => Set<ParserEntity>();
    public DbSet<RawManifestEntity> RawManifest => Set<RawManifestEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();
    public DbSet<ChangeWebhookDeliveryEntity> ChangeWebhookDeliveries => Set<ChangeWebhookDeliveryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SourceEntity>(e =>
        {
            // Dispatcher kademe 1 bu iki alandan eşliyor; ikisi de tekil olmalı.
            e.HasIndex(x => x.PeerAddress).IsUnique().HasFilter("peer_address IS NOT NULL");
            e.HasIndex(x => x.Hostname);
            e.HasIndex(x => x.OwnerGroup);
        });

        modelBuilder.Entity<ParserEntity>(e =>
        {
            e.HasIndex(x => new { x.ParserId, x.Version }).IsUnique();
            e.HasIndex(x => x.State);
        });

        modelBuilder.Entity<RawManifestEntity>(e =>
        {
            // Replay aralık sorgusu: "şu zaman diliminde hangi nesneler var".
            e.HasIndex(x => new { x.OwnerGroup, x.TsFrom });
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.VerifiedAt);
            // "Bu segment yüklendi mi" ve "saklama süresi doldu mu" sorgusu.
            e.HasIndex(x => x.WalSegment);
        });

        modelBuilder.Entity<ChangeWebhookDeliveryEntity>(e =>
        {
            // Saklama temizliği ("30 günden eski teslimat kaydını sil") ve
            // "bu uçtan son ne geldi" sorusu bu indeksten geçiyor.
            e.HasIndex(x => new { x.EndpointId, x.ReceivedAt });
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.Subject, x.At });
        });
    }
}

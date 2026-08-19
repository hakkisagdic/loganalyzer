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

    // Alarm motoru ve bildirim kanalları (T21, T22).
    public DbSet<AlertRuleEntity> AlertRules => Set<AlertRuleEntity>();
    public DbSet<AlertTriggerEntity> AlertTriggers => Set<AlertTriggerEntity>();
    public DbSet<MaintenanceWindowEntity> MaintenanceWindows => Set<MaintenanceWindowEntity>();
    public DbSet<NotificationChannelEntity> NotificationChannels => Set<NotificationChannelEntity>();
    public DbSet<AlertRuleChannelEntity> AlertRuleChannels => Set<AlertRuleChannelEntity>();
    public DbSet<NotificationDeliveryEntity> NotificationDeliveries => Set<NotificationDeliveryEntity>();

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

        modelBuilder.Entity<AlertRuleEntity>(e =>
        {
            // Zamanlayıcının tek sorgusu: "vadesi gelmiş etkin kurallar".
            // Kural sayısı arttığında bu sorgunun maliyeti sabit kalmalı (K16).
            e.HasIndex(x => new { x.Enabled, x.NextRunAt });
        });

        modelBuilder.Entity<AlertTriggerEntity>(e =>
        {
            e.HasIndex(x => new { x.RuleId, x.FiredAt });
            e.HasIndex(x => x.FiredAt);
        });

        modelBuilder.Entity<MaintenanceWindowEntity>(e =>
        {
            // "Şu anda açık pencere var mı" sorgusu.
            e.HasIndex(x => new { x.OwnerGroup, x.StartsAt, x.EndsAt });
            e.HasIndex(x => x.RuleId);
        });

        modelBuilder.Entity<NotificationChannelEntity>(e =>
        {
            e.HasIndex(x => x.OwnerGroup);
            e.HasIndex(x => new { x.Name, x.OwnerGroup }).IsUnique();
        });

        modelBuilder.Entity<AlertRuleChannelEntity>(e =>
        {
            e.HasKey(x => new { x.RuleId, x.ChannelId });
            e.HasIndex(x => x.ChannelId);
        });

        modelBuilder.Entity<NotificationDeliveryEntity>(e =>
        {
            // Gönderici turunun tek sorgusu: "vadesi gelmiş bekleyen teslimler".
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasIndex(x => x.TriggerId);
        });
    }
}

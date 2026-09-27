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
    public DbSet<SourceOwnershipHistoryEntity> SourceOwnershipHistory => Set<SourceOwnershipHistoryEntity>();
    public DbSet<TelemetryOwnerClaimEntity> TelemetryOwnerClaims => Set<TelemetryOwnerClaimEntity>();
    public TimeProvider HistoryClock { get; set; } = TimeProvider.System;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        var changed = ChangeTracker.Entries<SourceEntity>().Where(e => e.State == EntityState.Added
            || e.State == EntityState.Deleted || (e.State == EntityState.Modified && new[]
                { nameof(SourceEntity.OwnerGroup), nameof(SourceEntity.Hostname), nameof(SourceEntity.PeerAddress), nameof(SourceEntity.Enabled) }
                .Any(p => e.Property(p).IsModified))).ToArray();
        if (changed.Length == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        var ownTransaction = Database.IsRelational() && Database.CurrentTransaction is null;
        await using var transaction = ownTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            if (Database.IsNpgsql())
                await Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(735031)", cancellationToken);
            var sourceIds = changed.Select(e => e.Entity.SourceId).ToArray();
            var current = await SourceOwnershipHistory.Where(h => sourceIds.Contains(h.SourceId) && h.EffectiveToNano == null)
                .ToArrayAsync(cancellationToken);
            var now = (decimal)(HistoryClock.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
            // PostgreSQL timestamps have microsecond granularity; stored nanos
            // retain an exact half-open boundary even for two immediate writes.
            now = decimal.Floor(now / 1000) * 1000;
            if (current.Length != 0) now = Math.Max(now, current.Max(h => h.EffectiveFromNano) + 1000);
            // Close the old rows before inserting their successors. EF's entity
            // ordering does not encode a filtered unique index dependency.
            // Both commands remain under the source transaction and advisory lock.
            if (Database.IsNpgsql())
            {
                foreach (var prior in current)
                {
                    await Database.ExecuteSqlInterpolatedAsync($"UPDATE bizigo.source_ownership_history SET effective_to_nano = {now} WHERE revision = {prior.Revision}", cancellationToken);
                    Entry(prior).State = EntityState.Detached;
                }
            }
            foreach (var entry in changed)
            {
                if (!Database.IsNpgsql())
                    foreach (var prior in current.Where(h => h.SourceId == entry.Entity.SourceId)) prior.EffectiveToNano = now;
                var source = entry.Entity;
                source.UpdatedAt = new DateTimeOffset(checked((long)(now / 100)) + DateTimeOffset.UnixEpoch.UtcTicks, TimeSpan.Zero);
                SourceOwnershipHistory.Add(new()
                {
                    SourceId = source.SourceId, OwnerGroup = source.OwnerGroup,
                    PeerAddress = source.PeerAddress, Hostname = source.Hostname,
                    Enabled = entry.State != EntityState.Deleted && source.Enabled, EffectiveFromNano = now,
                });
            }
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    public DbSet<IdpGroupMappingEntity> IdpGroupMappings => Set<IdpGroupMappingEntity>();
    public DbSet<ParserEntity> Parsers => Set<ParserEntity>();
    public DbSet<RawManifestEntity> RawManifest => Set<RawManifestEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();
    public DbSet<ChangeWebhookDeliveryEntity> ChangeWebhookDeliveries => Set<ChangeWebhookDeliveryEntity>();

    // Değişiklik connector'ları ve çalışma geçmişi (T25).
    public DbSet<ChangeConnectorEntity> ChangeConnectors => Set<ChangeConnectorEntity>();
    public DbSet<ChangeConnectorRunEntity> ChangeConnectorRuns => Set<ChangeConnectorRunEntity>();
    public DbSet<ChangeConfigSnapshotEntity> ChangeConfigSnapshots => Set<ChangeConfigSnapshotEntity>();

    // Alarm motoru ve bildirim kanalları (T21, T22).
    public DbSet<AlertRuleEntity> AlertRules => Set<AlertRuleEntity>();
    public DbSet<AlertTriggerEntity> AlertTriggers => Set<AlertTriggerEntity>();
    public DbSet<MaintenanceWindowEntity> MaintenanceWindows => Set<MaintenanceWindowEntity>();
    public DbSet<NotificationChannelEntity> NotificationChannels => Set<NotificationChannelEntity>();
    public DbSet<AlertRuleChannelEntity> AlertRuleChannels => Set<AlertRuleChannelEntity>();
    public DbSet<NotificationDeliveryEntity> NotificationDeliveries => Set<NotificationDeliveryEntity>();

    // RCA kanıt paketleri (T36). Saklanıyorlar ki F4 aynı kanıt üzerinde farklı
    // model koşturup karşılaştırabilsin.
    public DbSet<EvidenceBundleEntity> EvidenceBundles => Set<EvidenceBundleEntity>();

    public DbSet<GoldenReviewEntity> GoldenReviews => Set<GoldenReviewEntity>();

    // RCA tetikleyicileri ve koşum soyağacı (T45).
    public DbSet<RcaRunEntity> RcaRuns => Set<RcaRunEntity>();

    /// <summary>
    /// Üretilen RCA belgeleri (T51). <b>Statü taşımıyor</b> — o
    /// <see cref="RcaRuns"/>'ın.
    /// </summary>
    public DbSet<RcaReportEntity> RcaReports => Set<RcaReportEntity>();

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
            e.Property(x => x.UpdatedAt).IsConcurrencyToken();
        });

        modelBuilder.Entity<SourceOwnershipHistoryEntity>(e =>
        {
            e.Property(x => x.EffectiveFromNano).HasPrecision(20, 0);
            e.Property(x => x.EffectiveToNano).HasPrecision(20, 0);
            e.HasIndex(x => new { x.SourceId, x.EffectiveFromNano }).IsUnique();
            e.HasIndex(x => x.SourceId).IsUnique().HasFilter("effective_to_nano IS NULL");
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

        modelBuilder.Entity<ChangeConnectorEntity>(e =>
        {
            // Webhook yolu bu alandan çözülüyor; iki connector aynı slug'ı
            // alırsa hangi grubun aldığı çağrıya göre değişirdi.
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.OwnerGroup);

            // Zamanlayıcının tek sorgusu: "vadesi gelmiş etkin connector'lar".
            e.HasIndex(x => new { x.Enabled, x.NextRunAt });
        });

        modelBuilder.Entity<ChangeConnectorRunEntity>(e =>
        {
            // "Bu connector'ın son N koşusu" ve saklama temizliği.
            e.HasIndex(x => new { x.ConnectorId, x.StartedAt });
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<ChangeConfigSnapshotEntity>(e =>
        {
            // Fark almanın tek sorgusu: "bu connector'ın EN SON anlık görüntüsü".
            e.HasIndex(x => new { x.ConnectorId, x.CapturedAt });
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
            // İndeks `Status` üzerinde: zamanlayıcı `Status == Enabled` filtreliyor.
            // 269 Sigma kuralı açıldığında bu sorgunun maliyeti sabit kalmalı (K16).
            e.HasIndex(x => new { x.Status, x.NextRunAt });

            // Sigma kuralının yukarı akış kimliği tekil: aynı kural iki kez
            // kaydedilirse derleme hattı hangisini güncelleyeceğini bilemez ve
            // biri sessizce bayat kalır.
            e.HasIndex(x => x.SigmaRuleId)
                .HasFilter(null)
                .IsUnique(false);
        });

        modelBuilder.Entity<AlertTriggerEntity>(e =>
        {
            e.HasIndex(x => new { x.RuleId, x.FiredAt });
            e.HasIndex(x => x.FiredAt);

            // "Bakılmayı bekleyen alarmlar" (T38). Durum iki değerli, yani tek
            // başına seçici değil — zamanla birlikte indeksleniyor ki açık
            // olanların en yenisi sabit maliyetle gelsin.
            e.HasIndex(x => new { x.State, x.FiredAt });
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

        modelBuilder.Entity<RcaRunEntity>(e =>
        {
            // Debounce sorgusu: "bu anahtar bu pencerede kabul edildi mi".
            // Yalnızca KABUL edilenler aranıyor, o yüzden `Accepted` de anahtarda.
            e.HasIndex(x => new { x.DebounceKey, x.Accepted, x.RequestedAt });

            // Soyağacı yürüyüşü: kökten başlayıp ata zincirini okumak.
            e.HasIndex(x => new { x.RootRunId, x.Depth });
            e.HasIndex(x => x.ParentRunId);

            // Idempotency: aynı anahtar ikinci kez gelirse aynı koşum dönmeli.
            // TEKİL ve filtreli — anahtar yalnızca dış API koşumlarında dolu ve
            // `null`'lar birbiriyle çakışmamalı.
            e.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");

            // "Neden RCA üretilmedi" ekranının sorgusu: sebebe göre sayım.
            e.HasIndex(x => new { x.Rejection, x.RequestedAt });
        });

        modelBuilder.Entity<EvidenceBundleEntity>(e =>
        {
            // "Son toplanan paketler" — liste ekranının tek sorgusu (T37).
            e.HasIndex(x => x.GatheredAt);

            // "Bu kanıt daha önce toplanmış mı" — F4'ün karşılaştırma akışı aynı
            // pencereyi tekrar toplamadan önce buna bakıyor. Tekil DEĞİL: aynı
            // pencerenin iki kez toplanması meşru ve ikisi de saklanmalı.
            e.HasIndex(x => x.ContentHash);

            // "Şu zaman aralığına ait paketler".
            e.HasIndex(x => new { x.WindowFrom, x.WindowTo });
        });

        modelBuilder.Entity<GoldenReviewEntity>(e =>
        {
            // Kalite göstergesinin tek sorgusu: kapsam altında karar dağılımı.
            // Grup önde çünkü filtre daima grupla başlıyor (K17) — kapsamsız
            // gösterge diye bir şey yok.
            e.HasIndex(x => new { x.OwnerGroup, x.ReviewedAt });

            // Bir paketin incelemesi var mı. Tekil DEĞİL: aynı paket için iki
            // kişinin ayrı kararı meşru bir veri ve F4'ün ölçmek isteyeceği bir
            // şey — insanlar birbiriyle ne kadar anlaşıyor.
            e.HasIndex(x => x.BundleId);

            // Tetiklenmeden incelemeye gidiş; kapalı alarmın kaydını açar.
            e.HasIndex(x => x.TriggerId);
        });

        modelBuilder.Entity<RcaReportEntity>(e =>
        {
            // Ekranın tek sorgusu: "bu paketin son raporu". Tekil DEĞİL —
            // aynı paket üzerinde farklı model/prompt koşturmak F4'ün
            // karşılaştırma akışının kendisi ve hepsi saklanmalı.
            e.HasIndex(x => new { x.BundleId, x.CreatedAt });

            // T47'nin sorgusu: "şu senaryonun raporlarında atılan cümle oranı
            // ne". Senaryo önde, çünkü karşılaştırma daima tek senaryo içinde
            // anlamlı — iki farklı senaryonun oranını toplamak, iki farklı
            // soruyu tek sayıya indirmek olurdu.
            e.HasIndex(x => new { x.ScenarioId, x.CreatedAt });
        });
    }
}

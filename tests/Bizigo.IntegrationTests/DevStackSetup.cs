using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Entegrasyon testlerinin <b>ortak kurulum yüzeyi</b>.
///
/// <para>
/// Depo kökünü bulan yardımcı üç ayrı test sınıfında birebir tekrarlanıyordu ve
/// ClickHouse/Postgres/S3 kurulumları ikişer kez. Dördüncüsünü yazmak, bu
/// deponun bedelini defalarca ödediği kalıbın test tarafındaki hâli olurdu:
/// aynı iddia birden çok yerde kodlanınca, ayrıştıkları gün hangisinin doğru
/// olduğu bilinemez hâle geliyor.
/// </para>
///
/// <para>
/// Burada <b>kurulum</b> var, iddia yok. Testler ne kurduklarını değil ne
/// sınadıklarını anlatmalı; kurulumun kendisi bir teste ait olmadığı için de
/// hiçbir testin içinde durmamalı.
/// </para>
/// </summary>
public static class DevStackSetup
{
    /// <summary>
    /// <c>Bizigo.sln</c>'i barındıran dizin.
    ///
    /// <para>Test ikilisi <c>bin/Debug/net10.0</c> altında koşuyor; katalog,
    /// göç dosyaları ve pattern kütüphanesi depo kökünden göreli. Yolu ortam
    /// değişkenine bağlamak, CI ile yerelin ayrışabileceği bir yer daha
    /// açardı.</para>
    /// </summary>
    public static string RepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı: Bizigo.sln hiçbir üst dizinde yok."),
            relative);
    }

    /// <summary>
    /// İzole bir ClickHouse veritabanı açıp göçleri uygular.
    ///
    /// <para>Her test sınıfı kendi veritabanını alıyor: paylaşılan bir şemada
    /// bir sınıfın yazdığı satır başka bir sınıfın sayımına karışır ve o hata
    /// yalnızca testler paralel koştuğunda görünür.</para>
    /// </summary>
    public static async Task<ClickHouseContext> ClickHouseAsync(
        DevStackFixture stack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        var context = await stack.CreateIsolatedClickHouseContextAsync(cancellationToken);
        await new ClickHouseMigrator(context).MigrateAsync(RepoPath("db/clickhouse"), cancellationToken);

        return context;
    }

    /// <summary>
    /// Runs the real publication initializer after both stores have been set up.
    /// Returns its status unchanged; negative migration fixtures call the runner
    /// directly so setup cannot mask their pre-repair state.
    /// </summary>
    public static Task<TopologyRepairInitializationResult> InitializeTopologyPublicationAsync(
        IDbContextFactory<ControlPlaneDbContext> factory,
        ClickHouseContext storage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(storage);

        var readiness = new TopologyObservedRepairReadiness(factory, storage);
        var runner = new TopologyPublicationRepairRunner(factory, storage, readiness);
        return runner.InitializeAsync(TopologyRepairStartMode.Startup, cancellationToken);
    }

    /// <summary>
    /// Explicit test-only fresh-store restore boundary. The caller must have
    /// stopped all writers and provisioned a different, migrated CH database.
    /// Resets only derived publication/repair state; preserves captured source
    /// history, binding, declared history and archive authority for replay.
    /// Does not initialize or certify the new target.
    /// </summary>
    public static async Task ResetTopologyPublicationForFreshStoreAsync(
        IDbContextFactory<ControlPlaneDbContext> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var reset = await db.Database.BeginTransactionAsync(cancellationToken);
        await ResetTopologyPublicationStateAsync(db, cancellationToken);
        await reset.CommitAsync(cancellationToken);
    }

    private static async Task ResetTopologyPublicationStateAsync(
        ControlPlaneDbContext db, CancellationToken cancellationToken)
    {
        // This owned fixture reset starts a new PG/CH test identity. It never
        // certifies Ready: positive setup calls the real runner afterward,
        // while migration/repair-negative tests retain Uninitialized state.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM bizigo.topology_repair_attestations;", cancellationToken);
        var repairReset = await db.Database.ExecuteSqlRawAsync("""
            UPDATE bizigo.topology_repair_state
            SET pg_database_identity = gen_random_uuid(),
                phase = 'Uninitialized', generation = 0, copy_attempt_id = NULL,
                clickhouse_database_uuid = NULL, old_canonical_identity_json = NULL,
                allowed_copy_identity_json = NULL, automatic_empty_init = false,
                certificate_digest = NULL, certificate_json = NULL,
                receipt_prefix_sequence = 0, receipt_prefix_sha256 = NULL,
                sidecar_revision = 0, certificate_member_set_id = NULL,
                updated_at = now()
            WHERE id = 1;
            """, cancellationToken);
        Assert.Equal(1, repairReset);

        // Privileged test-only reset of the owned, quiesced fixture. The update
        // above clears the certificate reference together with Uninitialized,
        // before removing immutable membership authority. All three related
        // tables are named explicitly; production DML guards remain intact.
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE bizigo.topology_repair_members,
                bizigo.topology_repair_member_sets,
                bizigo.topology_legacy_conversion_sidecars;
            """, cancellationToken);

        // PG is shared, while every test gets a fresh CH database. Receipts and
        // pending reservations belong to that same derived publication epoch;
        // retaining either while resetting sequence zero contaminates the next test.
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM bizigo.topology_publication_pending;
            DELETE FROM bizigo.topology_publication_receipts;
            """, cancellationToken);
        await db.TopologyReadState.ExecuteDeleteAsync(cancellationToken);
        // Publication initialization requires the authoritative zero state;
        // absence is not a successful empty-state certificate.
        var seeded = await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO bizigo.topology_read_state (id, epoch, published_sequence)
            VALUES (1, 0, 0);
            """, cancellationToken);
        Assert.Equal(1, seeded);
    }

    /// <summary>
    /// Kontrol düzlemi fabrikası: göçler uygulanmış ve testin dokunduğu tablolar
    /// boşaltılmış.
    ///
    /// <para>
    /// Postgres <b>paylaşılıyor</b> — ClickHouse'un aksine izole veritabanı
    /// açılmıyor — dolayısıyla artık satırlar sızabiliyor. Temizlik burada tek
    /// yerde duruyor ki yeni bir tablo eklendiğinde eklenecek yer belli olsun.
    /// </para>
    /// </summary>
    public static async Task<IDbContextFactory<ControlPlaneDbContext>> ControlPlaneAsync(
        DevStackFixture stack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        IDbContextFactory<ControlPlaneDbContext> factory =
            new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        await using var reset = await db.Database.BeginTransactionAsync(cancellationToken);
        await ResetTopologyPublicationStateAsync(db, cancellationToken);
        await db.RawManifest.ExecuteDeleteAsync(cancellationToken);
        await db.Sources.ExecuteDeleteAsync(cancellationToken);
        await db.SourceOwnershipHistory.ExecuteDeleteAsync(cancellationToken);
        await db.TelemetryOwnerClaims.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyDeclaredEdgeHistory.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyDeclaredEdges.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyBindings.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyOwnerHistory.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyNodeHistory.ExecuteDeleteAsync(cancellationToken);
        await db.TopologyNodes.ExecuteDeleteAsync(cancellationToken);
        await reset.CommitAsync(cancellationToken);

        return factory;
    }

    /// <summary>
    /// Ham arşiv seçenekleri — her çağrı <b>kendi kovasını</b> alıyor.
    ///
    /// <para>Kova adı paylaşılsaydı bir testin yazdığı nesne başka bir testin
    /// manifest doğrulamasında görünürdü; ayrı kova, S3 tarafındaki izolasyonun
    /// ClickHouse tarafındakiyle aynı seviyeye gelmesi.</para>
    /// </summary>
    public static RawStoreOptions RawOptions(DevStackFixture stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        return new RawStoreOptions
        {
            ServiceUrl = stack.S3ServiceUrl,
            Bucket = "bizigo-raw-" + Guid.NewGuid().ToString("N")[..8],
            AccessKey = "bizigoadmin",
            SecretKey = "bizigoadmin",
            ForcePathStyle = true,
            SegmentRetention = TimeSpan.FromHours(48),
        };
    }
}

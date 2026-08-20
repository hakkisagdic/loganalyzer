using Bizigo.ControlPlane;
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

        await db.RawManifest.ExecuteDeleteAsync(cancellationToken);
        await db.Sources.ExecuteDeleteAsync(cancellationToken);

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

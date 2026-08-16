using System.Text;
using Amazon.S3.Model;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T01 kabul kriteri: üç bileşene de bağlanılıyor ve göç altyapısı çalışıyor.
/// İş mantığı yok — bu testler zemin sağlam mı diye bakıyor.
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class DevStackSmokeTests(DevStackFixture stack)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClickHouse_bos_dizinde_gocu_calistirabiliyor()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "bizigo-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);

        try
        {
            using var context = stack.CreateClickHouseContext();
            var migrator = new ClickHouseMigrator(context);

            var result = await migrator.MigrateAsync(emptyDir, TestContext.Current.CancellationToken);

            Assert.Empty(result.Applied);
            Assert.Empty(result.AlreadyApplied);

            // schema_migrations tablosu oluşmuş olmalı — boş klasörde bile.
            var applied = await migrator.GetAppliedAsync(TestContext.Current.CancellationToken);
            Assert.Empty(applied);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClickHouse_gocu_uyguluyor_ve_tekrar_calistirmak_guvenli()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bizigo-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "0001_smoke.sql"),
                """
                -- İki ifadeli bir göç; ayraç mantığını da doğruluyor.
                CREATE TABLE IF NOT EXISTS smoke_a (id UInt32, note String) ENGINE = MergeTree ORDER BY id;

                INSERT INTO smoke_a (id, note) VALUES (1, 'noktalı virgül; metin içinde');
                """,
                TestContext.Current.CancellationToken);

            using var context = stack.CreateClickHouseContext();
            var migrator = new ClickHouseMigrator(context);

            var first = await migrator.MigrateAsync(dir, TestContext.Current.CancellationToken);
            Assert.Equal(["0001_smoke"], first.Applied);

            // İkinci çalıştırma idempotent olmalı.
            var second = await migrator.MigrateAsync(dir, TestContext.Current.CancellationToken);
            Assert.Empty(second.Applied);
            Assert.Equal(["0001_smoke"], second.AlreadyApplied);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClickHouse_uygulanmis_gocun_degistirilmesini_reddediyor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bizigo-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "0001_drift.sql");

        try
        {
            await File.WriteAllTextAsync(
                file,
                "CREATE TABLE IF NOT EXISTS drift_a (id UInt32) ENGINE = MergeTree ORDER BY id;",
                TestContext.Current.CancellationToken);

            using var context = stack.CreateClickHouseContext();
            var migrator = new ClickHouseMigrator(context);
            await migrator.MigrateAsync(dir, TestContext.Current.CancellationToken);

            // Uygulanmış göç dosyası değiştirildi — sessizce geçilmemeli.
            await File.WriteAllTextAsync(
                file,
                "CREATE TABLE IF NOT EXISTS drift_a (id UInt32, extra String) ENGINE = MergeTree ORDER BY id;",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => migrator.MigrateAsync(dir, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Postgres_kontrol_duzlemi_gocu_uygulaniyor()
    {
        // Uygulamayla AYNI yapılandırma — snake_case dahil. Ayrışırsa göç bir yerde
        // çalışıp başka yerde çalışmaz.
        var builder = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        ControlPlaneServiceCollectionExtensions.Configure(builder, stack.PostgresConnectionString);
        var options = (DbContextOptions<ControlPlaneDbContext>)builder.Options;

        await using var db = new ControlPlaneDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(pending);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RustFs_S3_API_ile_nesne_yazilip_okunabiliyor()
    {
        using var s3 = stack.CreateS3Client();
        const string bucket = "bizigo-raw";
        const string key = "smoke/deneme.txt";
        const string payload = "Türkçe içerik — ıİşŞğĞüÜöÖçÇ";

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, TestContext.Current.CancellationToken);

        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload,
                ContentType = "text/plain; charset=utf-8",
            },
            TestContext.Current.CancellationToken);

        using var response = await s3.GetObjectAsync(bucket, key, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        var roundTripped = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(payload, roundTripped);
    }
}

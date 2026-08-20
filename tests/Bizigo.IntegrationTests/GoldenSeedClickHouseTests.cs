using System.Globalization;
using Bizigo.Cli.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Altın örnek yükleyicisinin (T39) canlı ClickHouse tarafı.
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlayacak:</b> birim testleri yükleyicinin ürettiği
/// <see cref="LogEvent"/>'leri doğruluyor — zaman damgası ekilen ana eşit,
/// Sigma sondası satırda duruyor, dört vendor da planda var. Ama ölçüm
/// araçlarının baktığı yer <c>LogEvent</c> değil <b>ClickHouse görünümü</b>.
/// Aradaki her adım (RowBinary yazımı, <c>Map</c> kolonu, <c>events_ocsf</c>
/// görünümünün kolon adları) birim testinin göremediği yerde duruyor ve
/// ölçümlerin ön kontrolü tam olarak orada koşuyor.
/// </para>
///
/// <para>
/// Somut olarak dört şey:
/// </para>
/// <list type="number">
/// <item><c>measure.py</c>'nin sondası <c>events_ocsf.raw_data</c> içinde
/// <b>gerçekten</b> bulunuyor — dört vendor için de. Bu, Sigma ölçümünün
/// başlayıp başlamayacağı sorusunun kendisi.</item>
/// <item><c>device_vendor_name</c> ve <c>metadata_product_name</c> değerleri
/// ön kontrolün aradığı yazımlarla birebir aynı (<c>Fortinet</c>, <c>Cisco</c>,
/// <c>MikroTik</c>, <c>nginx</c>) — biri değişirse o vendor "verisiz" sayılıp
/// paydadan düşer ve oran sessizce yükselir.</item>
/// <item><c>signature_hash</c> kolonu dolu ve ClickHouse'un kendi
/// <c>xxHash64</c>'üyle uyumlu — baseline ölçümü bu kolonun üstünde duruyor.</item>
/// <item><see cref="SeedMaintenance"/> yalnızca <b>kendi</b> kapsam grubunu
/// siliyor. Bu maddenin bedeli ölçüldü: ClickHouse'ta bir milyon satırlık
/// kıyaslama verisi duruyor ve yükleyicinin onu silmesi başka bir ölçümü
/// sessizce yok etmek olurdu.</item>
/// </list>
///
/// <para>
/// <b>Koordinatör koşturur</b> (protokol §2): Testcontainers gerekiyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class GoldenSeedClickHouseTests(DevStackFixture stack) : IAsyncLifetime
{
    private const string SeedGroup = "golden";

    /// <summary>Yükleyicinin dokunmaması gereken "ürün verisi"nin temsilcisi.</summary>
    private const string ForeignGroup = "net-core";

    private static readonly DateTimeOffset Anchor = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        await new ClickHouseMigrator(_context).MigrateAsync(
            RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sigma_on_kontrolu_yuklenen_veriden_geciyor()
    {
        await SeedAsync();

        // 1 · Sonda. `measure.py` bunu bulamazsa çıkış kodu 3 ile duruyor.
        foreach (var (vendor, probe) in GoldenProbes())
        {
            var escaped = probe.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "''", StringComparison.Ordinal);

            var hits = await ScalarAsync(
                $"SELECT count() FROM events_ocsf WHERE position(raw_data, '{escaped}') > 0");

            Assert.True(hits > 0, $"{vendor}: measure.py'nin sondası events_ocsf.raw_data içinde yok.");
        }

        // 2 · Ön kontrolün vendor sorguları. Yazımlar measure.py'den birebir.
        foreach (var vendor in new[] { "Fortinet", "Cisco", "MikroTik" })
        {
            var rows = await ScalarAsync(
                $"SELECT count() FROM events_ocsf WHERE device_vendor_name = '{vendor}'");

            Assert.True(rows > 0, $"{vendor} events_ocsf'te yok — Sigma ölçümü kurallarını paydadan düşer.");
        }

        Assert.True(
            await ScalarAsync("SELECT count() FROM events_ocsf WHERE metadata_product_name = 'nginx'") > 0,
            "nginx events_ocsf'te yok — Sigma ölçümü kurallarını paydadan düşer.");
    }

    /// <summary>
    /// Baseline ölçümü (T35) <c>signature_hash</c> üzerinde <c>GROUP BY</c>
    /// yapıyor: kolon boşsa ölçüm "pencerede hiç imzalı olay yok" der.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yuklenen_satirlar_imza_tasiyor_ve_zamana_yayilmis()
    {
        await SeedAsync();

        var signatures = await ScalarAsync(
            $"SELECT uniqExact(signature_hash) FROM events WHERE owner_group = '{SeedGroup}' AND signature_hash != 0");

        Assert.True(signatures > 10, $"Yalnızca {signatures} ayrı imza — eğri oluşmaz.");

        // Yayılım: taban süpürmesi 30 güne kadar gidiyor, veri de gitmeli.
        var days = await ScalarAsync(
            $"SELECT uniqExact(toDate(ts)) FROM events WHERE owner_group = '{SeedGroup}'");

        Assert.True(days >= 30, $"Veri yalnızca {days} ayrı güne yayılmış; taban 30 güne süpürülüyor.");

        // Olay penceresinin kendisi: boşsa oranın paydası sıfır olur.
        var recent = await ScalarAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT count() FROM events WHERE owner_group = '{SeedGroup}' " +
            $"AND ts >= toDateTime64('{Anchor.AddMinutes(-45):yyyy-MM-dd HH:mm:ss}', 3, 'UTC')"));

        Assert.True(recent > 0, "Son 45 dakikada hiç satır yok — baseline ölçümü hiç başlamaz.");
    }

    /// <summary>
    /// <b>Vazgeçilmez madde.</b> Yükleyici kendi grubunu silebiliyor ama başka
    /// hiçbir gruba dokunmuyor. Kırmızı yanma yolu: <c>DeleteAsync</c>'in
    /// <c>WHERE</c>'i düşerse bu test yabancı grubun satırlarının gittiğini
    /// görür.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Temizleme_yalnizca_kendi_grubunu_siliyor()
    {
        var writer = new EventWriter(_context);
        await writer.WriteEventsAsync([Foreign(), Foreign()], TestContext.Current.CancellationToken);

        await SeedAsync();

        var maintenance = new SeedMaintenance(_context);
        Assert.True(await maintenance.CountAsync(SeedGroup, TestContext.Current.CancellationToken) > 0);

        await maintenance.DeleteAsync(SeedGroup, TestContext.Current.CancellationToken);

        Assert.Equal(0, await maintenance.CountAsync(SeedGroup, TestContext.Current.CancellationToken));
        Assert.Equal(2, await maintenance.CountAsync(ForeignGroup, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Yükleyicinin ürettiği <c>signature_hash</c> ClickHouse'un kendi
    /// <c>xxHash64</c>'ü ile aynı ailede: kolon <c>UInt64</c> ve sıfır değil.
    /// Hash'in tanımını <c>SignatureHashStorageTests</c> zaten çiviliyor;
    /// buradaki soru yükleyicinin o yolu gerçekten koşturup koşturmadığı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Imzasiz_satir_orani_dusuk()
    {
        await SeedAsync();

        var total = await ScalarAsync($"SELECT count() FROM events WHERE owner_group = '{SeedGroup}'");
        var unsigned = await ScalarAsync(
            $"SELECT count() FROM events WHERE owner_group = '{SeedGroup}' AND signature_hash = 0");

        Assert.Equal(0, unsigned);
        Assert.True(total > 0);
    }

    /// <summary>
    /// Yükleyicinin kendisi — birim testiyle <b>aynı</b> tipler, yalnızca yazım
    /// ucunda ClickHouse var.
    /// </summary>
    private async Task SeedAsync()
    {
        var catalogDirectory = RepoPath(Path.Combine("catalog", "parsers"));
        var samples = GoldenSampleSeeder.ReadSamples(catalogDirectory);
        Assert.NotEmpty(samples);

        var tables = MappingTableCatalog.LoadFromDirectory(RepoPath(Path.Combine("catalog", "mappings")));
        var library = GrokPatternLibrary.LoadWithOverlay(
            RepoPath(Path.Combine("catalog", "patterns", "legacy")),
            RepoPath(Path.Combine("catalog", "patterns", "bizigo-v1")));

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(
            catalogDirectory, new ParserCompiler(new GrokCompiler(library), tables));
        Assert.Empty(load.Errors);

        var masks = MaskCatalog.LoadFromFile(RepoPath(Path.Combine("catalog", "masks", "bizigo-masks.yaml")));
        var seeder = new GoldenSampleSeeder(new Dispatcher(catalog, new DispatchStats()), masks);

        // Testte küçük hacim: burada ölçülen şey hacim değil şekil. Ölçümlerin
        // koşacağı gerçek yükleme CLI'dan `--events` ile geliyor.
        var plan = GoldenSamplePlan.Build(
            seeder.Signatures(samples),
            GoldenSampleSeeder.Vendors(samples),
            new SeedPlanOptions(Anchor, TimeSpan.FromDays(30), TotalEvents: 4_000, ZipfExponent: 2.0, Seed: 39));

        var writer = new EventWriter(_context);

        await seeder.RunAsync(
            samples, plan, SeedGroup, batchRows: 2_000,
            (batch, token) => writer.WriteEventsAsync(batch, token),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>measure.py</c>'nin <c>golden_probes()</c> karşılığı: en uzun satırın
    /// ortasından 60 karakter. Kopya olması bilinçli — Python'u .NET testinden
    /// çağırmanın bedeli, kuralın iki tarafta da <b>aynı dosyadan</b>
    /// türetilmesinden büyük.
    /// </summary>
    private static Dictionary<string, string> GoldenProbes()
    {
        var probes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var directory in Directory.EnumerateDirectories(
                     RepoPath(Path.Combine("catalog", "parsers")), "samples", SearchOption.AllDirectories))
        {
            var longest = string.Empty;

            foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length > longest.Length)
                    {
                        longest = line;
                    }
                }
            }

            if (longest.Length >= 100)
            {
                probes[directory] = longest.Substring((longest.Length - 60) / 2, 60);
            }
        }

        Assert.Equal(4, probes.Count);
        return probes;
    }

    /// <summary>
    /// Sorgular fixture'ın HTTP yardımcısından geçiyor, sürücüden değil:
    /// <c>ClickHouseContext.CreateConnection</c> bilinçli olarak
    /// <c>internal</c> (K17 mimari testi) ve test iskelesinin o kuralı delmesi
    /// kuralı anlamsız kılardı.
    /// </summary>
    private async Task<long> ScalarAsync(string sql)
    {
        var raw = await stack.QueryScalarAsync(
            _context.Options.ConnectionString, sql, TestContext.Current.CancellationToken);

        return raw.Length == 0 ? 0 : long.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static LogEvent Foreign() => new()
    {
        EventId = Guid.NewGuid(),
        Timestamp = Anchor,
        IngestedAt = Anchor,
        TimeSource = TimeSources.Received,
        OwnerGroup = ForeignGroup,
        SourceId = "benchmark",
        Host = "benchmark",
        Vendor = "Fortinet",
        Product = "FortiGate",
        ParserId = "fortinet.fortigate.traffic",
        ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok,
        EncodingDetected = "utf-8",
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal),
        Body = "kıyaslama verisi — yükleyici buna dokunmamalı",
        RawRef = string.Empty,
    };

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }
}

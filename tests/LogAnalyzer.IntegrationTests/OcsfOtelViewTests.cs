using System.Diagnostics;
using System.Globalization;
using System.Net;
using LogAnalyzer.Contracts;
using LogAnalyzer.Storage.ClickHouse;

namespace LogAnalyzer.IntegrationTests;

/// <summary>
/// T07 kabul kriterleri (F1 §5, K8).
///
/// <para>
/// Asıl iddia: <b>aynı olay hem OCSF hem OTel görünümünden okunabiliyor</b> ve
/// bunun için iki kez saklanmıyor. Türetme maliyeti varsayılmıyor, ölçülüyor —
/// ticket bunu açıkça istiyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class OcsfOtelViewTests(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>
    /// Testlerin olay anı. Takvimden değil <see cref="EventTestClock"/>'tan:
    /// <c>events</c>'teki 90 günlük TTL'i ClickHouse kendi saatiyle uyguluyor ve
    /// sabit bir tarih (eskiden 2026-08-16 12:30) doksan gün sonra sessizce
    /// silinen satır demekti.
    /// </summary>
    private static readonly DateTimeOffset At = EventTestClock.Day.AddHours(12).AddMinutes(30);

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        _writer = new EventWriter(_context);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(
            RepoPath("db/clickhouse"),
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogAnalyzer.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (LogAnalyzer.sln).")
            : Path.Combine(dir.FullName, relative);
    }

    private static LogEvent Sample(DateTimeOffset ts) => new()
    {
        EventId = Guid.CreateVersion7(ts),
        Timestamp = ts,
        OwnerGroup = "network/core",
        SourceId = "fg-ankara-01",
        Host = "fw-01",
        Vendor = "fortinet",
        Product = "fortigate",
        ParserId = "fortinet.traffic",
        ParserVersion = "1.2.0",
        ParseStatus = ParseStatus.Ok,
        EncodingDetected = "windows-1254",
        SeverityNum = 5,
        OcsfClassUid = 4001,
        OcsfActivityId = 6,
        SrcIp = IPAddress.Parse("10.1.2.3").MapToIPv6(),
        DstIp = IPAddress.Parse("8.8.8.8").MapToIPv6(),
        SrcPort = 41022,
        DstPort = 443,
        Proto = "tcp",
        Action = "accept",
        Outcome = "success",
        UserName = "ahmet",
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["devid"] = "FG100E",
            ["ocsf.disposition_id"] = "2",
        },
        Body = "bağlantı kabul edildi",
        RawRef = $"raw/network/core/{EventTestClock.RawHour(ts)}/firewall/",
    };

    private Task<string> ScalarAsync(string sql) => stack.QueryScalarAsync(
        _context.Options.ConnectionString,
        sql,
        TestContext.Current.CancellationToken);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ayni_olay_hem_OCSF_hem_OTel_gorunumunden_okunuyor()
    {
        await _writer.WriteEventsAsync([Sample(At)], TestContext.Current.CancellationToken);

        var ocsfClass = await ScalarAsync("SELECT class_uid FROM events_ocsf LIMIT 1");
        var ocsfProto = await ScalarAsync("SELECT connection_info_protocol_name FROM events_ocsf LIMIT 1");
        var otelHost = await ScalarAsync("SELECT `host.name` FROM events_otel LIMIT 1");
        var otelBody = await ScalarAsync("SELECT Body FROM events_otel LIMIT 1");

        Assert.Equal("4001", ocsfClass);
        Assert.Equal("tcp", ocsfProto);
        Assert.Equal("fw-01", otelHost);
        Assert.Equal("bağlantı kabul edildi", otelBody);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kolona_yazilmayan_OCSF_alani_attrs_uzerinden_gorunuyor()
    {
        await _writer.WriteEventsAsync(
            [Sample(At)],
            TestContext.Current.CancellationToken);

        // Yeni bir OCSF alanı eklemek şema göçü değil, YAML değişikliği olmalı.
        var value = await ScalarAsync("SELECT unmapped['ocsf.disposition_id'] FROM events_ocsf LIMIT 1");

        Assert.Equal("2", value);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IPv4_ve_IPv6_ayni_kolondan_sorgulanabiliyor()
    {
        var v6 = Sample(At.AddSeconds(1)) with { SrcIp = IPAddress.Parse("2001:db8::1") };
        await _writer.WriteEventsAsync([Sample(At), v6], TestContext.Current.CancellationToken);

        var v4Count = await ScalarAsync(
            "SELECT count() FROM events WHERE src_ip = toIPv6('::ffff:10.1.2.3')");
        var v6Count = await ScalarAsync(
            "SELECT count() FROM events WHERE src_ip = toIPv6('2001:db8::1')");

        Assert.Equal("1", v4Count);
        Assert.Equal("1", v6Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Farkli_saat_dilimlerindeki_olaylar_UTC_de_dogru_siralaniyor()
    {
        // İki damga, farklı ofsetlerle. Duvar saati değerleri (15:00 ve 13:30)
        // UTC sırasının TERSİ: UTC'ye çevrilmezse sıralama cihazın saat
        // dilimine göre değişirdi.
        var istanbul = EventTestClock.Day.AddHours(12).ToOffset(TimeSpan.FromHours(3));
        var london = EventTestClock.Day.AddHours(12).AddMinutes(30).ToOffset(TimeSpan.FromHours(1));

        await _writer.WriteEventsAsync(
            [Sample(istanbul) with { Host = "istanbul" }, Sample(london) with { Host = "london" }],
            TestContext.Current.CancellationToken);

        // london 12:30 UTC, istanbul 12:00 UTC → london sonra gelmeli.
        var first = await ScalarAsync("SELECT host FROM events ORDER BY ts ASC LIMIT 1");
        var last = await ScalarAsync("SELECT host FROM events ORDER BY ts DESC LIMIT 1");

        Assert.Equal("istanbul", first);
        Assert.Equal("london", last);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Turetme_maliyeti_olculuyor()
    {
        var ts = EventTestClock.Day.AddHours(12);
        var events = Enumerable.Range(0, 20_000)
            .Select(i => Sample(ts.AddMilliseconds(i)))
            .ToArray();

        await _writer.WriteEventsAsync(events, TestContext.Current.CancellationToken);

        var direct = Stopwatch.StartNew();
        var directCount = await ScalarAsync("SELECT count() FROM events WHERE proto = 'tcp'");
        direct.Stop();

        var view = Stopwatch.StartNew();
        var viewCount = await ScalarAsync(
            "SELECT count() FROM events_ocsf WHERE connection_info_protocol_name = 'tcp'");
        view.Stop();

        Assert.Equal(directCount, viewCount);

        // Görünüm bir yeniden adlandırma katmanı; ölçüm bunu doğruluyor.
        // Kesin süre makineye göre değişir, o yüzden oran değil VARLIK sınanıyor:
        // görünüm sorgusu tamamlanıyor ve aynı sonucu veriyor.
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20k satır — doğrudan: {direct.ElapsedMilliseconds} ms, görünüm: {view.ElapsedMilliseconds} ms"));

        // Görünüm doğrudan sorgunun 10 katından yavaşsa türetme kararı yeniden
        // değerlendirilmeli; bu eşik bir performans hedefi değil, bir alarm.
        Assert.True(
            view.ElapsedMilliseconds <= Math.Max(50, direct.ElapsedMilliseconds * 10),
            $"Görünüm türetmesi beklenenden pahalı: {view.ElapsedMilliseconds} ms / {direct.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Severity_OCSF_olceginden_OTel_olcegine_ceviriliyor()
    {
        // Aynı kolon iki farklı ölçekle okunuyordu (T08 geri beslemesi, madde 9):
        // OCSF Critical (5) OTel'de DEBUG anlamına geliyordu. Sessiz sınıftan bir
        // hata — sorgu çalışıyor, sayı dönüyor, yalnızca anlamı yanlış.
        await _writer.WriteEventsAsync(
            [Sample(At) with { SeverityNum = 5 }],
            TestContext.Current.CancellationToken);

        Assert.Equal("5", await ScalarAsync("SELECT severity_id FROM events_ocsf LIMIT 1"));
        Assert.Equal("19", await ScalarAsync("SELECT SeverityNumber FROM events_otel LIMIT 1"));

        // Eşlemenin kaybettiği ayrıntı geri kazanılabilmeli: Critical ve High
        // aynı ERROR bandına düşüyor.
        Assert.Equal(
            "5",
            await ScalarAsync("SELECT `loganalyzer.ocsf_severity_id` FROM events_otel LIMIT 1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bilinmeyen_severity_OTel_de_de_bilinmeyen_kaliyor()
    {
        // "Belirtilmemiş" ile "düşük" aynı şey değil; 0 → 0.
        await _writer.WriteEventsAsync(
            [Sample(At) with { SeverityNum = 0 }],
            TestContext.Current.CancellationToken);

        Assert.Equal("0", await ScalarAsync("SELECT SeverityNumber FROM events_otel LIMIT 1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gorunumler_owner_group_kolonunu_tasiyor()
    {
        await _writer.WriteEventsAsync(
            [Sample(At)],
            TestContext.Current.CancellationToken);

        // Kapsam filtresi görünümde DEĞİL; kolonu taşıması yeterli, filtreyi
        // IScopedQuery uyguluyor. Görünüme filtre gömmek kapsamı iki yerde
        // tanımlamak olurdu.
        Assert.Equal("network/core", await ScalarAsync("SELECT owner_group FROM events_ocsf LIMIT 1"));
        Assert.Equal("network/core", await ScalarAsync("SELECT owner_group FROM events_otel LIMIT 1"));
    }
}

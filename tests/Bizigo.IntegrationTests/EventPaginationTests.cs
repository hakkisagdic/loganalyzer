using System.Diagnostics;
using System.Globalization;
using System.Net;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// T15 kabul kriteri: <b>derin sayfalama gerçek veriyle sınanıyor</b> — sayfa
/// 50'de ilk sayfayla karşılaştırılabilir süre.
///
/// <para>
/// F1'de ölçülen kısıt: keyset ancak sıralama anahtarının tam öneki
/// (<c>owner_group</c> + <c>source_id</c>) verildiğinde sabit süreli. Filtresiz
/// derin sayfa 1M satır okuyordu, kaynak filtresiyle 57k — ve derin sayfa ilk
/// sayfadan <b>hızlı</b> çıkmıştı (13,7 ms / 17,8 ms).
/// </para>
///
/// <para>
/// <b>Süre mutlak bir bütçeyle değil, aynı süreçte alınan bir tabana göre
/// ölçülüyor.</b> F1'in en pahalı dersi buydu: duvar saati bütçesi ölçmek
/// istediğin şeyi ölçmez — yüklü makinede sağlıklı kod bütçeyi aşar, hızlı
/// makinede bozuk kod bütçeye sığar. Buradaki eşik bir performans hedefi değil,
/// <b>bir alarm</b>: oran patlarsa keyset yerine offset'e benzer bir davranışa
/// düşülmüş demektir.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class EventPaginationTests(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>
    /// İki kaynağa bölünüyor, yani kaynak başına 12.000 satır. Sayfa 50'ye
    /// ulaşmak 10.000 satır istiyor; pay bilerek bırakıldı ki son sayfada imleç
    /// tükenmesin ve test "sayfalama bitti" ile "sayfalama bozuldu"yu
    /// karıştırmasın.
    /// </summary>
    private const int TotalEvents = 24_000;
    private const int PageSize = 200;
    private const int DeepPage = 50;

    private static readonly DateTimeOffset Start = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;
    private EventReader _reader = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(TestContext.Current.CancellationToken);
        await new ClickHouseMigrator(_context).MigrateAsync(
            RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        _reader = new EventReader(_context);

        var writer = new EventWriter(_context);

        // İki kaynak: kaynak filtresinin gerçekten daralttığını görebilmek için.
        // Tek kaynak olsaydı filtre hiçbir şeyi elemez, ölçüm de bir şey söylemezdi.
        var events = Enumerable.Range(0, TotalEvents)
            .Select(i => Sample(i, i % 2 == 0 ? "fg-core-01" : "fg-core-02"))
            .ToArray();

        foreach (var chunk in events.Chunk(5_000))
        {
            await writer.WriteEventsAsync(chunk, TestContext.Current.CancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
    }

    private static LogEvent Sample(int index, string sourceId)
    {
        var ts = Start.AddSeconds(index);

        return new LogEvent
        {
            EventId = Guid.CreateVersion7(ts),
            Timestamp = ts,
            TimeSource = TimeSources.Parsed,
            OwnerGroup = "net-core",
            SourceId = sourceId,
            Host = sourceId,
            Vendor = "fortinet",
            Product = "fortigate",
            ParseStatus = ParseStatus.Ok,
            SeverityNum = 5,
            SrcIp = IPAddress.IPv6Any,
            DstIp = IPAddress.IPv6Any,
            Body = string.Create(CultureInfo.InvariantCulture, $"bağlantı kabul edildi #{index}"),
            RawRef = "raw/net-core/2026/08/17/00/firewall/",
        };
    }

    private static EventQuery Page(string? sourceId, EventCursor? after) => new()
    {
        From = Start.AddMinutes(-1),
        To = Start.AddSeconds(TotalEvents + 60),
        SourceIds = sourceId is null ? [] : [sourceId],
        After = after,
        Limit = PageSize,
    };

    private static ScopePredicate Scope() =>
        ScopePredicate.From(AccessScope.ForGroups("u-core", ["net-core"]));

    /// <summary>
    /// Sayfalamanın <b>doğruluğu</b> — süreden önce gelen iddia. Keyset'in
    /// bozulması önce yinelenen ya da atlanan satır olarak görünür; ekran
    /// tarafında bu, kullanıcının sayfaladığını sanıp aynı satırları görmesi
    /// demek.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Elli_sayfa_boyunca_hicbir_olay_yinelenmiyor_ve_atlanmiyor()
    {
        var seen = new HashSet<Guid>();
        EventCursor? cursor = null;
        var previous = (Timestamp: DateTimeOffset.MaxValue, EventId: Guid.Empty);

        for (var page = 0; page < DeepPage; page++)
        {
            var result = await _reader.SearchAsync(
                Page("fg-core-01", cursor), Scope(), TestContext.Current.CancellationToken);

            Assert.Equal(PageSize, result.Events.Count);

            foreach (var item in result.Events)
            {
                Assert.True(seen.Add(item.EventId), $"Sayfa {page}: olay yinelendi ({item.EventId}).");

                // Azalan sıra: (ts, event_id) demeti kesin olarak küçülmeli.
                var current = (item.Timestamp, item.EventId);
                Assert.True(
                    current.Timestamp < previous.Timestamp
                        || (current.Timestamp == previous.Timestamp && current.EventId.CompareTo(previous.EventId) < 0),
                    $"Sayfa {page}: sıralama bozuldu ({current} >= {previous}).");

                previous = current;
            }

            cursor = result.Next;
            Assert.NotNull(cursor);
        }

        Assert.Equal(DeepPage * PageSize, seen.Count);
    }

    /// <summary>
    /// Kaynak filtresiyle derin sayfa, ilk sayfayla <b>karşılaştırılabilir</b>
    /// sürede. Ölçü mutlak değil orana dayalı ve taban aynı süreçte alınıyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kaynak_filtresiyle_derin_sayfa_ilk_sayfa_kadar_hizli()
    {
        var (firstMs, deepMs) = await MeasureAsync("fg-core-01");

        // Aynı ölçüm kaynak filtresiz de alınıyor — bir eşik olarak değil,
        // ekranın kullanıcıyı kaynak filtresine yönlendirmesinin sayısal
        // gerekçesini kayda geçirmek için.
        var (openFirstMs, openDeepMs) = await MeasureAsync(null);

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"filtreli  — sayfa 1: {firstMs} ms, sayfa {DeepPage}: {deepMs} ms\n" +
            $"filtresiz — sayfa 1: {openFirstMs} ms, sayfa {DeepPage}: {openDeepMs} ms"));

        // Eşik geniş bilerek: makine yükü ölçümü kolayca iki katına çıkarıyor ve
        // bu test performansı değil DAVRANIŞI koruyor. Oran patlarsa keyset
        // sabit süreli olmaktan çıkmış demektir.
        Assert.True(
            deepMs <= Math.Max(50, firstMs * 5),
            $"Derin sayfa beklenenden pahalı: {deepMs} ms / {firstMs} ms");
    }

    private async Task<(long FirstMs, long DeepMs)> MeasureAsync(string? sourceId)
    {
        var watch = Stopwatch.StartNew();
        var first = await _reader.SearchAsync(
            Page(sourceId, null), Scope(), TestContext.Current.CancellationToken);
        watch.Stop();
        var firstMs = watch.ElapsedMilliseconds;

        var cursor = first.Next;
        for (var page = 1; page < DeepPage; page++)
        {
            var result = await _reader.SearchAsync(
                Page(sourceId, cursor), Scope(), TestContext.Current.CancellationToken);
            cursor = result.Next;
            Assert.NotNull(cursor);
        }

        watch.Restart();
        var deep = await _reader.SearchAsync(
            Page(sourceId, cursor), Scope(), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.NotEmpty(deep.Events);

        return (firstMs, watch.ElapsedMilliseconds);
    }
}

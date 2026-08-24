using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Sink'in kayıp muhasebesi ile tablonun gerçeği aynı şeyi söylüyor mu (S02a)</b>
/// — gerçek ClickHouse'a karşı.
///
/// <para>
/// Birim testi sink'in <i>kendi</i> sayaçlarının tutarlı olduğunu gösteriyor;
/// gösteremediği şey <b>ClickHouse'un gerçekten ne yaptığı</b>. Bu testin
/// koşturulduğunda kanıtladığı şey tam olarak o boşluk: sink "3 yazdım, 2'si
/// pencere dışıydı" dediğinde tabloda <b>gerçekten 3 satır var</b>.
/// </para>
///
/// <h3>Neden bu ayrım ölçülmek zorunda</h3>
///
/// <para>
/// Kayıp şöyle bulundu: simülatör 100 satır bastı, uç <c>accepted 100</c> ve
/// <c>processed 100</c> dedi, ClickHouse <c>query_log</c>'unda INSERT başarılı
/// göründü ve <c>written_rows</c> doluydu — <b>tabloda sıfır satır vardı.</b>
/// Yani istemciye dönen sayı hayatta kalanı değil <b>gönderileni</b>
/// bildiriyor, ve aradaki farkı hiçbir şey saymıyordu.
/// </para>
///
/// <para>
/// Sink artık farkı sayıyor ama o sayım <b>ClickHouse'un bugünkü davranışına
/// dayanan bir varsayım</b>: süresi dolmuş satır parça oluşturulurken atılıyor.
/// Varsayım iki yönde de bozulabilir — TTL kaldırılırsa sink olmayan bir kaybı
/// raporlar, TTL'in kapsamı genişlerse gerçek kaybı göremez. İkisi de sessiz.
/// Bu yüzden karşılaştırma sayaç-sayaç değil, <b>sayaç-tablo</b> yapılıyor.
/// </para>
///
/// <h3>Koşturulduğunda ne kanıtlar</h3>
///
/// <para>
/// Üç şey: (1) pencere dışı satır tabloya girmiyor, (2) sink bunu <c>Expired</c>
/// olarak sayıyor, (3) <c>Written</c> tabloda gerçekten duran satır sayısına
/// <b>eşit</b>. Üçüncüsü asıl iddia; ilk ikisi onsuz yalnızca kendi kendini
/// doğrulayan sayaçlar olurdu.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class EventRetentionChainTests(DevStackFixture stack) : IAsyncLifetime
{
    // Sabit an: testin geçme sebebi duvar saati olmamalı (§6). Pencere
    // içi/dışı ayrımı bu andan türetiliyor.
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly EventSinkOptions _options = new();

    private ClickHouseContext _context = null!;
    private ClickHouseEventSink _sink = null!;
    private string _group = null!;

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(
            BaselineWindowMeasurement.RepoPath("db/clickhouse"),
            TestContext.Current.CancellationToken);

        // Kendi kapsam grubu: paylaşılan bir ad, başka bir testin satırlarını
        // bu testin sayımına karıştırırdı ve fark iddiası anlamsızlaşırdı.
        _group = "s02a_" + Guid.NewGuid().ToString("N")[..8];

        var time = new FakeTimeProvider(Now);

        _sink = new ClickHouseEventSink(
            new EventWriter(_context),
            new EventNormalizer(time),
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ClickHouseEventSink>.Instance,
            time);
    }

    public async ValueTask DisposeAsync() => await _sink.DisposeAsync();

    /// <summary>
    /// <b>Sink'in "yazdım" dediği sayı tabloda duran satır sayısına eşit.</b>
    ///
    /// <para>
    /// Kırmızı yanabildiği şu şekilde ölçülür: sink'teki
    /// <c>result.RowsWritten - _expiredInBuffer</c> çıkarması kaldırılırsa
    /// <c>Written</c> 5 der, tabloda 3 satır vardır ve son iddia düşer.
    /// Aynı biçimde <c>0001_events.sql</c>'deki TTL satırı kaldırılırsa bu kez
    /// tabloda 5 satır olur ve <c>Expired</c> iddiası düşer — yani bekçi
    /// varsayımın iki yönde bozulmasını da görüyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sinkin_muhasebesi_tablonun_gercegiyle_ayni()
    {
        var eski = Now - _options.EventRetention - TimeSpan.FromDays(1);
        var yeni = Now - TimeSpan.FromHours(1);

        await _sink.HandleAsync(
            [Olay(eski), Olay(yeni), Olay(eski), Olay(yeni), Olay(yeni)],
            TestContext.Current.CancellationToken);

        await _sink.FlushAsync(TestContext.Current.CancellationToken);

        var tabloda = long.Parse(
            await stack.QueryScalarAsync(
            stack.ClickHouseConnectionString,
            $"SELECT count() FROM bizigo.events WHERE owner_group = '{_group}'",
                TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, _sink.Expired);

        // ASIL İDDİA: sayaç ile tablo aynı sayıyı söylüyor. Sink'in kendi
        // sayaçlarını birbirine karşı sınamak bunu göstermezdi.
        Assert.Equal(tabloda, _sink.Written);
        Assert.Equal(3, tabloda);

        // Hiçbir satır hesapsız kalmadı: uçtaki `unaccounted` alanının
        // dayandığı denklem bu.
        Assert.Equal(5, _sink.Written + _sink.Expired + _sink.Dropped);
    }

    private ParsedEvent Olay(DateTimeOffset timestamp) => new(
        new RawRecord
        {
            EventId = Guid.CreateVersion7(Now),
            ReceivedAt = Now,
            SourceKey = "10.1.1.1",
            OwnerGroup = _group,
            SourceId = "s02a",
            Body = Encoding.UTF8.GetBytes("gövde"),
        },
        "gövde",
        "utf-8",
        new ResolvedSource("s02a", _group, "firewall", "auto", string.Empty, IsKnown: true),
        new ParseResult
        {
            ParserId = "fortinet.fortigate.traffic",
            ParserVersion = "1",
            Status = ParseStatus.Ok,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal),
            Timestamp = timestamp,
        },
        DispatchTier.InventoryBound);
}

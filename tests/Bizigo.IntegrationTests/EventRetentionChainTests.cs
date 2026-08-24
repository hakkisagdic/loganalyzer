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
/// Üç şey: (1) pencere dışı satır tabloda kalmıyor, (2) sink bunu
/// <c>Expired</c> olarak sayıyor, (3) <c>Written</c> tabloda gerçekten duran
/// satır sayısına <b>eşit</b>. Üçüncüsü asıl iddia; ilk ikisi onsuz yalnızca
/// kendi kendini doğrulayan sayaçlar olurdu.
/// </para>
///
/// <h3>TTL'in NE ZAMAN uygulandığı ölçüme karışıyordu — düzeltildi</h3>
///
/// <para>
/// Bu testin ilk hâli yerelde geçti ve <b>CI'da düştü</b>: <c>Expected 5,
/// Actual 3</c> — yani tabloda beş satır vardı, sink üç diyordu. Sebep bir ürün
/// hatası değildi: ClickHouse süresi dolmuş satırı yerelde insert anında
/// atmıştı, CI'nın taze sunucusunda ise atmamıştı. TTL bir <b>birleştirme</b>
/// (merge) davranışı; insert anında uygulanması garanti değil.
/// </para>
///
/// <para>
/// Yani testin geçme sebebi duvar saatine — daha doğrusu ClickHouse'un merge
/// zamanlamasına — bağlıydı, ve §6 tam olarak bunu yasaklıyor. Yerelde geçmesi
/// hiçbir şey kanıtlamıyordu.
/// </para>
///
/// <para>
/// Düzeltme iki parça: test <b>kendi veritabanını</b> alıyor (paylaşılan tabloda
/// TTL'i zorlamak başka testlerin satırlarını da etkilerdi) ve TTL
/// <c>MATERIALIZE TTL … mutations_sync = 2</c> ile <b>açıkça ve senkron</b>
/// uygulanıyor. Ölçülen şey artık "şu anda tabloda ne var" değil,
/// <b>"TTL çalıştıktan sonra ne kalıyor"</b> — sayacın iddia ettiği de bu.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class EventRetentionChainTests(DevStackFixture stack) : IAsyncLifetime
{
    // Sabit an: testin geçme sebebi duvar saati olmamalı (§6). Pencere
    // içi/dışı ayrımı bu andan türetiliyor.
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly EventSinkOptions _options = new();

    private const string Group = "s02a";

    private ClickHouseContext _context = null!;
    private ClickHouseEventSink _sink = null!;

    public async ValueTask InitializeAsync()
    {
        // KENDİ veritabanı. Paylaşılan tabloda TTL'i zorlamak (`MATERIALIZE
        // TTL`) yan etkisi olan bir iş: aynı tablodaki başka testlerin
        // satırlarını da değerlendirir ve onları sebepsiz kaybettirebilir.
        // İzole veritabanında hem ucuz hem yalnızca bu testi ilgilendiriyor.
        _context = await stack.CreateIsolatedClickHouseContextAsync(
            TestContext.Current.CancellationToken);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(
            BaselineWindowMeasurement.RepoPath("db/clickhouse"),
            TestContext.Current.CancellationToken);

        var time = new FakeTimeProvider(Now);

        _sink = new ClickHouseEventSink(
            new EventWriter(_context),
            new EventNormalizer(time),
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ClickHouseEventSink>.Instance,
            time);
    }

    public async ValueTask DisposeAsync()
    {
        await _sink.DisposeAsync();
        _context.Dispose();
    }

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

        var connection = _context.Options.ConnectionString;

        // TTL AÇIKÇA uygulanıyor. Bunu beklemek yerine zorlamak zorunlu:
        // ClickHouse süresi dolmuş satırı insert anında atabilir de atmayabilir
        // de — ölçüldü, yerelde attı, CI'da atmadı. Beklemek testi merge
        // zamanlamasına bağlar; `mutations_sync = 2` mutasyon bitene kadar
        // döndürmüyor, yani sayım deterministik.
        await stack.QueryScalarAsync(
            connection,
            "ALTER TABLE events MATERIALIZE TTL SETTINGS mutations_sync = 2",
            TestContext.Current.CancellationToken);

        var tabloda = long.Parse(
            await stack.QueryScalarAsync(
                connection,
                $"SELECT count() FROM events WHERE owner_group = '{Group}'",
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
            OwnerGroup = Group,
            SourceId = "s02a",
            Body = Encoding.UTF8.GetBytes("gövde"),
        },
        "gövde",
        "utf-8",
        new ResolvedSource("s02a", Group, "firewall", "auto", string.Empty, IsKnown: true),
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

using System.Globalization;
using System.Net;
using Bizigo.Contracts;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <c>events.signature_hash</c> gerçek ClickHouse'a karşı (T29 / K35).
///
/// <para>
/// Buradaki asıl test <see cref="Hash_ClickHouse_un_kendi_xxHash64_u_ile_ayni"/>:
/// .NET'in ürettiği değeri veritabanının <b>kendi</b> <c>xxHash64()</c>
/// fonksiyonuna karşı doğruluyor. Yanlış hesaplanmış bir <c>signature_hash</c>
/// hiçbir yerde hata vermiyor — istisna atmıyor, sorgu düşürmüyor, yalnızca
/// RCA'nın iki sinyalini sessizce bozuyor. Bağımsız bir ikinci uygulama, o
/// sessizliği bozmanın tek yolu; K14'ün Python/.NET maskeleme ikizinin
/// yaptığının aynısı.
/// </para>
///
/// <para>
/// Yan kazanç: kolonun tipi ve varsayılanı gerçekten göç dosyasından geliyor,
/// testin kendi kurduğu bir şemadan değil.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class SignatureHashStorageTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(RepoPath("catalog/masks/bizigo-masks.yaml"));
    private static readonly DateTimeOffset Base = new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;
    private EventReader _reader = null!;

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();
        _writer = new EventWriter(_context);
        _reader = new EventReader(_context);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// .NET'in hash'i ile ClickHouse'un <c>xxHash64()</c>'ü <b>her golden örnekte</b>
    /// aynı. Ayrışırlarsa kolon sessizce yanlış demektir.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hash_ClickHouse_un_kendi_xxHash64_u_ile_ayni()
    {
        var checkedSamples = 0;

        foreach (var sample in Masks.Golden)
        {
            var signature = Masks.Compute(sample.Input);
            if (signature.IsEmpty)
            {
                continue;
            }

            var fromClickHouse = await stack.QueryScalarAsync(
                _context.Options.ConnectionString,
                $"SELECT xxHash64({Quote(signature.Text)})",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                signature.Hash.ToString(CultureInfo.InvariantCulture),
                fromClickHouse);

            checkedSamples++;
        }

        Assert.True(checkedSamples > 0, "Golden örneklerin hiçbiri imza üretmedi — ölçüm boşa koştu.");
    }

    /// <summary>
    /// Kolon gidiş-dönüş: yazılan değer aynen okunuyor. <c>UInt64</c>'ün üst
    /// yarısındaki bir değer bilerek seçildi — <c>Int64</c>'e düşen bir sürücü
    /// dönüşümü burada taşar ve tam da sessizce yanlış veri üretirdi.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kolon_gidis_donus_ust_yaridaki_degeri_bozmuyor()
    {
        const ulong High = 18_000_000_000_000_000_001UL;
        var group = "t29-" + Guid.NewGuid().ToString("N")[..8];
        var events = new[]
        {
            Sample(group, "s-1", High, Base),
            Sample(group, "s-1", SignatureHash.None, Base.AddSeconds(1)),
        };

        await _writer.WriteEventsAsync(events, TestContext.Current.CancellationToken);

        var page = await _reader.SearchAsync(
            new EventQuery
            {
                From = Base.AddMinutes(-5),
                To = Base.AddMinutes(5),
                OwnerGroups = [group],
                Limit = 10,
            },
            ScopePredicate.From(AccessScope.System("test")),
            TestContext.Current.CancellationToken);

        var stored = page.Events.ToDictionary(e => e.EventId, e => e.SignatureHash);

        Assert.Equal(High, stored[events[0].EventId]);
        Assert.Equal(SignatureHash.None, stored[events[1].EventId]);
    }

    /// <summary>
    /// Korelasyonun kendisi henüz T35'te, ama kolonun <b>onu taşıyabildiği</b>
    /// burada gösteriliyor: "baseline penceresinde yok, olay penceresinde var"
    /// saf SQL ile cevaplanıyor — sidecar yok, örnekleme yok, önbellek yok.
    ///
    /// <para>
    /// Bu ticket'ın gerekçesinin çalıştırılabilir hâli. Aynı sorgu
    /// <c>template_id</c> üzerinde koşsaydı, imzanın ilk görülüşünde o kolon boş
    /// olduğu için <b>hiçbir</b> satır dönmezdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ilk_gorulen_imza_saf_SQL_ile_cevaplaniyor()
    {
        var group = "t29-" + Guid.NewGuid().ToString("N")[..8];
        var known = Masks.Compute("Failed password for admin from 10.1.2.3 port 51234 ssh2").Hash;
        var novel = Masks.Compute("%BGP-5-ADJCHANGE: neighbor 10.9.9.9 Down BGP Notification sent").Hash;

        Assert.NotEqual(known, novel);

        // Baseline penceresi: yalnızca bilinen imza.
        // Olay penceresi: bilinen + ilk kez görülen.
        await _writer.WriteEventsAsync(
            [
                Sample(group, "rtr-1", known, Base.AddHours(-2)),
                Sample(group, "rtr-1", known, Base.AddHours(-1)),
                Sample(group, "rtr-1", known, Base),
                Sample(group, "rtr-2", novel, Base.AddSeconds(30)),
            ],
            TestContext.Current.CancellationToken);

        var sql = $"""
            SELECT signature_hash FROM events
            WHERE owner_group = '{group}' AND signature_hash != 0
              AND ts >= toDateTime64('{Iso(Base.AddMinutes(-5))}', 3)
            GROUP BY signature_hash
            HAVING signature_hash NOT IN (
                SELECT signature_hash FROM events
                WHERE owner_group = '{group}' AND signature_hash != 0
                  AND ts < toDateTime64('{Iso(Base.AddMinutes(-5))}', 3)
            )
            ORDER BY signature_hash
            FORMAT TSV
            """;

        var result = await stack.QueryScalarAsync(
            _context.Options.ConnectionString, sql, TestContext.Current.CancellationToken);

        Assert.Equal(novel.ToString(CultureInfo.InvariantCulture), result);
    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// Maskelenmiş imza <c>&lt;</c>, <c>&gt;</c> ve tırnak taşıyabiliyor; SQL'e
    /// gömülen tek yer bu test ve kaçış elle yapılıyor.
    /// </summary>
    private static string Quote(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static LogEvent Sample(string ownerGroup, string sourceId, ulong signatureHash, DateTimeOffset ts) => new()
    {
        EventId = Guid.NewGuid(),
        Timestamp = ts,
        OwnerGroup = ownerGroup,
        SourceId = sourceId,
        Host = sourceId,
        Vendor = "cisco",
        Product = "ios",
        ParserId = "cisco.ios",
        ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok,
        SignatureHash = signatureHash,
        SrcIp = IPAddress.Parse("10.0.0.5"),
        DstIp = IPAddress.Parse("10.0.0.9"),
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal),
        Body = "gövde",
    };

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (Bizigo.sln).")
            : Path.Combine(dir.FullName, relative);
    }
}

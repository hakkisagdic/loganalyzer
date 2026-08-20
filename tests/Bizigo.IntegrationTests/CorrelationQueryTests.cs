using System.Net;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Korelasyon sorgularının kendisi, gerçek ClickHouse'a karşı (T35).
///
/// <para>
/// Birim testleri sağlayıcıların <b>yorumunu</b> sınıyor (eşik, z-score, lift);
/// burada sınanan şey <b>SQL'in ne döndürdüğü</b>. İkisi ayrı olmak zorunda:
/// doğru yorumlanmış yanlış bir sorgu, tam olarak bu projenin en pahalı hata
/// sınıfı — hata yok, sayaç yok, yalnızca yanlış kanıt.
/// </para>
///
/// <para>
/// Koşturulduğunda kanıtlayacağı şey: anti-join'in gerçekten tabanı dışladığı,
/// <c>signature_hash = 0</c> satırlarının elendiği, kapsam filtresinin
/// korelasyona da uygulandığı, ve <c>countIf</c> pencerelerinin doğru
/// bölündüğü.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class CorrelationQueryTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;
    private CorrelationReader _reader = null!;
    private string _mine = null!;
    private string _theirs = null!;

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();
        _writer = new EventWriter(_context);
        _reader = new CorrelationReader(_context);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        var run = Guid.NewGuid().ToString("N")[..8];
        _mine = $"t35-mine-{run}";
        _theirs = $"t35-theirs-{run}";
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private CorrelationWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
        OwnerGroups = [_mine],
    };

    private ScopePredicate MyScope() => ScopePredicate.From(AccessScope.ForGroups("analyst", [_mine]));

    /// <summary>
    /// Anti-join gerçekten tabanı dışlıyor: tabanda da olan imza dönmüyor,
    /// yalnızca pencerede beliren dönüyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ilk_gorulen_yalnizca_tabanda_olmayani_donduruyor()
    {
        const ulong Known = 1001;
        const ulong Novel = 2002;

        await _writer.WriteEventsAsync(
            [
                Event(_mine, "rtr-1", Known, Now.AddDays(-3)),
                Event(_mine, "rtr-1", Known, Now.AddMinutes(5)),
                Event(_mine, "rtr-2", Novel, Now.AddMinutes(10)),
                Event(_mine, "rtr-3", Novel, Now.AddMinutes(11)),
            ],
            TestContext.Current.CancellationToken);

        var rows = await _reader.GetFirstSeenSignaturesAsync(
            Window(), MyScope(), 100, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(Novel, row.SignatureHash);
        Assert.Equal(2, row.EventCount);

        // İki farklı kaynakta belirdi — ağırlığın kaynağı bu.
        Assert.Equal(2, row.SourceCount);
    }

    /// <summary>
    /// <b>T29'un bulgusu, sorgu tarafında.</b> <c>signature_hash = 0</c>
    /// "imza yok" demek (16 KB maskeleme sınırını aşan satırlar). Elenmezse
    /// hepsi tek bir sahte imzada toplanır ve o küme her pencerede "ilk kez
    /// görüldü" gibi davranır.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Imzasiz_satirlar_korelasyona_girmiyor()
    {
        await _writer.WriteEventsAsync(
            [
                Event(_mine, "rtr-1", 0, Now.AddMinutes(5)),
                Event(_mine, "rtr-2", 0, Now.AddMinutes(6)),
            ],
            TestContext.Current.CancellationToken);

        var firstSeen = await _reader.GetFirstSeenSignaturesAsync(
            Window(), MyScope(), 100, TestContext.Current.CancellationToken);
        var volume = await _reader.GetSignatureVolumeAsync(
            Window(), MyScope(), 100, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(firstSeen, row => row.SignatureHash == 0);
        Assert.DoesNotContain(volume, row => row.SignatureHash == 0);
    }

    /// <summary>
    /// <b>Kabul kriteri:</b> kapsam dışı veri hiçbir sinyalde görünmüyor.
    ///
    /// <para>
    /// Başka grubun imzası hem "ilk-görülen" listesinde olmamalı hem de tabanda
    /// sayılmamalı — ikincisi daha sinsi: sayılsaydı kendi yeni imzam "tabanda
    /// vardı" diye elenir ve sinyal sessizce kaybolurdu.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kapsam_disi_veri_hicbir_sinyalde_gorunmuyor()
    {
        const ulong Shared = 3003;

        await _writer.WriteEventsAsync(
            [
                // Başka grup bu imzayı tabanda görmüş.
                Event(_theirs, "their-rtr", Shared, Now.AddDays(-2)),

                // Bende ilk kez pencerede beliriyor.
                Event(_mine, "rtr-1", Shared, Now.AddMinutes(5)),
            ],
            TestContext.Current.CancellationToken);

        var rows = await _reader.GetFirstSeenSignaturesAsync(
            Window(), MyScope(), 100, TestContext.Current.CancellationToken);

        // Kapsam dışı taban benim sinyalimi bastırmıyor.
        Assert.Contains(rows, row => row.SignatureHash == Shared);

        // Ve başka grubun kaynağı hiçbir yerde görünmüyor.
        var propagation = await _reader.GetPropagationAsync(
            Window(), MyScope(), 3, 100, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(propagation, row => row.OwnerGroup == _theirs);
    }

    /// <summary>
    /// <c>countIf</c> pencereleri doğru bölüyor: aynı imzanın pencere ve taban
    /// sayımları ayrı ayrı doğru.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hacim_iki_pencereyi_ayri_sayiyor()
    {
        const ulong Busy = 4004;

        var events = new List<LogEvent>();
        for (var index = 0; index < 3; index++)
        {
            events.Add(Event(_mine, "rtr-1", Busy, Now.AddMinutes(index)));
        }

        for (var index = 0; index < 7; index++)
        {
            events.Add(Event(_mine, "rtr-1", Busy, Now.AddDays(-2).AddMinutes(index)));
        }

        await _writer.WriteEventsAsync(events, TestContext.Current.CancellationToken);

        var row = Assert.Single(
            await _reader.GetSignatureVolumeAsync(Window(), MyScope(), 100, TestContext.Current.CancellationToken),
            r => r.SignatureHash == Busy);

        Assert.Equal(3, row.WindowCount);
        Assert.Equal(7, row.BaselineCount);
    }

    /// <summary>
    /// Yayılma "bozulma"yı ayrıştırma hatası <b>veya</b> düşük önem derecesi
    /// olarak tanımlıyor, ve sağlıklı satırları saymıyor.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yayilma_yalnizca_bozulan_olaylari_sayiyor()
    {
        await _writer.WriteEventsAsync(
            [
                Event(_mine, "rtr-early", 5005, Now.AddMinutes(2)) with { SeverityNum = 3 },
                Event(_mine, "rtr-late", 5005, Now.AddMinutes(9)) with { ParseStatus = ParseStatus.Failed },

                // Sağlıklı: severity 6 (info) ve ayrıştırma başarılı.
                Event(_mine, "rtr-healthy", 5005, Now.AddMinutes(1)) with { SeverityNum = 6 },
            ],
            TestContext.Current.CancellationToken);

        var rows = await _reader.GetPropagationAsync(
            Window(), MyScope(), 3, 100, TestContext.Current.CancellationToken);

        Assert.Equal(["rtr-early", "rtr-late"], rows.Select(r => r.SourceId));

        // Sıra zamana göre: ilk bozulan başta.
        Assert.True(rows[0].FirstDegradedAt < rows[1].FirstDegradedAt);
    }

    /// <summary>
    /// <c>time_source != parsed</c> olan olaylar sayılıyor — sıralamanın
    /// güvenilirliği hakkındaki tek dürüst bilgi bu.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Guvenilmez_zamanli_olaylar_sayiliyor()
    {
        await _writer.WriteEventsAsync(
            [
                Event(_mine, "rtr-1", 6006, Now.AddMinutes(2)) with
                {
                    SeverityNum = 3, TimeSource = TimeSources.Observed,
                },
                Event(_mine, "rtr-1", 6006, Now.AddMinutes(3)) with
                {
                    SeverityNum = 3, TimeSource = TimeSources.Parsed,
                },
            ],
            TestContext.Current.CancellationToken);

        var row = Assert.Single(
            await _reader.GetPropagationAsync(Window(), MyScope(), 3, 100, TestContext.Current.CancellationToken),
            r => r.SourceId == "rtr-1");

        Assert.Equal(2, row.DegradedCount);
        Assert.Equal(1, row.UnreliableTimeCount);
    }

    /// <summary>
    /// İzin listesinde olmayan alan <b>istisna fırlatıyor</b>, sessizce
    /// atlanmıyor: atlansaydı kullanıcı uygulanmayan bir alana bakıldığını
    /// sanırdı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Izin_listesi_disi_alan_reddediliyor()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.GetAttributeLiftAsync(
            Window(), MyScope(), ["body"], 10, TestContext.Current.CancellationToken));
    }

    /// <summary>Ortak öznitelik alan başına pencere ve taban sayımlarını döndürüyor.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ortak_oznitelik_alan_basina_sayiyor()
    {
        await _writer.WriteEventsAsync(
            [
                Event(_mine, "rtr-1", 7007, Now.AddMinutes(1)) with { Host = "core-sw-02" },
                Event(_mine, "rtr-2", 7007, Now.AddMinutes(2)) with { Host = "core-sw-02" },
                Event(_mine, "rtr-3", 7007, Now.AddDays(-1)) with { Host = "core-sw-02" },
            ],
            TestContext.Current.CancellationToken);

        var rows = await _reader.GetAttributeLiftAsync(
            Window(), MyScope(), ["host"], 10, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows, r => r.Value == "core-sw-02");

        Assert.Equal("host", row.Field);
        Assert.Equal(2, row.WindowCount);
        Assert.Equal(1, row.BaselineCount);
    }

    private static LogEvent Event(string ownerGroup, string sourceId, ulong signature, DateTimeOffset ts) => new()
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
        SignatureHash = signature,
        TimeSource = TimeSources.Parsed,
        SeverityNum = 6,
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal),
        Body = $"gövde {signature}",
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

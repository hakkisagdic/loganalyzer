using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <c>change_events</c> kapsam dışı sayımı (T34, K17, RCA §3.2) — gerçek
/// ClickHouse'a karşı.
///
/// <para>
/// Bu sorgunun tek işi bir <b>sayı</b> döndürmek ve <b>hiçbir içerik</b>
/// sızdırmamak. Testin kırmızı yanması gereken iki yol var: sayı yanlışsa
/// (kapsam dışı değişiklikler görünmez kalır ve RCA yanlış güvenle koşar) ve
/// negatif kapsam koşulu ters uygulanmışsa (kullanıcı kendi verisini "dışarıda"
/// sanır). İkincisi daha sinsi: sonuç dolu döner ve doğru görünür.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ChangeOutOfScopeCountTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;
    private ChangeEventReader _reader = null!;
    private string _mine = null!;
    private string _theirs = null!;

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();
        _writer = new EventWriter(_context);
        _reader = new ChangeEventReader(_context);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);

        // Her koşu kendi gruplarını kullanıyor: paylaşılan bir grup adı, başka
        // bir testin satırlarını bu testin sayımına karıştırırdı.
        var run = Guid.NewGuid().ToString("N")[..8];
        _mine = $"t34-mine-{run}";
        _theirs = $"t34-theirs-{run}";

        await _writer.WriteChangeEventsAsync(
            [
                Change(_mine, "acl.push", Base),
                Change(_mine, "acl.push", Base.AddMinutes(5)),
                Change(_theirs, "firmware.upgrade", Base.AddMinutes(1)),
                Change(_theirs, "acl.push", Base.AddMinutes(2)),
                Change(_theirs, "acl.push", Base.AddMinutes(3)),
            ],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private ChangeQuery Window() => new()
    {
        From = Base.AddMinutes(-10),
        To = Base.AddMinutes(10),
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kapsam_disindaki_degisiklikler_sayiliyor()
    {
        var scope = ScopePredicate.From(AccessScope.ForGroups("analyst", [_mine]));

        var count = await _reader.CountOutOfScopeAsync(Window(), scope, TestContext.Current.CancellationToken);

        // Yalnızca *bu koşunun* diğer grubu; başka testlerin satırları da
        // kapsam dışında olduğu için eşitlik değil, alt sınır iddia ediliyor.
        Assert.True(count >= 3, $"Kapsam dışı sayım {count}, en az 3 bekleniyordu.");

        // Ve kendi satırları sayıma girmiyor: dışarısı toplamdan küçük olmalı.
        var mine = await _reader.SearchAsync(
            Window() with { OwnerGroups = [_mine] }, scope, TestContext.Current.CancellationToken);

        Assert.Equal(2, mine.Count);
    }

    /// <summary>
    /// Filtre kapsam dışına da uygulanıyor: "ilgili" sayımın anlamı, aynı
    /// soruyu dışarıda sormak. Uygulanmasaydı rapor alakasız değişiklikleri
    /// sayıp "kapsamınız dışında 342 ilişkili değişiklik var" derdi.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Filtre_kapsam_disina_da_uygulaniyor()
    {
        var scope = ScopePredicate.From(AccessScope.ForGroups("analyst", [_mine]));

        var all = await _reader.CountOutOfScopeAsync(
            Window(), scope, TestContext.Current.CancellationToken);

        var firmwareOnly = await _reader.CountOutOfScopeAsync(
            Window() with { ChangeKinds = ["firmware.upgrade"] },
            scope,
            TestContext.Current.CancellationToken);

        Assert.True(firmwareOnly < all, $"Filtreli sayım ({firmwareOnly}) filtresizden ({all}) küçük olmalı.");
        Assert.True(firmwareOnly >= 1);
    }

    /// <summary>
    /// Sınırsız kapsamda "dışarısı" <b>yok</b>: her şeyi gören biri için
    /// "kapsamınız dışında N var" cümlesi anlamsız ve yanıltıcı olurdu.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sinirsiz_kapsamda_disarisi_yok()
    {
        var count = await _reader.CountOutOfScopeAsync(
            Window(),
            ScopePredicate.From(AccessScope.System("test")),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    private static ChangeEvent Change(string ownerGroup, string kind, DateTimeOffset ts) => new()
    {
        ChangeId = Guid.NewGuid(),
        Timestamp = ts,
        OwnerGroup = ownerGroup,
        TargetKind = ChangeTargetKind.Config,
        TargetId = "core-sw-02",
        ChangeKind = kind,
        Actor = "m.yilmaz",
        Summary = "test",
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

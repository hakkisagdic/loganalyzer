using Bizigo.ControlPlane;
using Bizigo.Contracts;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// Idempotency yarışının <b>kaybeden</b> tarafı (T45 kusuru, T46'da düzeltildi).
///
/// <para>
/// Kusur entegrasyon paketinde çıktı: iki eşzamanlı istek de "bu anahtar yok"
/// gördü, ikisi de ekledi, ikincisi benzersiz indekse çarptı ve <b>istisna
/// kullanıcıya çıktı</b>. Yani <i>"aynı anahtar → aynı koşum"</i> garantisi tam
/// da garantinin gerektiği anda kırılıyordu; eşzamanlılık yokken idempotency
/// zaten kolay.
/// </para>
///
/// <para>
/// <b>Neden burada da bir test var.</b> Gerçek yarışı yalnızca Postgres'in
/// benzersiz indeksi üretebiliyor ve o test entegrasyon paketinde
/// (<c>RcaAdmissionStoreTests</c>). Buradaki test farklı bir soru soruyor:
/// <i>çarpışma geldiğinde kapı ne yapıyor?</i> Çarpışmayı bir
/// <see cref="ISaveChangesInterceptor"/> enjekte ediyor — yani indeksin var
/// olduğunu değil, <b>kaybedenin davranışını</b> sınıyor. İkisi ayrı iddia ve
/// ikisi de gerekli.
/// </para>
/// </summary>
public sealed class RcaAdmissionRaceTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly RaceInjectingFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private RcaAdmission Admission() =>
        new(_factory, new AlwaysAllowQuotaGate(), NullLogger<RcaAdmission>.Instance, new FixedTime(Noon));

    private static RcaTriggerRequest Request(string key) => new()
    {
        Source = RcaTriggerSource.External,
        Identity = "integration-client",
        OwnerGroup = "network/core",
        WindowFrom = Noon.AddMinutes(-15),
        WindowTo = Noon,
        IdempotencyKey = key,
    };

    /// <summary>
    /// <b>Kaybeden istek de başarılı bir cevap alıyor</b> — çağıran açısından
    /// ikisi de "aynı anahtarla istedim" diyor.
    ///
    /// <para>
    /// <c>Existing: true</c> dönmesi ayrıca uçtaki 200/201 ayrımını taşıyor:
    /// bu istek hiçbir şey <i>yaratmadı</i>, dolayısıyla 200. Kabul edilen yeni
    /// koşum 201 alıyor. Aynı koda koymak, çağıranın <i>"benim isteğim mi koşum
    /// başlattı"</i> sorusunu cevapsız bırakırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yarisi_kaybeden_istek_var_olan_kosumu_aliyor()
    {
        var winner = _factory.ArmRaceWith("key-42");

        var result = await Admission().AdmitAsync(Request("key-42"), Token);

        Assert.True(result.Existing);
        Assert.Equal(winner, result.Run.Id);

        // Ve tek satır kaldı: kaybeden kendi satırını yazmadı.
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.IdempotencyKey == "key-42", Token));
    }

    /// <summary>
    /// <b>Çarpışmayı yutmak yasak.</b> Anahtar yoksa hata benzersiz indeksten
    /// gelmiyor demektir; sessizce "var olanı döndürdüm" demek koşumun hiç
    /// yazılmadığını gizlerdi — §7'nin adını koyduğu sınıf.
    /// </summary>
    [Fact]
    public async Task Anahtarsiz_carpismada_istisna_yutulmuyor()
    {
        _factory.ArmFailureWithoutWinner();

        var request = new RcaTriggerRequest
        {
            Source = RcaTriggerSource.Manual,
            Identity = "analyst",
            OwnerGroup = "network/core",
            WindowFrom = Noon.AddMinutes(-15),
            WindowTo = Noon,
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => Admission().AdmitAsync(request, Token));
    }

    /// <summary>
    /// Anahtar var ama satır yok: yarış değil, başka bir kısıt. Yine yutulmuyor.
    /// </summary>
    [Fact]
    public async Task Anahtarli_ama_kazanansiz_carpismada_da_yutulmuyor()
    {
        _factory.ArmFailureWithoutWinner();

        await Assert.ThrowsAsync<DbUpdateException>(() => Admission().AdmitAsync(Request("key-7"), Token));
    }
}

/// <summary>
/// Tek kaydetmede çarpışma üreten fabrika.
///
/// <para>
/// Yarışı iki iş parçacığıyla kovalamak yerine <b>sonucunu</b> enjekte ediyor:
/// gerçek zamanlamaya bağlı bir test, geçme sebebi duvar saatiyle ilgili bir
/// test olurdu (§6). Burada sınanan şey zamanlama değil, çarpışma geldiğinde
/// verilen karar.
/// </para>
/// </summary>
internal sealed class RaceInjectingFactory : IDbContextFactory<ControlPlaneDbContext>, IDisposable
{
    private readonly DbContextOptions<ControlPlaneDbContext> _options;
    private readonly RaceInterceptor _interceptor = new();

    public RaceInjectingFactory()
    {
        var name = Guid.NewGuid().ToString();

        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(_interceptor)
            .Options;

        Plain = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    /// <summary>Kesici olmayan seçenekler: kazananı yazmak ve doğrulamak için.</summary>
    private DbContextOptions<ControlPlaneDbContext> Plain { get; }

    public ControlPlaneDbContext CreateDbContext() => new(Plain);

    /// <summary>Kapının kullandığı bağlam — kesici burada devrede.</summary>
    public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ControlPlaneDbContext(_options));

    /// <summary>
    /// Kazanan satırı şimdi yazar ve sıradaki kaydetmeyi düşürür — yani kapı
    /// "yok" gördükten <b>sonra</b> satırın belirdiği pencereyi taklit eder.
    /// </summary>
    public Guid ArmRaceWith(string idempotencyKey)
    {
        var winner = new RcaRunEntity
        {
            OwnerGroup = "network/core",
            Source = RcaTriggerSource.External,
            IdempotencyKey = idempotencyKey,
            State = RcaRunState.Queued,
            Accepted = true,
            CountsAgainstQuota = true,
        };

        _interceptor.Arm(() =>
        {
            using var db = new ControlPlaneDbContext(Plain);
            db.RcaRuns.Add(winner);
            db.SaveChanges();
        });

        return winner.Id;
    }

    public void ArmFailureWithoutWinner() => _interceptor.Arm(static () => { });

    public void Dispose()
    {
        using var db = CreateDbContext();
        db.Database.EnsureDeleted();
    }
}

internal sealed class RaceInterceptor : SaveChangesInterceptor
{
    private Action? _onNextSave;

    public void Arm(Action onNextSave) => _onNextSave = onNextSave;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_onNextSave is { } action)
        {
            // Tek atış: düzeltmenin yeniden okuması aynı tuzağa düşmesin.
            _onNextSave = null;
            action();

            throw new DbUpdateException(
                "duplicate key value violates unique constraint \"ix_rca_runs_idempotency_key\"");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Sabit saat; <c>FakeTimeProvider</c>'ın ilerletme yüzeyi gerekmiyor.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

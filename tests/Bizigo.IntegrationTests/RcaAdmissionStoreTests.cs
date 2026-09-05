using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Kabul kapısının <b>gerçek</b> Postgres'e karşı sözleşmesi (T45).
///
/// <para>
/// Birim testleri kapıyı EF'in bellek içi sağlayıcısıyla sınıyor ve o sağlayıcı
/// <b>hiçbir indeksi zorlamıyor</b>. Yani "aynı <c>Idempotency-Key</c> ikinci bir
/// koşum açmıyor" birim testinde yalnızca <i>uygulama katmanındaki</i> kontrolü
/// kanıtlıyor: kapı önce okuyor, bulamazsa yazıyor, ve o iki adımın arasında bir
/// yarış penceresi kalıyor. Pencereyi kapatan şey filtreli tekil indeks; onun
/// göçte gerçekten kurulduğunu ancak buradaki testler gösteriyor.
/// </para>
///
/// <para>
/// <b>Koşturulmadı</b> (CLAUDE.md §2 — konteyner gerektiriyor). Koşturulduğunda
/// kanıtlayacağı şey her testin özetinde yazılı.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class RcaAdmissionStoreTests(DevStackFixture stack) : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(Token);
        await db.Database.MigrateAsync(Token);
        await db.RcaRuns.ExecuteDeleteAsync(Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RcaRunEntity Run(string? idempotencyKey) => new()
    {
        RootRunId = Guid.NewGuid(),
        Source = RcaTriggerSource.External,
        TriggerIdentity = "svc",
        OwnerGroup = "network/core",
        WindowFrom = Now.AddHours(-1),
        WindowTo = Now,
        DebounceKey = $"api|svc|network/core|{Guid.NewGuid()}",
        LineageKey = "api|svc|network/core",
        IdempotencyKey = idempotencyKey,
        Accepted = true,
        RequestedAt = Now,
        RequestedBy = "svc",
    };

    /// <summary>
    /// Koşturulduğunda kanıtlar: <c>idempotency_key</c> üzerindeki tekil indeks
    /// göçte gerçekten kuruluyor ve ikinci kaydı <b>veritabanı</b> reddediyor.
    ///
    /// <para>
    /// Uygulama katmanı bunu zaten kontrol ediyor; bu test o kontrolün
    /// altındaki ağı sınıyor. Ağ yoksa iki eşzamanlı istek aynı anahtarla iki
    /// koşum açar — ve idempotent bir istemcinin tekrar denemesi tam da
    /// eşzamanlı gelir.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Idempotency_anahtari_veritabaninda_tekil()
    {
        await using var db = await _factory.CreateDbContextAsync(Token);

        db.RcaRuns.Add(Run("kilit-1"));
        await db.SaveChangesAsync(Token);

        db.RcaRuns.Add(Run("kilit-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    /// <summary>
    /// Koşturulduğunda kanıtlar: indeksin <b>filtresi</b> çalışıyor — anahtarsız
    /// koşumlar birbirini engellemiyor.
    ///
    /// <para>
    /// Bu testi ayrı yazmak şart. Filtresiz bir tekil indeks Postgres'te
    /// birden çok <c>NULL</c>'a izin verdiği için üstteki test <b>filtre
    /// olmadan da geçerdi</b>; yani tek başına indeksin doğru kurulduğunu
    /// göstermiyor. Dört kaynaktan üçü anahtarsız çalışıyor — filtre yanlış
    /// yazılsaydı kırılan şey API değil <b>diğer üç kaynak</b> olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Anahtarsiz_kosumlar_birbirini_engellemiyor()
    {
        await using var db = await _factory.CreateDbContextAsync(Token);

        db.RcaRuns.Add(Run(idempotencyKey: null));
        db.RcaRuns.Add(Run(idempotencyKey: null));
        db.RcaRuns.Add(Run(idempotencyKey: null));

        await db.SaveChangesAsync(Token);

        Assert.Equal(3, await db.RcaRuns.CountAsync(r => r.IdempotencyKey == null, Token));
    }

    /// <summary>
    /// Koşturulduğunda kanıtlar: aynı anahtarla gelen <b>eşzamanlı</b> iki
    /// istek tek koşum bırakıyor — kapının okuma/yazma penceresi kısıtla
    /// kapatılmış.
    ///
    /// <para>
    /// Birim testinin ulaşamadığı tek senaryo bu: orada iki çağrı sırayla
    /// koşuyor, dolayısıyla ikincisi birincinin kaydını her zaman görüyor.
    /// Gerçekte görmeyebilir.
    /// </para>
    ///
    /// <para>
    /// Kapının bugünkü hâli yarışı yakalayıp <see cref="DbUpdateException"/>'ı
    /// yutmuyor; bu test kırmızı yanarsa cevap <b>yeniden okuyup var olan
    /// koşumu döndürmek</b>, kısıtı gevşetmek değil.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ayni_anahtarla_eszamanli_iki_istek_tek_kosum_birakiyor()
    {
        var gate = new RcaAdmission(
            _factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System);

        RcaTriggerRequest Request() => RcaTriggerSources.FromExternal(
            "svc", "yaris-1", ["network/core"], Now.AddHours(-1), Now);

        var results = await Task.WhenAll(
            Task.Run(() => gate.AdmitAsync(Request(), Token), Token),
            Task.Run(() => gate.AdmitAsync(Request(), Token), Token));

        await using var db = await _factory.CreateDbContextAsync(Token);

        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.IdempotencyKey == "yaris-1", Token));

        // Ve iki istek de aynı koşumu görüyor: biri kabul, diğeri "zaten var".
        Assert.Single(results.Select(r => r.Run.Id).Distinct());
    }

    /// <summary>
    /// Koşturulduğunda kanıtlar: debounce sorgusu gerçek şemada da yalnızca
    /// <b>kabul edilmiş</b> koşumlara bakıyor, ve <c>rejected</c> satırları
    /// tabloda kalıyor.
    ///
    /// <para>
    /// Reddedilenlerin aynı tabloda durması bilinçli (RCA §5): "neden RCA
    /// üretilmedi" sorusu tek yerden cevaplanmalı. Ama o karar debounce
    /// sorgusuna bir tuzak kuruyor — reddedilmiş bir satır sonraki meşru
    /// talebi bastırırsa alarm fırtınası kendini kalıcılaştırır.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ret_kaydi_sonraki_mesru_talebi_bastirmiyor()
    {
        var gate = new RcaAdmission(
            _factory,
            new RejectingQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System);

        var rejected = await gate.AdmitAsync(
            RcaTriggerSources.FromSchedule("gunluk", ["network/core"], Now.AddHours(-1), Now), Token);

        Assert.False(rejected.Run.Accepted);
        Assert.Equal(RcaRejectionReason.QuotaExceeded, rejected.Run.Rejection);

        var open = new RcaAdmission(
            _factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System);

        var accepted = await open.AdmitAsync(
            RcaTriggerSources.FromSchedule("gunluk", ["network/core"], Now.AddHours(-1), Now), Token);

        Assert.True(accepted.Run.Accepted);

        await using var db = await _factory.CreateDbContextAsync(Token);
        Assert.Equal(2, await db.RcaRuns.CountAsync(Token));
    }

    /// <summary>
    /// Koşturulduğunda kanıtlar: <b>idempotency anahtarının süresi dolmuyor</b>
    /// — aradan aylar geçse de aynı anahtar aynı koşumu döndürüyor (T57).
    ///
    /// <para>
    /// Bu bir <b>kararın</b> bekçisi, bir eksikliğin değil.
    /// <c>RcaAdmission</c>'ın idempotency sorgusunda zaman sınırı yok ve
    /// olmamasının sebebi şemada: <c>idempotency_key</c> üzerinde filtreli
    /// TEKİL indeks var. Sorguya pencere koymak <b>etkisiz</b> olurdu — pencere
    /// eski anahtarı atlasa bile <c>INSERT</c> indekse çarpar, yarış yolu
    /// devreye girer ve <b>yine eski koşum</b> döner.
    /// </para>
    ///
    /// <para>
    /// <b>Zaman denklemden çıkarılmıyor, tam tersine ÖLÇÜLÜYOR</b> — ama duvar
    /// saatiyle değil: kabul kapısına altı ay ileri kurulmuş bir
    /// <see cref="TimeProvider"/> veriliyor ve testin geçme sebebi o farkın
    /// <b>hiçbir şeyi değiştirmemesi</b>. Sabit bir süre beklemek yerine saati
    /// taşımak, §6'nın "geçme sebebi duvar saatiyle ilgili olmamalı" kuralının
    /// doğru uygulaması.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Idempotency_anahtarinin_suresi_dolmuyor()
    {
        const string key = "istemci-anahtari-2026-08";

        var gate = new RcaAdmission(
            _factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));

        var first = await gate.AdmitAsync(
            RcaTriggerSources.FromExternal("svc", key, ["network/core"], Now.AddHours(-1), Now), Token);

        Assert.False(first.Existing);
        Assert.True(first.Run.Accepted);

        // ALTI AY SONRA, aynı anahtar. Kapı yeni bir saatle kuruluyor: geçen
        // sürenin gerçekten uygulamaya görünmesi için.
        var later = Now.AddMonths(6);

        var aged = new RcaAdmission(
            _factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(later));

        var second = await aged.AdmitAsync(
            RcaTriggerSources.FromExternal("svc", key, ["network/core"], later.AddHours(-1), later), Token);

        // AYNI koşum, yeni kayıt YOK — pencere talebi bile farklı olmasına rağmen.
        Assert.True(second.Existing);
        Assert.Equal(first.Run.Id, second.Run.Id);

        await using var db = await _factory.CreateDbContextAsync(Token);

        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.IdempotencyKey == key, Token));
    }

    private sealed class RejectingQuotaGate : IRcaQuotaGate
    {
        public ValueTask<RcaRejectionReason> CheckAsync(
            RcaTriggerRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(RcaRejectionReason.QuotaExceeded);
    }
}

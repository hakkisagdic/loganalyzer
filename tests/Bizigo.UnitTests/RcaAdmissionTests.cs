using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Kabul kapısının bekçileri (T45, RCA §5).
///
/// <para>
/// Hiçbiri duvar saati beklemiyor: "şimdi" <see cref="FakeTimeProvider"/>'dan
/// geliyor ve debounce penceresi saat ileri alınarak aşılıyor.
/// </para>
/// </summary>
public sealed class RcaAdmissionTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _factory.Dispose();

    private RcaAdmission Gate(IRcaQuotaGate? quota = null) => new(
        _factory,
        quota ?? new AlwaysAllowQuotaGate(),
        NullLogger<RcaAdmission>.Instance,
        _time);

    private static RcaTriggerRequest Alert(string ruleId, RcaRunEntity? parent = null) =>
        RcaTriggerSources.FromAlert(
            new AlertTriggerEntity
            {
                RuleId = Guid.Parse(ruleId),
                OwnerGroup = "network/core",
                WindowFrom = Now.AddMinutes(-15),
                WindowTo = Now,
                Summary = "sınama",
            },
            parent);

    private static readonly string RuleA = "aaaaaaaa-0000-4000-8000-000000000000";
    private static readonly string RuleB = "bbbbbbbb-0000-4000-8000-000000000000";

    // --- Debounce ----------------------------------------------------------

    /// <summary>
    /// Kabul kriteri: aynı <c>(rule_id, kapsam, pencere)</c> ikinci kez gelince
    /// <b>ikinci koşum doğmuyor</b>. Alarm fırtınasının karşılığı: 500 alarm → 1 RCA.
    /// </summary>
    [Fact]
    public async Task Ayni_pencerede_ikinci_talep_kosum_dogurmuyor()
    {
        var gate = Gate();

        var first = await gate.AdmitAsync(Alert(RuleA), Token);
        var second = await gate.AdmitAsync(Alert(RuleA), Token);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(RcaRejectionReason.Debounced, second.Rejection);

        await using var db = _factory.CreateDbContext();

        // İki KAYIT var (ret de kayda geçiyor) ama tek KABUL.
        Assert.Equal(2, await db.RcaRuns.CountAsync(Token));
        Assert.Equal(1, await db.RcaRuns.CountAsync(r => r.Accepted, Token));
    }

    [Fact]
    public async Task Pencere_dolunca_yeniden_kabul_ediliyor()
    {
        var gate = Gate();

        await gate.AdmitAsync(Alert(RuleA), Token);

        _time.Advance(RcaTriggerKey.DebounceWindow + TimeSpan.FromMinutes(1));

        Assert.True((await gate.AdmitAsync(Alert(RuleA), Token)).Accepted);
    }

    [Fact]
    public async Task Farkli_kural_ayni_pencerede_debounce_edilmiyor()
    {
        var gate = Gate();

        Assert.True((await gate.AdmitAsync(Alert(RuleA), Token)).Accepted);
        Assert.True((await gate.AdmitAsync(Alert(RuleB), Token)).Accepted);
    }

    /// <summary>
    /// Reddedilen bir kayıt sonraki meşru talebi de reddetseydi bir ret kendini
    /// çoğaltırdı: debounce yalnızca <b>kabul edilmiş</b> koşumlara bakıyor.
    /// </summary>
    [Fact]
    public async Task Reddedilen_kayit_sonraki_talebi_debounce_etmiyor()
    {
        var gate = Gate();

        await gate.AdmitAsync(Alert(RuleA), Token);
        await gate.AdmitAsync(Alert(RuleA), Token);   // reddedildi

        _time.Advance(RcaTriggerKey.DebounceWindow + TimeSpan.FromMinutes(1));

        Assert.True((await gate.AdmitAsync(Alert(RuleA), Token)).Accepted);
    }

    // --- Soyağacı ----------------------------------------------------------

    /// <summary>
    /// Kabul kriteri: <c>A → B → A</c> reddediliyor ve sebep <b>ata tekrarı</b>,
    /// <c>depth</c> değil.
    ///
    /// <para>
    /// İkisini ayrı sınamak şart: <c>depth ≤ 2</c> bu döngüyü <b>kısaltır ama
    /// engellemez</b> — A ikinci kez koşar, aynı rapor iki kez üretilir, kota iki
    /// kez ödenir. Sebep <c>DepthExceeded</c> olarak kaydedilseydi "derinlik
    /// yetiyor" yanılsaması testte de kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dongu_ata_tekrari_olarak_reddediliyor_derinlik_olarak_degil()
    {
        var gate = Gate();

        var a = await gate.AdmitAsync(Alert(RuleA), Token);
        Assert.True(a.Accepted);

        // B, A'nın devamı — derinlik 1, farklı anahtar.
        var b = await gate.AdmitAsync(Alert(RuleB, a.Run), Token);
        Assert.True(b.Accepted);
        Assert.Equal(1, b.Run.Depth);

        // A tekrar, B'nin devamı olarak: derinlik 2 ama asıl mesele döngü.
        var again = await gate.AdmitAsync(Alert(RuleA, b.Run), Token);

        Assert.False(again.Accepted);
        Assert.Equal(RcaRejectionReason.AncestorRepeat, again.Rejection);
        Assert.NotEqual(RcaRejectionReason.DepthExceeded, again.Rejection);
        Assert.Contains("soyağacında", again.Run.RejectionDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Döngü <b>pencere sınırını aşsa bile</b> yakalanıyor.
    ///
    /// <para>
    /// Soyağacı anahtarında pencere kovası <b>yok</b> ve sebebi bu: zincirler
    /// zaman alıyor (B'nin koşup bulgu üretmesi dakikalar sürüyor), yani pencere
    /// dahil edilseydi gerçek döngülerin çoğu kontrolden kaçardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dongu_pencere_sinirini_assa_bile_yakalaniyor()
    {
        var gate = Gate();

        var a = await gate.AdmitAsync(Alert(RuleA), Token);

        _time.Advance(TimeSpan.FromMinutes(20));   // iki pencere ileri
        var b = await gate.AdmitAsync(Alert(RuleB, a.Run), Token);

        _time.Advance(TimeSpan.FromMinutes(20));
        var again = await gate.AdmitAsync(Alert(RuleA, b.Run), Token);

        Assert.Equal(RcaRejectionReason.AncestorRepeat, again.Rejection);
    }

    /// <summary>
    /// Döngü <b>olmadan</b> derinlik sınırına çarpmak farklı bir şey söylüyor ve
    /// farklı kaydediliyor: <c>A → B → C</c>.
    /// </summary>
    [Fact]
    public async Task Dongusuz_zincir_derinlik_olarak_reddediliyor()
    {
        var gate = Gate();

        var a = await gate.AdmitAsync(Alert(RuleA), Token);
        var b = await gate.AdmitAsync(Alert(RuleB, a.Run), Token);

        var c = await gate.AdmitAsync(Alert("cccccccc-0000-4000-8000-000000000000", b.Run), Token);

        Assert.False(c.Accepted);
        Assert.Equal(RcaRejectionReason.DepthExceeded, c.Rejection);
        Assert.NotEqual(RcaRejectionReason.AncestorRepeat, c.Rejection);
    }

    [Fact]
    public async Task Kok_kosum_kendi_kokudur()
    {
        var a = await Gate().AdmitAsync(Alert(RuleA), Token);

        Assert.Equal(a.Run.Id, a.Run.RootRunId);
        Assert.Equal(0, a.Run.Depth);
        Assert.Null(a.Run.ParentRunId);
    }

    /// <summary>
    /// Devam kuralı yeni bir <b>kaynak</b> yaratmıyor: kaynak kökten miras
    /// alınıyor. Enum'da beşinci bir değer olmamasının kod tarafındaki karşılığı.
    /// </summary>
    [Fact]
    public async Task Devam_kosumu_kaynagini_kokunden_aliyor()
    {
        var gate = Gate();

        var root = await gate.AdmitAsync(
            RcaTriggerSources.FromSchedule("gunluk-kapasite", ["network/core"], Now.AddHours(-1), Now),
            Token);

        var child = await gate.AdmitAsync(Alert(RuleB, root.Run), Token);

        Assert.Equal(RcaTriggerSource.Schedule, root.Run.Source);
        Assert.Equal(RcaTriggerSource.Schedule, child.Run.Source);
        Assert.Equal(root.Run.Id, child.Run.RootRunId);
    }

    // --- Idempotency -------------------------------------------------------

    /// <summary>
    /// Kabul kriteri: aynı <c>Idempotency-Key</c> ile ikinci çağrı <b>aynı
    /// koşumu</b> döndürüyor, yeni koşu başlatmıyor.
    /// </summary>
    [Fact]
    public async Task Ayni_idempotency_anahtari_ayni_kosumu_donduruyor()
    {
        var gate = Gate();

        var request = RcaTriggerSources.FromExternal(
            "svc-hesabi", "anahtar-1", ["network/core"], Now.AddHours(-1), Now);

        var first = await gate.AdmitAsync(request, Token);
        var second = await gate.AdmitAsync(request, Token);

        Assert.False(first.Existing);
        Assert.True(second.Existing);
        Assert.Equal(first.Run.Id, second.Run.Id);

        await using var db = _factory.CreateDbContext();

        // İkinci çağrı hiçbir satır yazmadı: idempotent bir istemcinin tekrar
        // denemeleri "kaç RCA istendi" sayısını şişirmemeli.
        Assert.Equal(1, await db.RcaRuns.CountAsync(Token));
    }

    [Fact]
    public async Task Farkli_idempotency_anahtari_yeni_kosum_aciyor()
    {
        var gate = Gate();

        var first = await gate.AdmitAsync(
            RcaTriggerSources.FromExternal("svc", "anahtar-1", ["network/core"], Now.AddHours(-1), Now), Token);

        // Aynı pencere ve kapsam ama farklı anahtar: debounce yakalıyor, yani
        // ikinci talep YENİ bir kayıt açıyor ama kabul EDİLMİYOR.
        var second = await gate.AdmitAsync(
            RcaTriggerSources.FromExternal("svc", "anahtar-2", ["network/core"], Now.AddHours(-1), Now), Token);

        Assert.NotEqual(first.Run.Id, second.Run.Id);
        Assert.False(second.Existing);
        Assert.Equal(RcaRejectionReason.Debounced, second.Rejection);
    }

    // --- Kota kancası ------------------------------------------------------

    /// <summary>
    /// Kota kapısı <b>en sonda</b> çağrılıyor: reddedilen bir koşumun kotadan
    /// düşülüp düşülmeyeceği açık bir soru (T46) ve kota önce koşsaydı o soru
    /// sorulamazdı — döngü zaten kotayı tüketmiş olurdu.
    /// </summary>
    [Fact]
    public async Task Kota_kapisi_dongu_kontrolunden_sonra_calisiyor()
    {
        var quota = new CountingQuotaGate();
        var gate = Gate(quota);

        var a = await gate.AdmitAsync(Alert(RuleA), Token);
        var b = await gate.AdmitAsync(Alert(RuleB, a.Run), Token);
        await gate.AdmitAsync(Alert(RuleA, b.Run), Token);   // döngü

        // Üç talep, ama kota yalnızca döngüden ÖNCE geçen ikisi için soruldu.
        Assert.Equal(2, quota.Calls);
    }

    [Fact]
    public async Task Kota_reddi_kapali_kumeden_geliyor()
    {
        var gate = Gate(new AlwaysDenyQuotaGate());

        var result = await gate.AdmitAsync(Alert(RuleA), Token);

        Assert.False(result.Accepted);
        Assert.Equal(RcaRejectionReason.QuotaExceeded, result.Rejection);
    }

    private sealed class CountingQuotaGate : IRcaQuotaGate
    {
        public int Calls { get; private set; }

        public ValueTask<RcaRejectionReason> CheckAsync(
            RcaTriggerRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(RcaRejectionReason.None);
        }
    }

    private sealed class AlwaysDenyQuotaGate : IRcaQuotaGate
    {
        public ValueTask<RcaRejectionReason> CheckAsync(
            RcaTriggerRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RcaRejectionReason.QuotaExceeded);
    }
}

using System.Security.Claims;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;

namespace Bizigo.UnitTests;

/// <summary>
/// Koşum listesinin bekçileri (T46) — <c>GET /v1/rca/runs</c>.
///
/// <para>
/// <b>Bu paketin taşıdığı asıl şey bir garanti:</b> boş liste <i>"hiç
/// tetiklenmedi"</i> demek. Garanti, reddedilen koşumun listede görünmesine
/// bağlı; görünmezse "kota reddetti" ile "hiç tetiklenmedi" aynı cevaba düşer ve
/// ikisinin farkı — F4 §4.1'in taşıyıcı kuralı — sessizce kaybolur.
/// </para>
/// </summary>
public sealed class RcaRunQueryTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<RcaRunListResponse> QueryAsync(
        AccessScope scope,
        string? ownerGroup = null,
        string? trigger = null,
        int? limit = null) =>
        await RcaRunEndpoints.ListAsync(ownerGroup, trigger, limit, _factory, new FakeCurrentUser(scope), Token);

    private static AccessScope Core => AccessScope.ForGroups("analyst", ["network/core"]);

    private async Task<RcaRunEntity> SeedAsync(
        RcaRunState state,
        RcaRejectionReason rejection = RcaRejectionReason.None,
        string ownerGroup = "network/core",
        string trigger = "alert:fw-core-01",
        int minutesAgo = 0)
    {
        var run = new RcaRunEntity
        {
            OwnerGroup = ownerGroup,
            Source = RcaTriggerSource.Alert,
            TriggerIdentity = trigger,
            RequestedAt = Noon.AddMinutes(-minutesAgo),
            State = state,
            Rejection = rejection,
            Accepted = state != RcaRunState.Rejected,
            CountsAgainstQuota = RcaRunLifecycle.CountsAgainstQuota(state),
        };

        await using var db = _factory.CreateDbContext();
        db.RcaRuns.Add(run);
        await db.SaveChangesAsync(Token);

        return run;
    }

    /// <summary>
    /// <b>Bu testin işi bir kararı yerinde tutmak.</b> Reddedilen koşum listede
    /// <i>görünür</i>. Yarın biri "reddedilenleri gizleyelim, gürültü yapıyor"
    /// derse buradan kırmızı yanar — ve gizlemenin bedeli şu: dördüncü hâl (boş
    /// liste = hiç tetiklenmedi) birinciyle (kota reddetti) aynı cevaba düşer.
    /// </summary>
    [Fact]
    public async Task Reddedilen_kosum_listede_gorunuyor()
    {
        await SeedAsync(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded);

        var result = await QueryAsync(Core);

        var run = Assert.Single(result.Runs);
        Assert.Equal("rejected", run.State);
        Assert.False(run.CountsAgainstQuota);
    }

    /// <summary>
    /// <b>Boş listenin anlamı sözleşmede yazılı, burada sınanıyor.</b> Hiç satır
    /// yoksa RCA bu tetikleyici için <i>hiç denenmedi</i>. Reddedilen koşumun
    /// satır olması (yukarıdaki test) bu cümlenin ön koşulu: ikisi birlikte
    /// okunuyor, ayrı ayrı değil.
    /// </summary>
    [Fact]
    public async Task Bos_liste_hic_tetiklenmedigi_anlamina_geliyor()
    {
        // Aynı grupta başka bir tetikleyicinin koşumu var — yani veritabanı boş
        // değil, sorunun cevabı boş.
        await SeedAsync(RcaRunState.Complete, trigger: "alert:fw-edge-02");

        var result = await QueryAsync(Core, trigger: "alert:fw-core-01");

        Assert.Empty(result.Runs);
        Assert.Equal(0, result.Count);
    }

    /// <summary>
    /// <b>Üç yönlü ayrım cevapta doğrudan görünüyor</b>, istemcinin çıkarım
    /// yapması gerekmiyor: üç satır, üç ayrı <c>reason</c>. İstemci
    /// <c>state</c> ile ret sebebini birleştirmek zorunda kalsaydı o birleştirme
    /// her ekranda yeniden yazılırdı.
    /// </summary>
    [Fact]
    public async Task Uc_ayrim_tek_alandan_okunuyor()
    {
        await SeedAsync(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded, minutesAgo: 3);
        await SeedAsync(RcaRunState.Empty, minutesAgo: 2);
        await SeedAsync(RcaRunState.Cancelled, minutesAgo: 1);

        var result = await QueryAsync(Core);

        var reasons = result.Runs.Select(r => r.Reason).ToArray();

        Assert.Equal(3, reasons.Length);
        Assert.Equal(3, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
    }

    [Fact]
    public async Task En_yeni_kosum_basta()
    {
        await SeedAsync(RcaRunState.Complete, trigger: "eski", minutesAgo: 30);
        await SeedAsync(RcaRunState.Complete, trigger: "yeni", minutesAgo: 1);

        var result = await QueryAsync(Core);

        Assert.Equal("yeni", result.Runs[0].TriggerIdentity);
    }

    /// <summary>
    /// <b>K17.</b> Kapsam dışı grubun koşumu hiç görünmüyor — çağıran onu adıyla
    /// istese bile. 403 dönmek "böyle bir grup var ama göremezsin" bilgisini
    /// sızdırırdı; boş dönmek grubun varlığını da saklıyor.
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_grup_gorunmuyor()
    {
        await SeedAsync(RcaRunState.Complete, ownerGroup: "finance/core");

        Assert.Empty((await QueryAsync(Core)).Runs);
        Assert.Empty((await QueryAsync(Core, ownerGroup: "finance/core")).Runs);
    }

    /// <summary>
    /// <b>Kapsam filtresi sayfalamadan önce.</b> Kapsam dışı satırlar en yeniler
    /// olduğunda bellekte filtrelemek sayfayı boşaltırdı — ve boş listenin
    /// garantisi sessizce yalana dönerdi: "hiç tetiklenmedi" derken aslında
    /// "sayfanın tamamı başkasınındı" oluyordu.
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_yeni_satirlar_gorunur_olani_sayfadan_dusurmuyor()
    {
        // Görünür tek satır en eskisi; önünde beş kapsam dışı satır var.
        await SeedAsync(RcaRunState.Complete, trigger: "gorunur", minutesAgo: 60);

        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(RcaRunState.Complete, ownerGroup: "finance/core", minutesAgo: i);
        }

        var result = await QueryAsync(Core, limit: 5);

        var run = Assert.Single(result.Runs);
        Assert.Equal("gorunur", run.TriggerIdentity);
    }

    [Fact]
    public async Task Kapsamsiz_istek_hicbir_satir_gormuyor()
    {
        await SeedAsync(RcaRunState.Complete);

        var result = await QueryAsync(AccessScope.Denied);

        Assert.Empty(result.Runs);
    }

    [Fact]
    public async Task Sinirsiz_kapsam_butun_gruplari_goruyor()
    {
        await SeedAsync(RcaRunState.Complete, ownerGroup: "network/core");
        await SeedAsync(RcaRunState.Complete, ownerGroup: "finance/core");

        var result = await QueryAsync(AccessScope.System("replay"));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Limit_tavana_kirpiliyor()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedAsync(RcaRunState.Complete, minutesAgo: i);
        }

        // Tavanın üstünde bir istek hata değil; kırpılıyor. Sıfır ve negatif de
        // en az bir satıra çekiliyor — `Take(0)` sessizce boş liste döndürürdü
        // ve boş listenin anlamı bu uçta yüklü.
        Assert.Equal(3, (await QueryAsync(Core, limit: 10_000)).Count);
        Assert.Equal(1, (await QueryAsync(Core, limit: 0)).Count);
        Assert.Equal(1, (await QueryAsync(Core, limit: -5)).Count);
    }

    /// <summary>
    /// Tel adları sabit ve küçük harfli: ekran bu dizgilere göre dallanacak ve
    /// onları elle yazacak. <c>enum</c> adının değişmesi sözleşmeyi sessizce
    /// kırmamalı — <c>RcaReviewWireTests</c>'in çaktığı çivinin aynısı.
    /// </summary>
    [Fact]
    public void Durum_adlari_tele_kucuk_harfli_iniyor()
    {
        foreach (var state in Enum.GetValues<RcaRunState>())
        {
            var wire = RcaRunResponse.Of(new RcaRunEntity
            {
                OwnerGroup = "network/core",
                State = state,
            }).State;

            Assert.Equal(state.ToString().ToLowerInvariant(), wire);
            Assert.DoesNotContain(wire, ch => char.IsUpper(ch));
        }
    }
}

/// <summary>Sabit kapsam taşıyan test ikizi; <c>HttpContext</c> istemiyor.</summary>
internal sealed class FakeCurrentUser(AccessScope scope) : ICurrentUser
{
    public AccessScope Scope { get; } = scope;

    public ClaimsPrincipal? Principal => null;
}

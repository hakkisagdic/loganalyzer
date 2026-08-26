using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bizigo.Rca;



/// <param name="Identity">
/// Kaynağa özgü kimlik: alarmda <c>rule_id</c>, takvimde zamanlama kimliği,
/// kullanıcı ve API'de talebi açan özne.
/// </param>
/// <param name="Parent">
/// Bu talebi doğuran koşum — devam kuralı. Kök talepte <see langword="null"/>.
/// </param>
public sealed record RcaTriggerRequest
{
    public required RcaTriggerSource Source { get; init; }
    public required string Identity { get; init; }
    public required string OwnerGroup { get; init; }
    public required DateTimeOffset WindowFrom { get; init; }
    public required DateTimeOffset WindowTo { get; init; }

    public RcaRunEntity? Parent { get; init; }
    public string? IdempotencyKey { get; init; }
    public string RequestedBy { get; init; } = string.Empty;
}

/// <param name="Existing">
/// Idempotency anahtarı zaten görülmüşse <b>var olan</b> koşum. Yeni bir kayıt
/// yazılmadı.
/// </param>
public sealed record RcaAdmissionResult(RcaRunEntity Run, bool Existing)
{
    public bool Accepted => Run.Accepted;

    public RcaRejectionReason Rejection => Run.Rejection;
}

/// <summary>
/// Kota kapısı — <b>T46'nın işi, burada yalnızca kancası</b>.
///
/// <para>
/// T45 kotayı uygulamıyor; uygulasaydı iki ticket aynı kısıtı iki kez yazardı ve
/// biri değiştiğinde diğeri sessizce ayrışırdı. Buradaki tek taahhüt <b>çağrının
/// yeri</b>: kota kontrolü debounce ve soyağacından <b>sonra</b>, kayıt
/// yazılmadan önce koşuyor.
/// </para>
///
/// <para>
/// Sıra tesadüfi değil. Reddedilen bir koşumun kotadan düşülüp düşülmeyeceği
/// <b>açık bir soru</b> (RCA §5) ve kararı T46'da; ama kota kontrolü döngü
/// kontrolünden önce koşsaydı o soru sorulamaz hâle gelirdi — döngü zaten
/// kotayı tüketmiş olurdu.
/// </para>
/// </summary>
public interface IRcaQuotaGate
{
    /// <summary>
    /// Kabul edilebilir mi. <see cref="RcaRejectionReason.None"/> dönerse geçiyor.
    /// </summary>
    ValueTask<RcaRejectionReason> CheckAsync(RcaTriggerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// T46 inene kadarki kota kapısı: <b>hiçbir şeyi reddetmiyor</b>.
///
/// <para>
/// Varsayılanın "her şey geçer" olması bilinçli ve tehlikesiz: T45'in teslim
/// ettiği şey kota değil, kotanın <b>çağrılacağı nokta</b>. Varsayılan
/// "her şey reddedilir" olsaydı T45 tek başına hiçbir RCA üretemez ve kendi
/// bekçileri sınanamaz hâle gelirdi.
/// </para>
/// </summary>
public sealed class AlwaysAllowQuotaGate : IRcaQuotaGate
{
    public ValueTask<RcaRejectionReason> CheckAsync(
        RcaTriggerRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RcaRejectionReason.None);
}

/// <summary>
/// <b>Dört kaynağın tek kapısı</b> (T45, RCA §5).
///
/// <para>
/// Alarm, kullanıcı, dış API ve <c>schedule</c> — dördü de buradan geçiyor.
/// Ayrı yollar olsaydı kota, debounce ve döngü koruması dört kez yazılacaktı ve
/// dördüncüsü eksik kalırdı; §5'in açılış cümlesi tam bunu yasaklıyor.
/// </para>
///
/// <para>
/// <b>Kapı sırası ve neden bu sıra:</b>
/// </para>
/// <list type="number">
///   <item><description><b>Idempotency</b> — aynı anahtar zaten görüldüyse hiçbir
///   kapı çalıştırılmadan var olan koşum dönüyor. Önce olması şart: aynı talebin
///   ikinci kez gelmesi yeni bir karar değil, aynı kararın tekrar okunması.</description></item>
///   <item><description><b>Ata tekrarı</b> — döngü. Derinlikten <b>önce</b>:
///   ikisi çakıştığında (A → B → A) derinlik önce sorulsaydı döngü görünmez
///   olurdu ve ret "sınıra çarptı" diye kaydedilirdi.</description></item>
///   <item><description><b>Derinlik</b> — sınır. Buraya ancak döngü yokken
///   gelinir, yani bu ret gerçekten sınır hakkında.</description></item>
///   <item><description><b>Debounce</b> — pencere sorgusu.</description></item>
///   <item><description><b>Kota</b> — T46. En sonda, çünkü reddedilen bir
///   koşumun kotadan düşülüp düşülmeyeceği açık bir soru ve önce koşsaydı o
///   soru sorulamazdı.</description></item>
/// </list>
///
/// <para>
/// <b>Her ret kayda geçiyor.</b> Sessizce düşürmek, "neden RCA üretilmedi"
/// sorusunu cevapsız bırakır.
/// </para>
/// </summary>
public sealed class RcaAdmission(
    IDbContextFactory<ControlPlaneDbContext> factory,
    IRcaQuotaGate quota,
    ILogger<RcaAdmission> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// İzin verilen en büyük derinlik. <c>depth ≥ 2</c> reddediliyor (RCA §5),
    /// yani kök + bir devam.
    /// </summary>
    public const int MaxDepth = 2;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<RcaAdmissionResult> AdmitAsync(
        RcaTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _time.GetUtcNow();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 1) Idempotency — kapılardan önce.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var seen = await db.RcaRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            if (seen is not null)
            {
                // Yeni koşu YOK ve yeni kayıt da yok: aynı anahtar aynı raporu
                // döndürüyor. İkinci bir satır yazmak, "kaç RCA istendi"
                // sayısını idempotent bir istemcinin tekrar denemeleriyle
                // şişirirdi.
                return new RcaAdmissionResult(seen, Existing: true);
            }
        }

        var depth = request.Parent is null ? 0 : request.Parent.Depth + 1;
        var root = request.Parent?.RootRunId ?? Guid.Empty;

        var run = new RcaRunEntity
        {
            ParentRunId = request.Parent?.Id,
            Depth = depth,

            // Kaynak KÖKTEN miras alınıyor: devam kuralı yeni bir kaynak
            // yaratmıyor, "kim nihayetinde sebep oldu" sorusu derinlikten
            // bağımsız cevaplanabiliyor.
            Source = request.Parent?.Source ?? request.Source,

            TriggerIdentity = request.Identity,
            OwnerGroup = request.OwnerGroup,
            WindowFrom = request.WindowFrom,
            WindowTo = request.WindowTo,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey,
            RequestedAt = now,
            RequestedBy = request.RequestedBy,
        };

        run.RootRunId = root == Guid.Empty ? run.Id : root;

        run.DebounceKey = RcaTriggerKey.Debounce(run.Source, request.Identity, request.OwnerGroup, now);
        run.LineageKey = RcaTriggerKey.Lineage(run.Source, request.Identity, request.OwnerGroup);

        var (rejection, detail) = await DecideAsync(db, run, request, cancellationToken).ConfigureAwait(false);

        run.Rejection = rejection;
        run.RejectionDetail = detail;
        run.Accepted = rejection == RcaRejectionReason.None;

        db.RcaRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!run.Accepted)
        {
            logger.LogInformation(
                "RCA talebi reddedildi: {Reason} — {Detail} (kaynak {Source}, kapsam {Scope}, derinlik {Depth}).",
                rejection,
                detail,
                run.Source,
                run.OwnerGroup,
                run.Depth);
        }

        return new RcaAdmissionResult(run, Existing: false);
    }

    /// <summary>
    /// Kabul edilmiş bir koşumu ürettiği kanıt paketine bağlar.
    ///
    /// <para>
    /// Ayrı bir adım, çünkü paket <b>kabulden sonra</b> üretiliyor: kapı
    /// "koşsun mu" diye karar veriyor, koşumun kendisi sonra oluyor. Tek adımda
    /// yapılsaydı kapı, kabul etmeyeceği bir talebin kanıtını toplamak zorunda
    /// kalırdı — yani reddin bedeli kabulünkiyle aynı olurdu.
    /// </para>
    /// </summary>
    public async Task AttachBundleAsync(Guid runId, Guid bundleId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var run = await db.RcaRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            return;
        }

        run.EvidenceBundleId = bundleId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kapılar, ucuzdan pahalıya. İlk reddeden kazanıyor.</summary>
    private async Task<(RcaRejectionReason Reason, string Detail)> DecideAsync(
        ControlPlaneDbContext db,
        RcaRunEntity run,
        RcaTriggerRequest request,
        CancellationToken cancellationToken)
    {
        // 2) Ata tekrarı — döngü. **Derinlikten ÖNCE**, ve bu sıra ticket'ın
        //    taşıyıcı ayrımı.
        //
        //    A → B → A zincirinde iki koşul da doğru: derinlik 2'ye ulaştı VE
        //    anahtar soyağacında. Derinlik önce sorulsaydı ret `DepthExceeded`
        //    diye kaydedilirdi ve döngü **görünmez** olurdu — tam da
        //    "`depth ≤ 2` yetiyor" yanılsaması. İkisi çakıştığında daha özel
        //    olanı, yani gerçekten açıklayan cevabı yazıyoruz.
        //
        //    Bedeli: derinliğin tek başına yeteceği durumda da soyağacı
        //    yürünüyor. Zincir `MaxDepth` ile sınırlı olduğu için o yürüyüş
        //    en fazla iki satır.
        if (request.Parent is not null)
        {
            var ancestors = await AncestorKeysAsync(db, request.Parent, cancellationToken).ConfigureAwait(false);

            if (ancestors.Contains(run.LineageKey))
            {
                return (RcaRejectionReason.AncestorRepeat, $"anahtar soyağacında zaten var: {run.LineageKey}");
            }
        }

        // 3) Derinlik — sınır, döngü DEĞİL. Buraya ancak döngü YOKKEN gelinir,
        //    yani bu ret gerçekten "zincir yeterince derine indi" diyor.
        //    A → B → C bunun örneği: üç farklı anahtar, hiç döngü yok.
        if (run.Depth >= MaxDepth)
        {
            return (RcaRejectionReason.DepthExceeded, $"derinlik {run.Depth} ≥ {MaxDepth}");
        }

        // 4) Debounce — aynı tetikleyicinin aynı penceredeki tekrarı.
        //    Yalnızca KABUL edilmiş koşumlara bakıyor: reddedilen bir kayıt
        //    sonraki meşru talebi de reddetseydi, bir ret kendini çoğaltırdı.
        var debounced = await db.RcaRuns
            .AsNoTracking()
            .AnyAsync(r => r.DebounceKey == run.DebounceKey && r.Accepted, cancellationToken)
            .ConfigureAwait(false);

        if (debounced)
        {
            return (RcaRejectionReason.Debounced, $"aynı pencerede zaten koştu: {run.DebounceKey}");
        }

        // 5) Kota — T46. Kanca burada; kısıt orada.
        var quotaVerdict = await quota.CheckAsync(request, cancellationToken).ConfigureAwait(false);

        return quotaVerdict == RcaRejectionReason.None
            ? (RcaRejectionReason.None, string.Empty)
            : (quotaVerdict, "kota kapısı reddetti");
    }

    /// <summary>
    /// Atanın ve onun atalarının soyağacı anahtarları.
    ///
    /// <para>
    /// Zincir <see cref="MaxDepth"/> ile sınırlı olduğu için yürüyüş kısa; yine
    /// de bir güvenlik sayacı var. Sınırsız bir <c>while</c>, veri bozulursa
    /// (bir koşum kendini ata gösterirse) sonsuz döngüye girerdi — ve bu kapının
    /// varlık sebebi tam olarak sonsuz döngüleri engellemek.
    /// </para>
    /// </summary>
    private static async Task<HashSet<string>> AncestorKeysAsync(
        ControlPlaneDbContext db,
        RcaRunEntity parent,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { parent.LineageKey };
        var seen = new HashSet<Guid> { parent.Id };
        var current = parent.ParentRunId;

        for (var step = 0; step < MaxDepth + 2 && current is { } id; step++)
        {
            if (!seen.Add(id))
            {
                break;
            }

            var ancestor = await db.RcaRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (ancestor is null)
            {
                break;
            }

            keys.Add(ancestor.LineageKey);
            current = ancestor.ParentRunId;
        }

        return keys;
    }
}

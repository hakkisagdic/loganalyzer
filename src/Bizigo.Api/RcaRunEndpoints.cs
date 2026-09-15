using System.Text.Json.Serialization;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api;

/// <summary>
/// Bir RCA koşumunun ekranda okunan hâli (T46).
///
/// <para>
/// <b>§8 gereği dar.</b> Alan kümesi T38'in kapatma ekranının gerçekten okuduğu
/// kadar: hangi koşum, hangi tetikleyici, ne oldu, ne zaman. Kanıt paketi
/// kimliği, soyağacı, debounce anahtarı, token sayacı — hepsi <c>rca_runs</c>'ta
/// var ve hiçbirinin bugün bir tüketicisi yok. Tüketicisi olmayan alan bir
/// tahmindir ve tele indiği an sözleşme olur.
/// </para>
/// </summary>
/// <param name="State">
/// Makine tarafı: kapalı kümenin değeri, küçük harfli
/// (<c>rejected</c>, <c>queued</c>, <c>complete</c>, <c>empty</c>, …).
/// </param>
/// <param name="Reason">
/// <b>"Neden RCA yok" sorusunun tek alandan cevabı.</b> İstemci
/// <c>state</c> ile ret sebebini birleştirip anlam üretmek zorunda değil —
/// kalsaydı o birleştirme her ekranda yeniden yazılırdı ve hepsi kendi içinde
/// tutarlı görünürdü. Cümleyi <see cref="RcaRunLifecycle.Describe"/> üretiyor,
/// yani motorun bildirimdeki gerekçesiyle ekrandaki gerekçe <b>aynı kaynaktan</b>.
/// </param>
/// <param name="CountsAgainstQuota">
/// Bu satır grubun günlük hakkından düşüldü mü. Ekran "kota doldu" derken
/// hangi satırların o kotayı yediğini gösterebilsin diye burada — reddedilen
/// satırlar listede duruyor ama kotayı yemiyor ve ikisi karıştırılabilir.
/// </param>
public sealed record RcaRunResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("trigger_identity")] string TriggerIdentity,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("counts_against_quota")] bool CountsAgainstQuota,
    [property: JsonPropertyName("model_boundary")] string ModelBoundary,
    [property: JsonPropertyName("model_boundary_override_reason")] string? ModelBoundaryOverrideReason,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt)
{
    /// <summary>
    /// Varlıktan tel biçimine tek dönüşüm.
    ///
    /// <para>
    /// <c>ToString()</c> yerine burada durmasının sebebi <c>RcaReviewResponse</c>
    /// ile aynı: dizgiler tel sözleşmesi ve bir <c>enum</c> adının değişmesi
    /// sessizce sözleşmeyi kırmamalı.
    /// </para>
    ///
    /// <para>
    /// <b>Gerekçe <c>null</c> iken <c>null</c> kalıyor</b> (T54), boş dizeye
    /// çevrilmiyor: <c>null</c> "muafiyet yok", boş dize "muafiyet var ama
    /// gerekçesi yazılmamış". <c>ModelEndpoint.AuditFields()</c> tam tersini
    /// yapıyor (<c>?? string.Empty</c>) ve <b>orada doğru</b> — sözlüğün
    /// değerleri <c>object</c> ve <c>null</c> anahtarı düşürürdü; burada aynı
    /// dönüşüm iki farklı iddiayı aynı bayta indirirdi.
    /// </para>
    /// </summary>
    public static RcaRunResponse Of(RcaRunEntity run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new RcaRunResponse(
            run.Id,
            run.Source.ToString().ToLowerInvariant(),
            run.TriggerIdentity,
            run.OwnerGroup,
            run.State.ToString().ToLowerInvariant(),
            RcaRunLifecycle.Describe(run.State, run.Rejection),
            run.CountsAgainstQuota,
            run.ModelBoundary.ToString().ToLowerInvariant(),
            run.ModelBoundaryOverrideReason,
            run.RequestedAt,
            run.FinishedAt);
    }
}

/// <summary>
/// Koşum listesi.
///
/// <para>
/// <b>Boş liste bir garanti taşıyor: bu tetikleyici için RCA <i>hiç
/// denenmedi</i>.</b> "Kota reddetti" boş liste <b>değil</b> — reddedilen koşum
/// <c>rca_runs</c>'ta bir satır ve bu sorguda <c>state=rejected</c> olarak
/// görünüyor. Dört cevabın anlamı:
/// </para>
///
/// <list type="table">
/// <item><term><c>rejected</c> satırı</term><description>Kapı reddetti — bakılmadı.</description></item>
/// <item><term><c>empty</c> satırı</term><description>Bakıldı, ilişkili kanıt bulunamadı.</description></item>
/// <item><term><c>cancelled</c> satırı</term><description>Başladı, bir sınırda kesildi.</description></item>
/// <item><term>boş liste</term><description>Hiç tetiklenmedi.</description></item>
/// </list>
///
/// <para>
/// Garantinin doğru kalması <b>reddin satır olmayı sürdürmesine</b> bağlı;
/// <c>RcaRunQueryTests</c> o bağı tutuyor. Yarın biri "reddedilenleri listeden
/// gizleyelim" derse ayrımın dördüncü hâli ikinciyle karışır ve kimse fark etmez.
/// </para>
/// </summary>
public sealed record RcaRunListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("runs")] IReadOnlyList<RcaRunResponse> Runs);

/// <summary>
/// RCA koşumlarının okuma yolu (T46).
///
/// <para>
/// <b>Neden genel uç, alarma özel değil.</b> §4.2 alarm satırında "reddetme —
/// kaydet" istiyor ve bu ihtiyaç <b>okuma tarafında</b> karşılanıyor: kayıt
/// zaten oluşuyor, eksik olan alarmın kendi tetikleyici anahtarıyla
/// bakabilmesiydi. Alarma özel bir uç bugün aynı cevabı verirdi ama kapatma
/// akışına bağlanırdı; akış değiştiğinde uç değişir, ve ikinci bir tüketici
/// biraz farklı bir şekil isteyince üçüncü bir uç doğar.
/// </para>
/// </summary>
public static class RcaRunEndpoints
{
    /// <summary>Tek istekte dönebilecek en fazla satır.</summary>
    public const int MaxLimit = 200;

    /// <summary>İstemci bir sayı vermediğinde dönen satır sayısı.</summary>
    public const int DefaultLimit = 50;

    public static IEndpointRouteBuilder MapRcaRuns(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/v1/rca/runs", ListAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListRcaRuns")
            .WithTags("rca")
            .Produces<RcaRunListResponse>();

        return routes;
    }

    /// <summary>
    /// Koşumları en yeniden eskiye listeler.
    ///
    /// <para>
    /// <b>Kapsam (K17):</b> filtre <c>owner_group</c> üzerinden ve
    /// <b><c>Take</c>'ten önce</b> uygulanıyor. Bellekte yapılsaydı en yeni elli
    /// satırın hepsi kapsam dışı olduğunda cevap boş dönerdi — ve boş listenin
    /// garantisi ("hiç tetiklenmedi") sessizce yalan olurdu. Kapsam dışı bir grup
    /// açıkça istenirse yine boş dönüyor; 403 dönmek <i>"böyle bir grup var ama
    /// göremezsin"</i> bilgisini sızdırırdı.
    /// </para>
    /// </summary>
    public static async Task<RcaRunListResponse> ListAsync(
        [FromQuery(Name = "owner_group")] string? ownerGroup,
        [FromQuery(Name = "trigger")] string? trigger,
        [FromQuery(Name = "limit")] int? limit,
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(user);

        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            // Kapalı başlayan kapsam: hiçbir grubu olmayan istek hiçbir satır
            // görmez. Burada erken dönmek sorguyu da atlıyor.
            return new RcaRunListResponse(0, []);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.RcaRuns.AsNoTracking();

        if (!scope.IsUnrestricted)
        {
            var groups = scope.OwnerGroups.ToArray();
            query = query.Where(r => groups.Contains(r.OwnerGroup));
        }

        // Çağıranın verdiği grup kapsamı **genişletmiyor**, yalnızca daraltıyor:
        // yukarıdaki filtre zaten uygulandı, bu onun üstüne biniyor.
        if (!string.IsNullOrWhiteSpace(ownerGroup))
        {
            query = query.Where(r => r.OwnerGroup == ownerGroup);
        }

        if (!string.IsNullOrWhiteSpace(trigger))
        {
            query = query.Where(r => r.TriggerIdentity == trigger);
        }

        var rows = await query
            .OrderByDescending(r => r.RequestedAt)
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Cümle bellekte üretiliyor: `Describe` kapalı kümenin tek sahibi ve
        // SQL'e çevrilebilir olmak zorunda değil.
        var runs = rows.Select(RcaRunResponse.Of).ToArray();

        return new RcaRunListResponse(runs.Length, runs);
    }
}

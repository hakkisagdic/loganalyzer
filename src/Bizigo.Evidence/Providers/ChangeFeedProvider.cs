using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

/// <summary>
/// Değişiklik akışı — RCA'nın en güçlü sinyali (K21).
///
/// <para>
/// Olay penceresinden <b>önceki</b> bir aralığa da bakıyor: bir ACL push'unun
/// etkisi anında görünmüyor, dakikalar sonra görünüyor. Yalnızca pencerenin
/// kendisine bakan bir sağlayıcı, tam da aradığı nedeni kaçırırdı.
/// </para>
///
/// <para>
/// <b>Bu sınıfın en önemli satırı boş sonuç yolunda.</b> RCA artifact'ının 4.
/// riski: change beslemesi hiç bağlanmamışsa, "değişiklik yok" diyen bir
/// sağlayıcı olur — ve o cümle, ölçülmemiş bir şeyi ölçülmüş gibi gösterir.
/// O yüzden boş sonuç iki farklı duruma ayrılıyor:
/// <see cref="EvidenceStatus.Empty"/> (baktık, bu pencerede değişiklik yok) ve
/// <see cref="EvidenceStatus.NeverFed"/> (akışta hiç kayıt yok, yani
/// bakamıyoruz).
/// </para>
/// </summary>
public sealed class ChangeFeedProvider(IScopedQuery query, TimeProvider? timeProvider = null) : IEvidenceProvider
{
    /// <summary>
    /// Olay penceresinden ne kadar geriye bakılıyor. Bu bir <b>varsayılan</b>,
    /// ölçülmüş bir değer değil — T35 baseline uzunluğunu gerçek veriyle
    /// seçerken bu da onunla birlikte gözden geçirilmeli.
    /// </summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);

    /// <summary>
    /// "Hiç beslenmemiş" yoklamasının geriye bakış aralığı. Bir yıl: akışın
    /// bağlı olup olmadığını anlamaya fazlasıyla yeter ve yalnızca <b>boş</b>
    /// sonuç yolunda, tek ek sorguyla koşuyor.
    /// </summary>
    public static readonly TimeSpan EverProbe = TimeSpan.FromDays(365);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Id => "change.feed";

    public EvidenceKind Kind => EvidenceKind.Change;

    /// <summary>
    /// Akış her zaman sorgulanabilir. "Veri var mı" sorusu ayrı ve cevabı
    /// <see cref="EvidenceStatus"/>'te — burada <c>false</c> dönmek, boş bir
    /// akışı "kapalı sağlayıcı" gibi göstermek olurdu ve ikisi farklı şeyler.
    /// </summary>
    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(budget);

        var changeQuery = new ChangeQuery
        {
            From = window.From - Lead,
            To = window.To,
            OwnerGroups = window.OwnerGroups,
            Limit = budget.MaxItems + 1,
        };

        var changes = await query.SearchChangesAsync(changeQuery, scope, cancellationToken);
        var outOfScope = await query.CountOutOfScopeChangesAsync(changeQuery, scope, cancellationToken);

        var truncated = changes.Count > budget.MaxItems;
        var kept = truncated ? changes.Take(budget.MaxItems).ToArray() : changes;

        if (kept.Count == 0)
        {
            return await EmptyOrNeverFedAsync(changeQuery, scope, outOfScope, cancellationToken);
        }

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = Kind,
            Status = EvidenceStatus.Gathered,
            Items = [.. kept.Select(change => ToItem(change, window))],
            OutOfScopeCount = outOfScope,
            Truncated = truncated,
            Detail = truncated
                ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en yeniler tutuldu."
                : string.Empty,
        };
    }

    /// <summary>
    /// Boş sonucun hangi boşluk olduğunu ayırt eder — bu sağlayıcının varlık
    /// sebebinin yarısı.
    ///
    /// <para>
    /// Ek sorgu yalnızca burada, yani <b>yalnızca boş sonuç yolunda</b> koşuyor:
    /// akış doluysa maliyeti sıfır.
    /// </para>
    /// </summary>
    private async Task<EvidenceSlice> EmptyOrNeverFedAsync(
        ChangeQuery windowQuery,
        AccessScope scope,
        long outOfScope,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        var ever = await query.SearchChangesAsync(
            windowQuery with { From = now - EverProbe, To = now, Limit = 1 },
            scope,
            cancellationToken);

        if (ever.Count > 0)
        {
            return new EvidenceSlice
            {
                ProviderId = Id,
                Kind = Kind,
                Status = EvidenceStatus.Empty,
                Detail = "Bu pencerede kayıtlı değişiklik yok.",
                OutOfScopeCount = outOfScope,
            };
        }

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = Kind,
            Status = EvidenceStatus.NeverFed,
            Detail =
                "Değişiklik akışında hiç kayıt yok — besleme bağlı olmayabilir. " +
                "Bu, 'değişiklik olmadı' demek DEĞİL.",
            OutOfScopeCount = outOfScope,
        };
    }

    private static EvidenceItem ToItem(ChangeEvent change, RcaWindow window) => new(
        change.ChangeId.ToString(),
        "change.feed",
        EvidenceKind.Change,
        change.Timestamp,

        // Ağırlık zamansal yakınlıktan: olay penceresine yakın bir değişiklik,
        // yarım saat öncekinden daha ilgili. Sağlayıcı içinde karşılaştırılabilir,
        // sağlayıcılar arasında değil.
        Weight: Proximity(change.Timestamp, window.From),
        Summary: Describe(change),
        Payload: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner_group"] = change.OwnerGroup,
            ["target_kind"] = change.TargetKind.ToString(),
            ["target_id"] = change.TargetId,
            ["change_kind"] = change.ChangeKind,
            ["actor"] = change.Actor,
            ["source"] = change.Source,
            ["external_ref"] = change.ExternalRef,
        });

    private static string Describe(ChangeEvent change) => string.Create(
        CultureInfo.InvariantCulture,
        $"{change.ChangeKind} · {change.TargetId}{Suffix(" · aktör: ", change.Actor)}{Suffix(" — ", change.Summary)}");

    private static string Suffix(string prefix, string value) =>
        value.Length > 0 ? prefix + value : string.Empty;

    /// <summary>
    /// 1.0 = olay anında, 0'a doğru azalıyor. <see cref="Lead"/> kadar önce
    /// olan değişiklik 0 alıyor.
    /// </summary>
    private static double Proximity(DateTimeOffset changedAt, DateTimeOffset windowStart)
    {
        var distance = windowStart - changedAt;

        return distance <= TimeSpan.Zero
            ? 1.0
            : Math.Max(0, 1.0 - (distance / Lead));
    }
}

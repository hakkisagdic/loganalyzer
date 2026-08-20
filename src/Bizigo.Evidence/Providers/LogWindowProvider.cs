using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

/// <summary>
/// Olay penceresindeki bozuk satırlar — log türünün <b>referans uygulaması</b>.
///
/// <para>
/// <b>T35'in beş korelasyonundan biri değil</b>, bilerek. Bu sağlayıcının işi
/// sözleşmeyi gerçek ClickHouse'a karşı uçtan uca koşturmak: kapsam kapısı,
/// kapsam dışı sayım, bütçe kırpması, boş/dolu ayrımı ve ham loga inen yol.
/// Beş sinyal (ilk-görülen imza, hacim sapması, sessizlik, ortak öznitelik,
/// yayılma sırası) T35'te <b>ayrı sağlayıcılar</b> olarak iniyor ve bu sınıfta
/// hiçbir şey değiştirmiyorlar — sözleşmenin taşıyıcı iddiası bu.
/// </para>
///
/// <para>
/// Neden "bozuk satırlar" seçildi: kanıt paketinin zaman çizelgesi ve
/// <c>drilldown</c>'u zaten pencerenin ham satırlarına ihtiyaç duyuyor, ve
/// <c>parse_status != ok</c> filtresi onu okunabilir bir boyutta tutuyor.
/// Filtresiz bir pencere dökümü kanıt değil, veri yığınıdır.
/// </para>
/// </summary>
public sealed class LogWindowProvider(IScopedQuery query) : IEvidenceProvider
{
    public string Id => "logs.window";

    public EvidenceKind Kind => EvidenceKind.Log;

    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(budget);

        var eventQuery = new EventQuery
        {
            From = window.From,
            To = window.To,
            OwnerGroups = window.OwnerGroups,
            SourceIds = window.SourceIds,
            ParseStatuses = [ParseStatus.Failed, ParseStatus.Partial],
            Limit = budget.MaxItems,
            Ascending = true,
        };

        var page = await query.SearchEventsAsync(eventQuery, scope, cancellationToken);

        // Kapsam dışı sayım **her zaman** alınıyor, sonuç boş olsa bile: "senin
        // kapsamında bir şey yok ama dışarıda 342 var" tam da raporun söylemesi
        // gereken cümle, ve boş sonuçta sormamak onu sessizce yutardı.
        var outOfScope = await query.CountOutOfScopeEventsAsync(eventQuery, scope, cancellationToken);

        if (page.Events.Count == 0)
        {
            return new EvidenceSlice
            {
                ProviderId = Id,
                Kind = Kind,
                Status = EvidenceStatus.Empty,
                Detail = "Pencerede ayrıştırma sorunu olan olay yok.",
                OutOfScopeCount = outOfScope,
            };
        }

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = Kind,
            Status = EvidenceStatus.Gathered,
            Items = [.. page.Events.Select(ToItem)],
            OutOfScopeCount = outOfScope,

            // `HasMore` bütçeye takıldığımızın tek göstergesi. Sessizce
            // kırpılmış bir liste "hepsi bu" diye okunur.
            Truncated = page.HasMore,
            Detail = page.HasMore
                ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en eskiler tutuldu."
                : string.Empty,
        };
    }

    private static EvidenceItem ToItem(LogEvent e) => new(
        e.EventId.ToString(),
        "logs.window",
        EvidenceKind.Log,
        e.Timestamp,

        // `failed`, `partial`'dan daha güçlü bir sinyal: parser satırı hiç
        // tanımadıysa satırın kendisi yeni bir şey olabilir.
        Weight: e.ParseStatus == ParseStatus.Failed ? 1.0 : 0.5,
        Summary: string.Create(
            CultureInfo.InvariantCulture,
            $"{e.SourceId} · {e.ParseStatus.ToString().ToLowerInvariant()} · " +
            $"{(e.Body.Length > 160 ? e.Body[..160] + "…" : e.Body)}"),
        Payload: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner_group"] = e.OwnerGroup,
            ["source_id"] = e.SourceId,
            ["host"] = e.Host,
            ["parser_id"] = e.ParserId,
            ["parse_status"] = e.ParseStatus.ToString(),

            // `time_source` kanıtın parçası (F3 planı): zamanı `observed` ya da
            // `received` olan bir olayın gerçek zamanı dakikalarca önce olabilir
            // ve korelasyon penceresi bunu bilmeden kayar. T35 bunu rapora
            // taşıyacak; taşıyabilmesi için kanıt satırının onu getirmesi şart.
            ["time_source"] = e.TimeSource,
            ["signature_hash"] = e.SignatureHash.ToString(CultureInfo.InvariantCulture),
        },

        // Ham loga inen yol. SQL dizgisi değil, yapılandırılmış sorgu:
        // tıklandığında `IScopedQuery`'den geçiyor ve kapsam kapısı yeniden
        // uygulanıyor. Kanıt paketi saklandığı için (T36) buraya SQL koymak,
        // kapsamı atlayan bir yolu diske yazmak olurdu.
        Drilldown: new EventQuery
        {
            From = e.Timestamp.AddMinutes(-1),
            To = e.Timestamp.AddMinutes(1),
            SourceIds = [e.SourceId],
            Limit = 200,
        });
}

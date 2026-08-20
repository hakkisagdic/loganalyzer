using System.Text.Json.Serialization;
using Bizigo.Replay;

namespace Bizigo.Api;

/// <param name="Field">Değişen alan adı.</param>
public sealed record ReplayFieldChangeResponse(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("before")] string Before,
    [property: JsonPropertyName("after")] string After);

/// <summary>
/// Örnek fark. <b>Sayı tek başına "doğru mu" sorusunu cevaplamıyor</b> — kuru
/// koşunun değeri kullanıcının birkaç gerçek satıra bakabilmesinde.
/// </summary>
public sealed record ReplayDiffResponse(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("status_before")] string StatusBefore,
    [property: JsonPropertyName("status_after")] string StatusAfter,
    [property: JsonPropertyName("changes")] IReadOnlyList<ReplayFieldChangeResponse> Changes);

/// <summary>
/// Replay raporunun <b>tel üzerindeki</b> şekli.
///
/// <para>
/// <b>Neden ayrı bir tip:</b> uç bugüne kadar <c>ReplayReport</c>'u doğrudan
/// döndürüyordu, yani <c>Bizigo.Replay</c>'in domain tipi fiilen tel
/// sözleşmesiydi. O tipe eklenen her alan kimse karar vermeden API'ye sızardı —
/// T24'te <c>ChangeEvent</c> için, T15'te <c>LogEvent</c> için verilen kararın
/// aynısı. Sözleşmeye neyin gireceği <b>sunucunun</b> kararı ve o karar burada
/// veriliyor.
/// </para>
///
/// <para>
/// <b>Dışarıda bırakılan tek şey <c>Plan</c>.</b> İstemci planı zaten kendisi
/// gönderdi; geri yollamak yankıdan ibaret olurdu. Daha önemlisi
/// <see cref="ReplayPlan"/> ileride kimlik bilgisi ya da iç bayrak taşıyabilecek
/// bir yapılandırma tipi ve yankı, ona eklenen her alanı otomatik olarak
/// yanıta koyardı. Kullanıcının hangi aralığın koştuğunu görmesi gerekiyorsa o
/// bilgi <see cref="Partitions"/>'da zaten var.
/// </para>
///
/// <para>
/// <c>HasMissingObjects</c> da yok: <see cref="MissingObjects"/>'in boş olup
/// olmadığından türeyen bir bayrak, aynı gerçeği iki kez söylemek olurdu ve
/// ikisi bir gün ayrışabilirdi.
/// </para>
/// </summary>
public sealed record ReplayResponse(
    [property: JsonPropertyName("partitions")] IReadOnlyList<string> Partitions,
    [property: JsonPropertyName("records_replayed")] int RecordsReplayed,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("changed")] int Changed,

    /// <summary><c>failed</c> → <c>ok</c>. Replay'in asıl vaadi bu sayı.</summary>
    [property: JsonPropertyName("failed_to_ok")] int FailedToOk,

    /// <summary>Sıfırdan büyükse yeni parser bir gerileme getirmiş.</summary>
    [property: JsonPropertyName("ok_to_failed")] int OkToFailed,

    [property: JsonPropertyName("new_rows")] int NewRows,
    [property: JsonPropertyName("copied_unchanged")] int CopiedUnchanged,

    /// <summary>
    /// Canlı yazmaya açık olduğu için atlanan bölümler (T27 bulgusu).
    ///
    /// <para>
    /// Tele <b>çıkmak zorunda</b>: sessizce kısalan bir replay, manifest'in
    /// (K25 koruma #4) kapattığı hatanın aynısı. Kullanıcı "7 gün istedim, 6 gün
    /// koştu"yu yalnızca buradan görebiliyor.
    /// </para>
    /// </summary>
    [property: JsonPropertyName("skipped_open_partitions")] IReadOnlyList<string> SkippedOpenPartitions,

    [property: JsonPropertyName("missing_objects")] IReadOnlyList<string> MissingObjects,
    [property: JsonPropertyName("changes_by_field")] IReadOnlyDictionary<string, int> ChangesByField,
    [property: JsonPropertyName("samples")] IReadOnlyList<ReplayDiffResponse> Samples,

    /// <summary>
    /// Saniye cinsinden. <c>TimeSpan</c> doğrudan yayınlansaydı JSON'a
    /// <c>"00:01:23.4560000"</c> gibi .NET'e özgü bir dizge inerdi; sayı hem
    /// dilden bağımsız hem ekranda doğrudan kullanılabilir.
    /// </summary>
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,

    /// <summary><see langword="false"/> ise yalnızca rapor üretildi, yazma yapılmadı.</summary>
    [property: JsonPropertyName("applied")] bool Applied)
{
    public static ReplayResponse From(ReplayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ReplayResponse(
            Partitions: report.Partitions,
            RecordsReplayed: report.RecordsReplayed,
            Unchanged: report.Unchanged,
            Changed: report.Changed,
            FailedToOk: report.FailedToOk,
            OkToFailed: report.OkToFailed,
            NewRows: report.NewRows,
            CopiedUnchanged: report.CopiedUnchanged,
            SkippedOpenPartitions: report.SkippedOpenPartitions,
            MissingObjects: report.MissingObjects,
            ChangesByField: report.ChangesByField,
            Samples: [.. report.Samples.Select(sample => new ReplayDiffResponse(
                sample.EventId,
                sample.StatusBefore,
                sample.StatusAfter,
                [.. sample.Changes.Select(change => new ReplayFieldChangeResponse(
                    change.Field, change.Before, change.After))]))],
            DurationSeconds: report.Duration.TotalSeconds,
            Applied: report.Applied);
    }
}

/// <summary>
/// Eksik nesne yüzünden duran replay'in 409 gövdesi.
///
/// <para>
/// <c>ErrorResponse</c> yetmiyor: kullanıcının kararı verebilmesi için
/// <b>hangi</b> nesnelerin eksik olduğunu görmesi gerekiyor. Bu bir hata değil
/// bir duruş — "devam edeyim mi" sorusu ve cevabı için gereken veri.
/// </para>
/// </summary>
public sealed record ReplayBlockedResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("missing_objects")] IReadOnlyList<string> MissingObjects,
    [property: JsonPropertyName("hint")] string Hint);

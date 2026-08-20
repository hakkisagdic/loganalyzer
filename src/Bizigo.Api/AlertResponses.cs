using System.Text.Json.Serialization;

namespace Bizigo.Api;

/// <summary>
/// Alarm uçlarının yanıt gövdeleri (T21–T23).
///
/// <para>
/// <b>Anonim nesne değil, adlandırılmış tip</b> — <c>AuthMeResponse</c> ile aynı
/// gerekçe: gövde OpenAPI belgesine şema olarak inmezse T14'ün ürettiği
/// TypeScript tarafında <c>unknown</c> kalıyor ve ekran elle tip yazmak zorunda
/// kalıyor. Elle yazılan tip, API değiştiği gün sessizce yalan söyler; şemadan
/// üretilen tip CI'ı kırar.
/// </para>
///
/// <para>
/// <c>JsonPropertyName</c> nitelikleri <b>zorunlu</b>: varsayılan camelCase
/// politikası <c>owner_groups</c>'u <c>ownerGroups</c> yapar ve F1'den beri
/// yerleşik olan snake_case sözleşmesini kırar.
/// </para>
/// </summary>
public sealed record AlertSearchResponse(
    [property: JsonPropertyName("full_text")] string? FullText,
    [property: JsonPropertyName("filters")] FieldFilterResponse[] Filters,
    [property: JsonPropertyName("source_ids")] string[] SourceIds);

public sealed record FieldFilterResponse(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("values")] string[] Values);

public sealed record AlertRuleResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("rule_type")] string RuleType,
    [property: JsonPropertyName("owner_groups")] string[] OwnerGroups,
    [property: JsonPropertyName("window_seconds")] int WindowSeconds,
    [property: JsonPropertyName("interval_seconds")] int IntervalSeconds,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("comparison")] string Comparison,
    [property: JsonPropertyName("silence_seconds")] int SilenceSeconds,
    [property: JsonPropertyName("repeat_interval_seconds")] int RepeatIntervalSeconds,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("next_run_at")] DateTimeOffset? NextRunAt,
    [property: JsonPropertyName("last_run_at")] DateTimeOffset? LastRunAt,
    [property: JsonPropertyName("last_fired_at")] DateTimeOffset? LastFiredAt,
    [property: JsonPropertyName("last_run_state")] string LastRunState,
    [property: JsonPropertyName("last_error")] string LastError,
    [property: JsonPropertyName("search")] AlertSearchResponse Search);

public sealed record AlertRuleListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("rules")] AlertRuleResponse[] Rules);

/// <param name="ChannelIds">
/// Düzenleme formunun geri yazması için. Liste ucunda yok — kural başına ayrı
/// sorgu, elli kurallık listede elli sorgu demekti.
/// </param>
public sealed record AlertRuleDetailResponse(
    [property: JsonPropertyName("rule")] AlertRuleResponse Rule,
    [property: JsonPropertyName("channel_ids")] Guid[] ChannelIds);

/// <param name="Value">
/// Kural tipinin baktığı değer: eşikte sayı, oranda katsayı.
/// <b>Eşik uygulanmamış</b> — ekran eşiği değiştirdiğinde bunu yeniden yorumluyor.
/// </param>
public sealed record PreviewPointResponse(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("value")] double Value);

/// <param name="GapsSeconds">
/// Penceredeki sessizlik süreleri. Eşikten bağımsız: kullanıcı eşiği
/// değiştirdiğinde kaçının aşıldığı aynı listeden yeniden sayılıyor.
/// </param>
public sealed record PreviewSourceResponse(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("last_seen")] DateTimeOffset? LastSeen,
    [property: JsonPropertyName("gaps_seconds")] double[] GapsSeconds);

/// <summary>
/// Önizleme yanıtı (T23).
///
/// <para>
/// <b>Eşikten bağımsız olması sözleşmenin parçası.</b> <see cref="FiringCount"/>
/// yalnızca kolaylık; asıl cevap <see cref="Points"/> ve <see cref="Sources"/>.
/// Ekran eşiği değiştirdiğinde sayıyı bunlardan yeniden hesaplıyor ve yeni bir
/// istek atmıyor.
/// </para>
/// </summary>
public sealed record AlertPreviewResponse(
    [property: JsonPropertyName("rule_type")] string RuleType,
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("bucket_seconds")] int BucketSeconds,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("firing_count")] int FiringCount,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("points")] PreviewPointResponse[] Points,
    [property: JsonPropertyName("sources")] PreviewSourceResponse[] Sources);

/// <param name="State"><c>pending</c> | <c>delivered</c> | <c>failed</c>.</param>
/// <param name="LastError">Redaksiyondan geçmiş; gizli bilgi buraya yazılamıyor (T22).</param>
public sealed record AlertDeliveryResponse(
    [property: JsonPropertyName("channel_id")] Guid ChannelId,
    [property: JsonPropertyName("channel_name")] string ChannelName,
    [property: JsonPropertyName("channel_type")] string? ChannelType,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("last_error")] string LastError);

/// <param name="Deliveries">
/// "Gönderildi" ile "ulaştı" ayrı (T23 kabul kriteri). Tetiklenmeyle birlikte
/// dönüyor: ayrı bir uçtan çekilseydi ekran satır başına bir istek atardı ve
/// çoğu ekran bunu yapmayıp yalnızca "tetiklendi"yi gösterirdi.
/// </param>
public sealed record AlertTriggerResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("rule_id")] Guid RuleId,
    [property: JsonPropertyName("rule_name")] string RuleName,
    [property: JsonPropertyName("fired_at")] DateTimeOffset FiredAt,
    [property: JsonPropertyName("window_from")] DateTimeOffset WindowFrom,
    [property: JsonPropertyName("window_to")] DateTimeOffset WindowTo,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("deliveries")] AlertDeliveryResponse[] Deliveries,

    // Asgari yaşam döngüsü (T38). Ekran açık ile kapalıyı ayırt edebilmeli;
    // ayrı bir uçtan çekilseydi liste satır başına bir istek atardı ve çoğu
    // ekran bunu yapmayıp hepsini açık gösterirdi.
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
    [property: JsonPropertyName("closed_by")] string? ClosedBy,
    [property: JsonPropertyName("review_id")] Guid? ReviewId);

public sealed record AlertTriggerListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("triggers")] AlertTriggerResponse[] Triggers);

public sealed record MaintenanceWindowResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("rule_id")] Guid? RuleId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("ends_at")] DateTimeOffset EndsAt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("created_by")] string CreatedBy);

public sealed record MaintenanceWindowListResponse(
    [property: JsonPropertyName("windows")] MaintenanceWindowResponse[] Windows);

public sealed record CreatedIdResponse([property: JsonPropertyName("id")] Guid Id);

/// <summary>
/// F1'den beri yerleşik hata gövdesi: <c>{ "error": "…", "hint": "…" }</c>.
///
/// <para>
/// <c>hint</c> isteğe bağlı ve <b>ayrı</b> taşınıyor: <c>error</c> ne olduğunu,
/// <c>hint</c> ne yapılacağını söylüyor ve arayüzde ayrı yerlere gidiyorlar
/// (<c>ErrorState</c>). İkisini tek cümlede birleştirmek, eyleme çağrıyı hata
/// metninin içinde kaybediyordu. BFF vekili (<c>lib/api/proxy.ts</c>) kendi
/// ürettiği hatalarda bu şekli zaten kullanıyordu; tip onu geç yakaladı (T19).
/// </para>
/// </summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("hint")] string? Hint = null);

/// <summary>
/// Kanal yapılandırmasının gizli <b>olmayan</b> kısmı.
///
/// <para>
/// Olduğu gibi dönebiliyor, çünkü içine gizli bilgi girmesi yapısal olarak
/// mümkün değil: gizli olan tek alan ayrı bir kolonda ve burada yalnızca
/// <c>secret_set</c> olarak görünüyor.
/// </para>
/// </summary>
public sealed record ChannelSettingsResponse(
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string[] To,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("use_start_tls")] bool UseStartTls);

/// <param name="SecretSet">
/// Şifreli metin bile dönmüyor: uzunluğu tek başına bir ipucu ve hiçbir
/// istemcinin işine yaramıyor.
/// </param>
public sealed record NotificationChannelResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("channel_type")] string ChannelType,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("secret_set")] bool SecretSet,
    [property: JsonPropertyName("settings")] ChannelSettingsResponse Settings,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record NotificationChannelListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("channels")] NotificationChannelResponse[] Channels);

/// <param name="Error">Redaksiyondan geçmiş; hedef adres burada görünmüyor.</param>
public sealed record ChannelTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string Error);

public sealed record AlertingNotificationStats(
    [property: JsonPropertyName("queued")] long Queued,
    [property: JsonPropertyName("delivered")] long Delivered,
    [property: JsonPropertyName("retried")] long Retried,
    [property: JsonPropertyName("abandoned")] long Abandoned);

/// <param name="TimedOut">Sıfırdan büyükse motor <b>bilmediği</b> bir şeyi "sessiz" sanmış olabilir.</param>
/// <param name="ScopedQueries">
/// T21 kabul kriteri burada ölçülüyor: kural sayısı arttığında bu sayı doğrusal
/// ötesi büyümemeli.
/// </param>
public sealed record AlertingStatsResponse(
    [property: JsonPropertyName("turns")] long Turns,
    [property: JsonPropertyName("evaluated")] long Evaluated,
    [property: JsonPropertyName("fired")] long Fired,
    [property: JsonPropertyName("suppressed")] long Suppressed,
    [property: JsonPropertyName("timed_out")] long TimedOut,
    [property: JsonPropertyName("failed")] long Failed,
    [property: JsonPropertyName("scoped_queries")] long ScopedQueries,
    [property: JsonPropertyName("notifications")] AlertingNotificationStats Notifications,

    /// <summary>
    /// Son <c>ingested_at</c>'i değerlendiricinin şimdisinden ileride olan kaynak
    /// sayısı (T27). Sıfırdan büyükse <b>sessizlik alarmları gecikiyor</b>.
    ///
    /// <para>
    /// Tele çıkması bilinçli: sayaç yalnızca süreç içinde dursaydı, hiç kimsenin
    /// bakmadığı bir sayı olurdu. Bu, kendini belli etmeyen arıza sınıfı —
    /// <see cref="AlertingStats"/>'in var olma sebebiyle aynı.
    /// </para>
    /// </summary>
    [property: JsonPropertyName("clock_skewed_sources")] long ClockSkewedSources);

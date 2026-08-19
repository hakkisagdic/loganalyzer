using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting.Notifications;

/// <param name="SourceId">Etkilenen kaynak; sessizlik tipinde dolu.</param>
public sealed record NotificationLine(
    string SourceId,
    double Value,
    double Threshold,
    string Summary);

/// <summary>
/// Kanala gidecek mesajın <b>kanaldan bağımsız</b> hâli (T22).
///
/// <para>
/// Slack, Teams, e-posta ve webhook aynı içeriği farklı biçimlerde istiyor.
/// İçeriği bir kez burada kurup biçimlendirmeyi kanala bırakmak, dört yerde
/// dört farklı "hangi alanlar mesaja giriyor" kararı olmasını engelliyor —
/// F4'te agent senaryoları da bu kanalları kullanacak ve o zaman beşinci bir
/// karar eklenmemeli.
/// </para>
/// </summary>
public sealed record NotificationMessage(
    string RuleName,
    AlertRuleType RuleType,
    string OwnerGroup,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    IReadOnlyList<NotificationLine> Lines,
    string? Link)
{
    /// <summary>Kaç tetiklenme gruplandı. Bir'den büyükse mesaj toplu.</summary>
    public int TriggerCount => Lines.Count;

    /// <summary>Tek satırlık başlık — e-posta konusu, Slack fallback metni.</summary>
    public string Title => TriggerCount > 1
        ? $"[Bizigo] {RuleName} — {TriggerCount} tetiklenme"
        : $"[Bizigo] {RuleName}";

    /// <summary>Düz metin gövde. E-posta ve Slack bunu kullanıyor.</summary>
    public string ToPlainText()
    {
        var lines = new List<string>
        {
            Title,
            $"Tip: {Describe(RuleType)}   Kapsam: {OwnerGroup}",
            $"Aralık: {WindowFrom:yyyy-MM-dd HH:mm:ss} – {WindowTo:yyyy-MM-dd HH:mm:ss} UTC",
            string.Empty,
        };

        lines.AddRange(Lines.Select(static line => "• " + line.Summary));

        if (!string.IsNullOrWhiteSpace(Link))
        {
            lines.Add(string.Empty);
            lines.Add("Aramayı aç: " + Link);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Genel webhook'un gövdesi. <b>Makine okuyacak</b>, dolayısıyla biçimlendirme
    /// değil alanlar önemli: alan adları kararlı, tarihler ISO-8601.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(new WebhookPayload(
        RuleName,
        Describe(RuleType),
        OwnerGroup,
        WindowFrom,
        WindowTo,
        TriggerCount,
        Link,
        [.. Lines.Select(static l => new WebhookLine(l.SourceId, l.Value, l.Threshold, l.Summary))]),
        JsonOptions);

    internal static string Describe(AlertRuleType type) => type switch
    {
        AlertRuleType.Threshold => "eşik",
        AlertRuleType.Ratio => "oran",
        AlertRuleType.Silence => "sessizlik",
        _ => type.ToString(),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record WebhookPayload(
        string Rule,
        string Type,
        string OwnerGroup,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        int TriggerCount,
        string? Link,
        IReadOnlyList<WebhookLine> Triggers);

    private sealed record WebhookLine(string SourceId, double Value, double Threshold, string Summary);
}

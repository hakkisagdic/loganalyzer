using System.Text.RegularExpressions;

namespace LogAnalyzer.Lite.Services;

internal enum WebhookKind
{
    Slack,
    Discord,
}

/// <summary>
/// Hangi adreslere bildirim gönderilebileceği — <b>izin listesi</b>.
///
/// <para>
/// Eski hâli herhangi bir URL'yi kabul ediyordu ve test ucu oraya POST
/// atıp durum kodunu, durum metnini ve istisna mesajını geri döndürüyordu:
/// iç ağı (<c>http://loganalyzer-lite:5060</c>, bulut meta veri adresi,
/// komşu konteynerler) taramak için yanıt kâhinli bir SSRF. Artık ana makine
/// sabit, şema <c>https</c>, port varsayılan, yol sağlayıcının webhook
/// biçiminde; yönlendirmeler de istemcide kapalı.
/// </para>
/// </summary>
internal static partial class WebhookUrlPolicy
{
    public const int MaxLength = 512;

    /// <summary>
    /// GET yanıtındaki sabit maskeler. <c>*</c> yol deseninde geçerli değil,
    /// yani maskeden türetilmiş bir değer asla adres olarak kabul edilmez —
    /// ya maskenin kendisi (= "değiştirme") ya da açık bir 400.
    /// </summary>
    public const string SlackMask = "https://hooks.slack.com/services/********";

    public const string DiscordMask = "https://discord.com/api/webhooks/********";

    public static string Mask(WebhookKind kind) => kind == WebhookKind.Slack ? SlackMask : DiscordMask;

    public static string Requirement(WebhookKind kind) => kind == WebhookKind.Slack
        ? "Slack webhook adresi https://hooks.slack.com/services/... biçiminde olmalı."
        : "Discord webhook adresi https://discord.com/api/webhooks/... veya https://discordapp.com/api/webhooks/... biçiminde olmalı.";

    public static bool TryValidate(WebhookKind kind, string? value, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();

        if (candidate.Length > MaxLength
            || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.IsDefaultPort
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var allowed = kind switch
        {
            WebhookKind.Slack =>
                string.Equals(parsed.IdnHost, "hooks.slack.com", StringComparison.OrdinalIgnoreCase)
                && SlackPath().IsMatch(parsed.AbsolutePath),
            WebhookKind.Discord =>
                (string.Equals(parsed.IdnHost, "discord.com", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parsed.IdnHost, "discordapp.com", StringComparison.OrdinalIgnoreCase))
                && DiscordPath().IsMatch(parsed.AbsolutePath),
            _ => false,
        };

        if (!allowed)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    [GeneratedRegex("^/services/[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$")]
    private static partial Regex SlackPath();

    [GeneratedRegex("^/api/webhooks/[0-9]+/[A-Za-z0-9_-]+$")]
    private static partial Regex DiscordPath();
}

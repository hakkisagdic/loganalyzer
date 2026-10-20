using System.Globalization;
using System.Text.RegularExpressions;

namespace LogAnalyzer.Lite.Configuration;

/// <summary>
/// Lite'ın ortam değişkenlerinden okunan ayarları.
///
/// <para>
/// DI'dan <b>tembel</b> çözülüyor (<c>builder.Build()</c> sonrası), yani
/// testlerin ve ortamın verdiği değerlerin tamamını görüyor. Sır taşıyan tek
/// alan <see cref="IngestToken"/>; hiçbir log satırına ya da yanıta yazılmaz.
/// </para>
/// </summary>
internal sealed partial record LiteSettings
{
    public const string IngestTokenKey = "LITE_INGEST_TOKEN";
    public const string AllowedOriginsKey = "LITE_ALLOWED_ORIGINS";
    public const string FrameAncestorsKey = "LITE_FRAME_ANCESTORS";
    public const string EnableDemoKey = "LITE_ENABLE_DEMO";
    public const string DataDirectoryKey = "LITE_DATA_DIR";
    public const string CommitKey = "GIT_SHA";

    public const string DefaultDataDirectory = "/app/data";

    /// <summary>Tanımlıysa <c>/api/v1/ingest</c> bu belirteci ister.</summary>
    public string? IngestToken { get; init; }

    /// <summary>Normalize edilmiş köken listesi (<c>https://host[:port]</c>).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary><c>frame-ancestors</c>'a <c>'self'</c> dışında eklenecek kaynaklar.</summary>
    public IReadOnlyList<string> FrameAncestors { get; init; } = [];

    public bool EnableDemo { get; init; }

    public string DataDirectory { get; init; } = DefaultDataDirectory;

    /// <summary>Doğrulanmış commit SHA'sı; geçersiz ya da boşsa <c>null</c>.</summary>
    public string? Commit { get; init; }

    /// <summary>
    /// Yok sayılan ayar girdileri — açılışta uyarı olarak loglanır. Sır
    /// içermez: belirteç burada hiçbir zaman anılmaz.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static LiteSettings From(IConfiguration configuration)
    {
        var warnings = new List<string>();

        var token = configuration[IngestTokenKey]?.Trim();

        var origins = new List<string>();
        foreach (var raw in Split(configuration[AllowedOriginsKey], ','))
        {
            if (TryNormalizeOrigin(raw, out var origin))
            {
                if (!origins.Contains(origin, StringComparer.Ordinal))
                {
                    origins.Add(origin);
                }
            }
            else
            {
                warnings.Add($"{AllowedOriginsKey}: geçersiz köken yok sayıldı ('{raw}'). Biçim: https://alan.adi[:port]");
            }
        }

        var ancestors = new List<string>();
        foreach (var raw in Split(configuration[FrameAncestorsKey], ' '))
        {
            if (FrameAncestorSource().IsMatch(raw))
            {
                var normalized = raw.ToLowerInvariant();
                if (!ancestors.Contains(normalized, StringComparer.Ordinal))
                {
                    ancestors.Add(normalized);
                }
            }
            else
            {
                warnings.Add($"{FrameAncestorsKey}: geçersiz kaynak yok sayıldı ('{raw}'). Biçim: https://alan.adi[:port]");
            }
        }

        var dataDirectory = configuration[DataDirectoryKey];

        return new LiteSettings
        {
            IngestToken = string.IsNullOrEmpty(token) ? null : token,
            AllowedOrigins = origins,
            FrameAncestors = ancestors,
            EnableDemo = string.Equals(configuration[EnableDemoKey]?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            DataDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? DefaultDataDirectory : dataDirectory.Trim(),
            Commit = SanitizeCommit(configuration[CommitKey]),
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Bir köken dizgesini <c>scheme://host[:port]</c> biçimine indirger.
    /// Yol, sorgu, parça ya da kullanıcı bilgisi taşıyan değer köken değildir.
    /// </summary>
    public static bool TryNormalizeOrigin(string? raw, out string origin)
    {
        origin = string.Empty;

        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : string.Create(CultureInfo.InvariantCulture, $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}");

        return true;
    }

    /// <summary>
    /// Yalnızca 7–40 karakterlik onaltılık bir SHA kabul edilir; başka her şey
    /// <c>null</c>. <c>/health</c> bu alanı yansıttığı için ortamdan gelen
    /// rastgele bir metin dışarı sızmamalı.
    /// </summary>
    public static string? SanitizeCommit(string? raw)
    {
        var value = raw?.Trim();

        return value is not null && CommitSha().IsMatch(value)
            ? value.ToLowerInvariant()
            : null;
    }

    private static IEnumerable<string> Split(string? value, char separator) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitSha();

    /// <summary>
    /// Yalnızca <c>http(s)://[*.]alan.adi[:port]</c>; <c>'self'</c> zaten her
    /// zaman başta. Çıplak <c>*</c> ve yalnız şema (<c>https:</c>) BİLEREK
    /// reddediliyor: ikisi de çerçevelemeyi herkese açıyor.
    /// </summary>
    [GeneratedRegex(@"^https?://(\*\.)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*(:\d{1,5})?$")]
    private static partial Regex FrameAncestorSource();
}

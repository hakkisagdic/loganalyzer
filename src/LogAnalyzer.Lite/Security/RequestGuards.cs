using System.Security.Cryptography;
using System.Text;
using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Models;
using Microsoft.Extensions.Primitives;

namespace LogAnalyzer.Lite.Security;

/// <summary>Uç meta verisi: bu uç ingest belirteci ister (tanımlıysa).</summary>
internal sealed class IngestTokenRequirement
{
    public static readonly IngestTokenRequirement Instance = new();

    private IngestTokenRequirement()
    {
    }
}

/// <summary>Uç meta verisi: durum değiştiren yönetim eylemi — CSRF/aynı köken kapısı.</summary>
internal sealed class AdminActionRequirement
{
    public static readonly AdminActionRequirement Instance = new();

    private AdminActionRequirement()
    {
    }
}

/// <summary>
/// İki kapı, tek ara katman: ingest belirteci ve yönetim eylemleri için
/// aynı köken denetimi.
///
/// <para>
/// <b>Neden uç filtresi değil ara katman:</b> uç filtreleri model bağlamadan
/// SONRA koşuyor, yani kimliği doğrulanmamış bir çağıran gövdeyi bize
/// ayrıştırtabilirdi. Bu ara katman yönlendirmeden sonra, uç çalışmadan önce
/// koşuyor ve uçların meta verisine bakıyor.
/// </para>
///
/// <para>
/// <b>Yönetim kapısı bir kimlik doğrulama DEĞİL.</b> Üretimde kimlik
/// doğrulamayı Traefik BasicAuth yapıyor; bu kapı tarayıcının o kimliği
/// başka bir sitenin isteğine iliştirmesini (CSRF) engelliyor. Özel başlık
/// zorunluluğu çapraz kökenli her isteği CORS ön uçuşuna zorluyor, köken
/// denetimi de ikinci savunma hattı.
/// </para>
/// </summary>
internal sealed class LiteRequestGuardMiddleware
{
    public const string IngestTokenHeader = "X-LogAnalyzer-Ingest-Token";
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "loganalyzer-lite";

    private readonly RequestDelegate _next;
    private readonly LiteSettings _settings;
    private readonly ILogger<LiteRequestGuardMiddleware> _logger;
    private readonly byte[]? _expectedTokenHash;

    public LiteRequestGuardMiddleware(RequestDelegate next, LiteSettings settings, ILogger<LiteRequestGuardMiddleware> logger)
    {
        _next = next;
        _settings = settings;
        _logger = logger;
        _expectedTokenHash = settings.IngestToken is { } token ? Hash(token) : null;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();

        if (endpoint is null)
        {
            return _next(context);
        }

        if (_expectedTokenHash is not null
            && endpoint.Metadata.GetMetadata<IngestTokenRequirement>() is not null
            && !TokenMatches(context.Request.Headers[IngestTokenHeader]))
        {
            _logger.LogWarning("Ingest request rejected: missing or invalid {Header} header.", IngestTokenHeader);
            return Reject(context, StatusCodes.Status401Unauthorized, new ApiError
            {
                Error = "ingest_token_invalid",
                Message = "Geçerli bir ingest belirteci gerekli.",
            });
        }

        if (endpoint.Metadata.GetMetadata<AdminActionRequirement>() is not null
            && RejectAdminRequest(context.Request) is { } reason)
        {
            _logger.LogWarning("Admin request to {Path} rejected: {Reason}.", context.Request.Path, reason);
            return Reject(context, StatusCodes.Status403Forbidden, new ApiError
            {
                Error = "admin_request_rejected",
                Message = "Bu işlem yalnızca LogAnalyzer Lite arayüzünden yapılabilir.",
            });
        }

        return _next(context);
    }

    /// <summary>
    /// Sabit zamanlı karşılaştırma. İki taraf önce SHA-256'dan geçiyor: böylece
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> uzunluk farkında
    /// erken dönse bile beklenen belirtecin uzunluğu sızmıyor.
    /// </summary>
    private bool TokenMatches(StringValues provided) =>
        provided.Count == 1
        && provided[0] is { Length: > 0 } value
        && CryptographicOperations.FixedTimeEquals(Hash(value), _expectedTokenHash);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <returns>Reddin gerekçesi (log için, sır içermez) ya da kabul ise <c>null</c>.</returns>
    private string? RejectAdminRequest(HttpRequest request)
    {
        if (!string.Equals(request.Headers[RequestedWithHeader], RequestedWithValue, StringComparison.Ordinal))
        {
            return "missing X-Requested-With";
        }

        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return "cross-site request";
        }

        var origin = request.Headers.Origin;

        if (origin.Count == 0)
        {
            // Tarayıcı dışı istemci ya da başlığı iletmeyen bir vekil; özel
            // başlık zorunluluğu yine geçerli.
            return null;
        }

        if (origin.Count > 1
            || !LiteSettings.TryNormalizeOrigin(origin[0], out var normalized))
        {
            return "unparseable Origin";
        }

        if (_settings.AllowedOrigins.Contains(normalized, StringComparer.Ordinal)
            || IsSameOrigin(normalized, request))
        {
            return null;
        }

        return "foreign Origin";
    }

    /// <summary>
    /// Köken, isteğin geldiği ana makineyle aynı mı.
    ///
    /// <para>
    /// TLS Traefik'te sonlandığı için iç istekte şema <c>http</c>; şema
    /// karşılaştırılmıyor, ana makine ve port karşılaştırılıyor. Portu
    /// olmayan bir <c>Host</c> kökenin şemasının varsayılan portu sayılıyor.
    /// <c>X-Forwarded-Host</c> da kabul ediliyor: tarayıcı çapraz kökenden bu
    /// başlığı ön uçuşsuz koyamıyor, ön uçuş da CORS'ta düşüyor.
    /// </para>
    /// </summary>
    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        var originUri = new Uri(origin);

        return HostMatches(originUri, request.Host.Value)
            || HostMatches(originUri, request.Headers["X-Forwarded-Host"].FirstOrDefault());
    }

    private static bool HostMatches(Uri origin, string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var host = HostString.FromUriComponent(hostHeader.Split(',')[0].Trim());

        // Portsuz Host, kökenin şemasının VARSAYILAN portu demek — kökenin
        // kendi portu değil. Aksi hâlde aynı ana makinedeki başka bir port
        // (http://localhost:8081) aynı köken sayılırdı.
        var requestPort = host.Port ?? (origin.Scheme == Uri.UriSchemeHttps ? 443 : 80);

        return string.Equals(host.Host, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
            && requestPort == origin.Port;
    }

    private static Task Reject(HttpContext context, int statusCode, ApiError error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(error);
    }
}

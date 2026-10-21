using LogAnalyzer.Lite.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace LogAnalyzer.Lite.Security;

/// <summary>
/// Tarayıcıya verilen politika: CORS ve güvenlik başlıkları.
///
/// <para>
/// Eski hâli <c>AllowAnyOrigin</c> ve <c>frame-ancestors *</c> idi — her
/// site arayüzü çerçeveleyebiliyor ve API'yi okuyabiliyordu. Artık varsayılan
/// yalnızca aynı köken; genişletmek <c>LITE_ALLOWED_ORIGINS</c> /
/// <c>LITE_FRAME_ANCESTORS</c> ile açık bir karar.
/// </para>
/// </summary>
internal static class BrowserPolicies
{
    public static void ConfigureCors(CorsOptions options, LiteSettings settings)
    {
        options.AddDefaultPolicy(policy =>
        {
            if (settings.AllowedOrigins.Count == 0)
            {
                // Köken listesi boş: hiçbir çapraz kökene CORS başlığı verilmez.
                return;
            }

            policy.WithOrigins([.. settings.AllowedOrigins])
                .WithMethods(HttpMethods.Get, HttpMethods.Post)
                .WithHeaders("Content-Type", LiteRequestGuardMiddleware.RequestedWithHeader);
        });
    }

    public static string ContentSecurityPolicy(LiteSettings settings) =>
        settings.FrameAncestors.Count == 0
            ? "frame-ancestors 'self'"
            : "frame-ancestors 'self' " + string.Join(' ', settings.FrameAncestors);

    public static IApplicationBuilder UseLiteSecurityHeaders(this IApplicationBuilder app, LiteSettings settings)
    {
        var csp = ContentSecurityPolicy(settings);
        var sameOriginOnly = settings.FrameAncestors.Count == 0;

        return app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = csp;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";

            if (sameOriginOnly)
            {
                // CSP'yi tanımayan eski tarayıcılar için. Ek kaynak tanımlıysa
                // X-Frame-Options bir izin listesini ifade edemiyor; o zaman
                // yalnızca CSP konuşuyor.
                headers.XFrameOptions = "SAMEORIGIN";
            }

            return next(context);
        });
    }
}

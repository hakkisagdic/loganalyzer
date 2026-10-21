using System.Reflection;
using LogAnalyzer.Lite.Configuration;

namespace LogAnalyzer.Lite.Services;

/// <summary>
/// Derlemenin taşıdığı sürüm ve commit.
///
/// <para>
/// SDK, <c>SourceRevisionId</c> biliniyorsa onu bilgilendirici sürüme
/// <c>+sha</c> olarak ekliyor. İmaj derlemesinde <c>.git</c> yok; Dockerfile
/// <c>GIT_SHA</c> derleme argümanını hem <c>SourceRevisionId</c>'ye hem ortama
/// geçiriyor. Ortamdaki değer önce geliyor.
/// </para>
/// </summary>
internal static class BuildInfo
{
    private static readonly string InformationalVersion =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static string Version { get; } = InformationalVersion.Split('+')[0];

    public static string? AssemblyCommit { get; } =
        InformationalVersion.Split('+') is [_, var revision, ..] ? LiteSettings.SanitizeCommit(revision) : null;

    public static string Commit(LiteSettings settings) => settings.Commit ?? AssemblyCommit ?? "unknown";
}

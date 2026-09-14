using System.Diagnostics;

namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>git</c>'i bir olgu kaynağı olarak okuyan tek yer.</b>
///
/// <para>
/// İki bekçi depoyu dosya sisteminden değil <c>git</c>'ten soruyor:
/// <see cref="CiCoverageTests"/> <i>CI neyi checkout ediyor</i> diye
/// (<c>git ls-files</c>), <see cref="EpicStatusTests"/> <i>hangi iş main'e
/// girdi</i> diye (<c>git log --merges</c>). Çağrı kabuğu ikisinde de aynı ve
/// tek yerde duruyor — <c>CLAUDE.md</c> §9: ortak yüzey varsa genişlet,
/// kopyalama.
/// </para>
///
/// <para>
/// <b>Sessizce dosya sistemine düşmüyor.</b> <c>git</c> yoksa ya da komut
/// hata dönerse istisna atılıyor; boş çıktı da çağıranın kararına bırakılmıyor,
/// <see cref="Lines"/> onu ayrıca reddediyor. Gerekçe §7: kapsamını yitirmiş
/// bir bekçi, olmayan bekçiyle <b>aynı</b> sonucu veriyor — ve yeşil yanıyor.
/// </para>
/// </summary>
internal static class Git
{
    /// <summary>
    /// <paramref name="arguments"/> ile <c>git</c> koşturur ve satırları döner.
    /// Boş çıktı bir bulgu değil bir <b>arıza</b>: bekçinin girdisi kaybolmuş
    /// demektir.
    /// </summary>
    internal static IReadOnlyList<string> Lines(params string[] arguments)
    {
        var lines = RawLines(arguments);

        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"`git {string.Join(' ', arguments)}` hiçbir satır döndürmedi; " +
                "bekçi boş küme üzerinde dönerdi.");
        }

        return lines;
    }

    private static IReadOnlyList<string> RawLines(string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryLayout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"git başlatılamadı; `{string.Join(' ', arguments)}` bekçisi kapsamını belirleyemez.");

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`git {string.Join(' ', arguments)}` {git.ExitCode} ile döndü: " +
                git.StandardError.ReadToEnd());
        }

        return
        [
            .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0),
        ];
    }
}

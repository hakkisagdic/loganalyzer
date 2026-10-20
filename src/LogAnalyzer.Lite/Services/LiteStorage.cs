using System.Globalization;
using System.Text;
using LogAnalyzer.Lite.Configuration;

namespace LogAnalyzer.Lite.Services;

/// <summary>
/// Lite'ın kalıcı dosyaları: tek dizin, atomik yazım, dünyaya kapalı izinler.
///
/// <para>
/// <b>Dizin <c>LITE_DATA_DIR</c>'den geliyor (varsayılan <c>/app/data</c>).</b>
/// Eski hâli <c>AppDomain.BaseDirectory</c>'den türetiyordu; yani dizin
/// yayımlama çıktısının içindeydi ve birim değiştiğinde ya da yol
/// kaydığında uygulama <b>sessizce boş</b> başlıyordu.
/// </para>
///
/// <para>
/// <b>Yazılamayan dizin açılışı durduruyor.</b> Eski hâli yazım hatasını
/// yutuyordu: olaylar bellekte duruyor, yeniden başlatmada kayboluyordu ve
/// bunu söyleyen hiçbir şey yoktu. Sağlık denetiminin kırmızı yanması bundan
/// iyi.
/// </para>
/// </summary>
internal sealed class LiteStorage
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode DataFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly ILogger<LiteStorage> _logger;

    public LiteStorage(LiteSettings settings, ILogger<LiteStorage> logger)
    {
        _logger = logger;
        DirectoryPath = Path.GetFullPath(settings.DataDirectory);

        EnsureDirectory();
        RemoveStaleTemporaryFiles();
        ProbeWritable();
    }

    public string DirectoryPath { get; }

    public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

    /// <returns>Dosyanın içeriği; dosya yoksa <c>null</c>.</returns>
    public string? ReadText(string fileName)
    {
        var path = PathFor(fileName);

        if (!File.Exists(path))
        {
            return null;
        }

        TightenFileMode(path);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>
    /// Önce aynı dizinde geçici bir dosyaya (0600) yazar, diske boşaltır, sonra
    /// hedefin üstüne taşır. Aynı dosya sisteminde <c>rename</c> atomik:
    /// okuyan ya eski ya yeni içeriği görür, yarım yazılmış bir dosyayı asla.
    /// </summary>
    public void WriteAtomic(string fileName, string content)
    {
        var target = PathFor(fileName);
        var temporary = Path.Combine(DirectoryPath, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = DataFileMode;
            }

            using (var stream = new FileStream(temporary, options))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Ayrıştırılamayan bir dosyayı kenara alır. Silmek ya da üstüne yazmak
    /// veri kaybıdır; eski hâli bozuk dosyayı yok sayıp ilk kayıtta üstüne
    /// yazıyordu.
    /// </summary>
    /// <returns>Kenara alınan dosyanın adı.</returns>
    public string Quarantine(string fileName)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var quarantined = $"{fileName}.corrupt-{stamp}-{Guid.NewGuid().ToString("N")[..8]}";
        File.Move(PathFor(fileName), PathFor(quarantined));
        return quarantined;
    }

    private void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(DirectoryPath);
                }
                else
                {
                    Directory.CreateDirectory(DirectoryPath, DirectoryMode);
                }

                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(DirectoryPath);

                if ((mode & GroupOrOther) != 0)
                {
                    // Eski imaj dizini 0777 ile yaratıyordu ve mevcut birim o
                    // izni taşıyor. Sahibi biz değilsek daraltamayız; uyarı
                    // geçiş kitabındaki chown adımına işaret ediyor.
                    try
                    {
                        File.SetUnixFileMode(DirectoryPath, DirectoryMode);
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException)
                    {
                        _logger.LogWarning(
                            "Data directory {Directory} is accessible by group/other ({Mode}) and could not be tightened; run the ownership step from the Lite runbook.",
                            DirectoryPath,
                            mode);
                    }
                }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"LogAnalyzer Lite data directory '{DirectoryPath}' cannot be created ({LiteSettings.DataDirectoryKey}).",
                error);
        }
    }

    private void RemoveStaleTemporaryFiles()
    {
        foreach (var stale in Directory.EnumerateFiles(DirectoryPath, ".*.tmp"))
        {
            TryDelete(stale);
        }
    }

    private void ProbeWritable()
    {
        const string probe = ".write-probe";

        try
        {
            WriteAtomic(probe, "ok");
            File.Delete(PathFor(probe));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"LogAnalyzer Lite data directory '{DirectoryPath}' is not writable ({LiteSettings.DataDirectoryKey}). " +
                "Incidents and alert settings would be lost on restart; refusing to start.",
                error);
        }
    }

    private void TightenFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if ((File.GetUnixFileMode(path) & GroupOrOther) != 0)
            {
                File.SetUnixFileMode(path, DataFileMode);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(
                "Data file {File} is readable by group/other and could not be tightened; run the ownership step from the Lite runbook.",
                Path.GetFileName(path));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // Geçici dosya; bir sonraki açılışta RemoveStaleTemporaryFiles topluyor.
        }
    }
}

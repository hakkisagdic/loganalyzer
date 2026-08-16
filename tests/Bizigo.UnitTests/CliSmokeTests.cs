using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bizigo.UnitTests;

/// <summary>
/// CLI'nin kendisini koşturur. Birim testleri motoru doğruluyor; burada
/// doğrulanan şey <b>bağlantı</b>: argüman ayrıştırma, dosya çözümü, çıkış kodu.
/// CI ve F2'deki UI editörü bu üç şeye güvenecek.
/// </summary>
[Collection("cli")]
public sealed class CliSmokeTests
{
    private const string DemoParser = """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: acme.demo.kv
          version: 1.0.0
        pipeline:
          - kv: { field: message }
          - convert: { fields: { dstport: int } }
        map:
          core:
            src_ip: "{{ srcip }}"
            dst_port: "{{ dstport }}"
            action: "{{ action }}"
          ocsf:
            class_uid: 4001
            activity_id: { from: action, table: ocsf_network_activity, default: 99 }
        tests:
          - name: kabul
            input: 'srcip=10.0.0.5 dstport=53 action=accept'
            expect:
              core.src_ip: "10.0.0.5"
              core.dst_port: 53
              ocsf.activity_id: 6
              parse_status: ok
        """;

    [Fact]
    public void Parser_lint_temiz_dosyada_sifir_doner()
    {
        using var file = new TempParser(DemoParser);
        var (exitCode, stdout, _) = Run("parser", "lint", file.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("acme.demo.kv", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_lint_sema_hatasinda_bir_doner()
    {
        using var file = new TempParser(DemoParser.Replace("kind: Parser", "kind: Parsr", StringComparison.Ordinal));
        var (exitCode, _, stderr) = Run("parser", "lint", file.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("Desteklenmeyen kind", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_lint_redos_riskli_pattern_i_yakalar()
    {
        // Geriye bakış doğrusal motoru kapatıyor; iç içe niceleyici artık gerçek risk.
        var risky = DemoParser.Replace(
            "  - kv: { field: message }",
            """  - grok: { field: message, patterns: ["(?<!x)^(a+)+$"], on_failure: continue }""",
            StringComparison.Ordinal);

        using var file = new TempParser(risky);
        var (exitCode, _, stderr) = Run("parser", "lint", file.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("GROK001", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_test_gomulu_testleri_kosturur()
    {
        using var file = new TempParser(DemoParser);
        var (exitCode, stdout, _) = Run("parser", "test", file.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("1 geçti, 0 kaldı", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_test_basarisizlikta_fark_gosterir()
    {
        using var file = new TempParser(DemoParser.Replace("core.dst_port: 53", "core.dst_port: 80", StringComparison.Ordinal));
        var (exitCode, _, stderr) = Run("parser", "test", file.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("beklenen: 80", stderr, StringComparison.Ordinal);
        Assert.Contains("gerçek  : 53", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_try_alanlari_basar()
    {
        using var file = new TempParser(DemoParser);
        var (exitCode, stdout, _) = Run(
            "parser", "try", file.Path, "--input", "srcip=10.0.0.5 dstport=53 action=deny");

        Assert.Equal(0, exitCode);
        Assert.Contains("parse_status: ok", stdout, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5", stdout, StringComparison.Ordinal);
        Assert.Contains("activity_id", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_try_json_ciktisi_verir()
    {
        using var file = new TempParser(DemoParser);
        var (exitCode, stdout, _) = Run(
            "parser", "try", file.Path, "--input", "srcip=10.0.0.5 dstport=53 action=accept", "--json");

        Assert.Equal(0, exitCode);

        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.Equal("ok", document.RootElement.GetProperty("parse_status").GetString());
        Assert.Equal(6, document.RootElement.GetProperty("ocsf").GetProperty("activity_id").GetInt32());
    }

    [Fact]
    public void Parser_try_turkce_govdeyi_bozmaz()
    {
        using var file = new TempParser(DemoParser);
        var (_, stdout, _) = Run(
            "parser", "try", file.Path, "--input", """msg="INTERFACE arayüzü kapandı" action=deny""");

        Assert.Contains("INTERFACE arayüzü kapandı", stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ altyapı

    private sealed class TempParser : IDisposable
    {
        public TempParser(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bizigo-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var executable = System.IO.Path.Combine(
            RepositoryLayout.Root, "src", "Bizigo.Cli", "bin", Configuration, "net10.0",
            OperatingSystem.IsWindows() ? "bizigo.exe" : "bizigo");

        Assert.SkipUnless(File.Exists(executable), $"CLI derlenmemiş: {executable}");

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryLayout.Root,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        // apphost, PATH'teki `dotnet`'e bakar. Bu makinede /usr/local/share/dotnet
        // yalnızca SDK 8/9 taşıyor; çalışan çatıyı kendi süreçlerimizden türetiyoruz.
        info.Environment["DOTNET_ROOT"] = DotnetRoot();

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(60));

        return (process.ExitCode, stdout, stderr);
    }

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string DotnetRoot()
    {
        // .../dotnet/shared/Microsoft.NETCore.App/10.0.x/ → .../dotnet
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return runtime.Parent?.Parent?.Parent?.FullName ?? string.Empty;
    }
}

using System.Text.RegularExpressions;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Geçiş kitabının (<c>deploy/README.lite.md</c>) komut sırası.</b>
///
/// <para>
/// Kitap üretimde elle, yukarıdan aşağı uygulanıyor; sıradaki bir hata ya
/// veriyi kaybettiriyor ya da arayüzü kimliksiz açıyor. Bu yüzden düzyazı
/// değil, "Geçiş kitabı" bölümündeki <c>bash</c> bloklarının komutları
/// sırasıyla çıkarılıp sınanıyor.
/// </para>
/// </summary>
public sealed partial class RunbookContractTests
{
    private const string StopOldContainer = "docker stop loganalyzer-lite";
    private const string DisableOldRestart = "docker update --restart=no loganalyzer-lite-pre-rollout";

    [GeneratedRegex(@"^```bash\n(?<body>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex BashBlock();

    /// <summary>Kaynak birimi bağlayan her komut: <c>-v "$VOL_SRC":/yol[:kip]</c>.</summary>
    [GeneratedRegex(@"-v\s+""\$VOL_SRC"":/[^\s:]+(?<mode>:\S+)?")]
    private static partial Regex SourceVolumeMount();

    [GeneratedRegex(@"^curl\b.*%\{http_code\}.*\bhttps://loglar\.")]
    private static partial Regex UnauthenticatedProbe();

    /// <summary>Geçiş kitabındaki komutlar, sırasıyla; satır devamları birleştirilmiş, yorum satırları atılmış.</summary>
    private static List<string> CutoverCommands()
    {
        var text = File.ReadAllText(RepositoryRoot.File("deploy", "README.lite.md")).ReplaceLineEndings("\n");

        var start = text.IndexOf("\n## Geçiş kitabı", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.lite.md'de 'Geçiş kitabı' bölümü yok.");

        var section = text[(start + 1)..];
        var end = section.IndexOf("\n## ", StringComparison.Ordinal);
        if (end >= 0)
        {
            section = section[..end];
        }

        var commands = new List<string>();

        foreach (Match block in BashBlock().Matches(section))
        {
            var body = block.Groups["body"].Value.Replace("\\\n", " ", StringComparison.Ordinal);

            commands.AddRange(body
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#')));
        }

        Assert.NotEmpty(commands);
        return commands;
    }

    private static int IndexOf(List<string> commands, string command)
    {
        var index = commands.FindIndex(line => line.StartsWith(command, StringComparison.Ordinal)
            && (line.Length == command.Length || char.IsWhiteSpace(line[command.Length])));
        Assert.True(index >= 0, $"Geçiş kitabında '{command}' komutu yok.");
        return index;
    }

    /// <summary>
    /// Eski sürüm spool'u her olayda yerinde, atomik olmayan biçimde yeniden
    /// yazıyor ve yeni sürüm ayrıştıramadığı spool'u kenara alıp BOŞ
    /// başlıyor. Çalışan birimden alınan bir kopya hem kopya ile durdurma
    /// arasındaki olayları kaçırıyor hem de yarım yazılmış bir dosyaya denk
    /// gelebiliyor. Kaynak birimi okuyan her komut, eski konteyner durdurulup
    /// yeniden başlatma ilkesi kapatıldıktan SONRA ve salt okunur geliyor.
    /// </summary>
    [Fact]
    public void Kaynak_birim_yalnizca_eski_konteyner_durduktan_sonra_ve_salt_okunur_okunuyor()
    {
        var commands = CutoverCommands();
        var stop = IndexOf(commands, StopOldContainer);
        var restartOff = IndexOf(commands, DisableOldRestart);

        var reads = commands
            .Select((line, index) => (line, index))
            .Where(c => SourceVolumeMount().IsMatch(c.line))
            .ToList();

        Assert.NotEmpty(reads);
        Assert.True(restartOff > stop, "Yeniden başlatma ilkesi durdurmadan sonra kapatılmalı.");

        Assert.All(reads, read =>
        {
            Assert.True(read.index > restartOff,
                $"Kaynak birim eski konteyner durmadan okunuyor: {read.line}");

            foreach (Match mount in SourceVolumeMount().Matches(read.line))
            {
                Assert.Equal(":ro", mount.Groups["mode"].Value);
            }
        });
    }

    /// <summary>
    /// <c>loglar.&lt;alan&gt;</c>'ın BasicAuth route'u compose dışında tanımlı
    /// ve Coolify'da domain atamak BasicAuth'suz ikinci bir router üretiyor.
    /// Kitap kimliksiz 401 denetimini hem eski konteyneri durdurmadan önce
    /// (route bugün korumalı mı) hem de yeni konteyner geldikten sonra
    /// yapıyor ve yeni konteynerde Coolify'ın ürettiği bir router kuralı
    /// olmadığını denetliyor.
    /// </summary>
    [Fact]
    public void Kimliksiz_401_denetimi_durdurmadan_once_ve_yeni_konteynerden_sonra_yapiliyor()
    {
        var commands = CutoverCommands();
        var stop = IndexOf(commands, StopOldContainer);
        var newContainer = commands.FindIndex(line => line.StartsWith("NEW=", StringComparison.Ordinal));
        Assert.True(newContainer > stop, "Yeni konteyneri bulan 'NEW=' satırı durdurmadan sonra gelmeli.");

        var probes = commands
            .Select((line, index) => (line, index))
            .Where(c => UnauthenticatedProbe().IsMatch(c.line))
            .Select(c => c.index)
            .ToList();

        Assert.Contains(probes, index => index < stop);
        Assert.Contains(probes, index => index > newContainer);

        Assert.Contains(commands.Skip(newContainer + 1), line =>
            line.StartsWith("docker inspect \"$NEW\"", StringComparison.Ordinal)
            && line.Contains(@"traefik\.http\.routers\.", StringComparison.Ordinal));
    }
}

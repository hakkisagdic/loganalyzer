namespace Bizigo.UnitTests;

/// <summary>
/// <b>Komut çekirdeği konsola yazmıyor</b> — ve bu bir üslup tercihi değil.
///
/// <para>
/// <b>Gerekçe MCP tarafında ve mekanik:</b> <c>stdout</c> protokolün kendisi.
/// Oraya düşen tek bir satır JSON-RPC akışını bozuyor ve istemci bunu
/// <b>ayrıştırma hatası</b> olarak görüyor — yani arıza ürünü değil
/// <b>ayrıştırıcıyı</b> işaret ediyor, ve hatanın kaynağı ile belirtisi farklı
/// katmanlarda oluyor. Bir komut çekirdeğine sızacak tek bir
/// <c>Console.WriteLine</c>, <c>bizigo mcp serve</c>'ü bozar.
/// </para>
///
/// <para>
/// <b>Bekçi M02'nin ölçümünden doğdu.</b> Ölçüldü: hesap katmanında
/// (<c>Fields/</c>, <c>Seeding/</c>, <c>ParserToolbox</c>) <c>Console</c>
/// çağrısı <b>sıfırdı</b> — ama o sıfırı tutan <b>hiçbir şey yoktu</b>.
/// Bu deponun kuralı net: bir şey ölçülmediyse çalıştığı varsayılmaz.
/// </para>
///
/// <para>
/// <b>Neden kaynak taraması, yansıma değil:</b> bir <c>Console.WriteLine</c>
/// çağrısı IL'de bir metot çağrısı ve onu yansımayla görmek IL çözümlemesi
/// ister. Kaynak taraması bu depoda yerleşik bir kalıp — <c>CiCoverageTests</c>
/// <c>ci.yml</c>'ı, <c>ScenarioEngineTests</c> faz belgesini, <c>AlertLinkTargetTests</c>
/// <c>criteria.ts</c>'i aynı şekilde okuyor.
/// </para>
/// </summary>
public sealed class CommandCoreConsoleTests
{
    /// <summary>
    /// <b>İki sunum katmanının ikisi de burada DEĞİL</b> — CLI konsola yazıyor
    /// (yazması gerekiyor), MCP sunumu <c>McpToolResult</c> döndürüyor.
    /// Yasak yalnızca ortak çekirdekte.
    /// </summary>
    private static string CoreDirectory =>
        Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Commands");

    [Fact]
    public void Komut_cekirdeginde_Console_kullanimi_yok()
    {
        var files = Directory
            .EnumerateFiles(CoreDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Boş küme üzerinde bu test sessizce geçerdi — ve yeşilliği hiçbir şey
        // ifade etmezdi. Bu deponun beş kez adını koyduğu hâl.
        Assert.True(files.Length > 0, $"Komut çekirdeğinde hiç kaynak dosyası bulunamadı: {CoreDirectory}");

        var offenders = files
            .Select(path => (Path: Path.GetRelativePath(RepositoryLayout.Root, path),
                             Lines: Offending(File.ReadAllLines(path))))
            .Where(static entry => entry.Lines.Count > 0)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Komut çekirdeği konsola yazıyor:\n" +
            string.Join("\n", offenders.Select(o =>
                $"  {o.Path}: satır {string.Join(", ", o.Lines)}")) +
            "\n\nstdout MCP tarafında PROTOKOLÜN KENDİSİ; oraya düşen bir satır " +
            "JSON-RPC akışını bozar ve istemci bunu ayrıştırma hatası olarak görür. " +
            "Çıktı çekirdekten bir DEĞER olarak dönmeli; onu çizmek sunumun işi.");
    }

    /// <summary>
    /// <c>Console</c> geçen satır numaraları. Yorum satırları elenmiyor ve
    /// bilerek: bir yorumda <c>Console</c> geçmesi de bu kuralın tartışıldığı
    /// bir yer demek, ve kural değişiyorsa bekçi de değişmeli.
    /// </summary>
    private static IReadOnlyList<int> Offending(string[] lines) =>
    [
        .. lines
            .Select(static (line, index) => (line, number: index + 1))
            .Where(static entry => entry.line.Contains("Console.", StringComparison.Ordinal))
            .Select(static entry => entry.number),
    ];
}

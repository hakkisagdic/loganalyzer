using System.Diagnostics;
using System.Runtime.InteropServices;
using Bizigo.Parsing.Samples;

namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>.githooks/pre-push</c> kapısının kendisi.</b>
///
/// <para>
/// Kanca <c>BIZIGO_CI_STATUS</c> diye bir alan taşıyor ve yorumunda
/// <i>"TEST DİKİŞİ"</i> yazıyor — <b>ama o dikişi kullanan bir test yoktu.</b>
/// Yani kancanın dört dalı (geç · engelle · koşum sürüyor · bilinmeyen değer)
/// yalnızca elle, bir kez ve kayıt bırakmadan denenmişti. Dikişi olup testi
/// olmayan bir bekçi, bu deponun adını koyduğu sınıfın kendisi: mekanizma
/// var, ölçüm yok.
/// </para>
///
/// <para>
/// <b>Bu testin var olma sebebi ölçülmüş bir kusur.</b> Kanca CI durumunu
/// <c>gh run list --branch main --limit 1</c> ile okuyordu, yani main'deki en
/// son koşumu — <b>hangi akış olduğuna bakmadan</b>. <c>ci-red.yml</c> bir
/// <c>workflow_run</c> tetikleyicisiyle CI'dan SONRA bittiği için "en son
/// koşum" neredeyse her zaman bekçi işiydi, ve bekçi işi kendi içinde
/// başarılıyken <c>success</c> döndürüyordu — <b>CI kırmızıyken bile</b>.
/// Ölçüldü (<c>07a461d</c>): süzgeçsiz sorgu <c>success</c> dedi, aynı
/// commit'te CI <c>failure</c>'dı.
/// </para>
///
/// <para>
/// Testler <b>ağı hiç kullanmıyor</b>: dikiş tam bu yüzden var. Sorgunun
/// kendisini (akış süzgeci) burada sınamak mümkün değil — o bir <c>gh</c>
/// çağrısı — ve o yüzden süzgeç <see cref="Sorgu_akisi_suzuyor"/> ile
/// <b>metin düzeyinde</b> çivilendi. Zayıf bir iddia, ama süzgecin sessizce
/// kaldırılmasını yakalıyor ve sessizce kaldırılması bu kusurun tam kendisiydi.
/// </para>
/// </summary>
public sealed class PrePushHookTests
{
    private static string HookPath => Path.Combine(RepositoryLayout.Root, ".githooks", "pre-push");

    /// <summary>
    /// <c>sh</c> gerektiriyor. Windows'ta <b>beyanlı</b> atlanıyor: sessizce
    /// geçen bir test, koşmadığı hâlde koşmuş sayılırdı (§2, madde 2).
    /// </summary>
    private static bool ShellAvailable => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Theory]
    [InlineData("success", 0)]
    [InlineData("skipped", 0)]
    [InlineData("neutral", 0)]
    [InlineData("failure", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("timed_out", 1)]
    [InlineData("startup_failure", 1)]

    // Tanınmayan bir değer KIRMIZI sayılıyor. Tersi — "herhalde iyidir" diye
    // geçmek — bu depodaki sessiz-yeşil sınıfının kendisi olurdu.
    [InlineData("bilinmeyen-bir-durum", 1)]
    public async Task Main_e_push_CI_durumuna_gore_duruyor(string ciStatus, int expectedExit)
    {
        Assert.SkipUnless(ShellAvailable, "`sh` yok (Windows) — kanca kabuk betiği.");

        var exit = await RunHookAsync("refs/heads/main", ciStatus);

        Assert.Equal(expectedExit, exit);
    }

    /// <summary>
    /// <b>Koşum sürüyorsa push GEÇİYOR</b> — ve bu dalın yerleşimi ölçülerek
    /// düzeltildi.
    ///
    /// <para>
    /// <c>conclusion</c> alanı sürmekte olan bir koşumda JSON <c>null</c> ve
    /// <c>--jq</c> onu <c>"null"</c> <b>dizesi</b> olarak basıyor — boş değil,
    /// yani "okunamadı" kontrolüne düşmüyor. Eski hâlde bu kırmızı sayılıyordu:
    /// bir push'un hemen ardından ikinci bir push <c>SON CI koşumu: null</c>
    /// diyerek engelleniyordu.
    /// </para>
    ///
    /// <para>
    /// Kontrol bir süre test dikişinin <b>içinde</b> kaldı ve o hâliyle bu test
    /// dala hiç uğramıyordu — yazılmış ama sınanmamış bir dal. Dikişin
    /// ölçemediği bir dal, ölçülmemiş bir daldır.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kosum_surerken_push_engellenmiyor()
    {
        Assert.SkipUnless(ShellAvailable, "`sh` yok (Windows) — kanca kabuk betiği.");

        Assert.Equal(0, await RunHookAsync("refs/heads/main", "null"));
    }

    /// <summary>
    /// <b><c>main</c> dışına push hiç sorgulanmıyor.</b> Kanca yalnızca main'i
    /// koruyor; her dalda CI'ya bakmak, ajanların her teslimini bir CI koşumuna
    /// bağlardı ve bu depoda dal başına CI koşumu yok.
    /// </summary>
    [Fact]
    public async Task Main_disina_push_kirmiziyken_bile_geciyor()
    {
        Assert.SkipUnless(ShellAvailable, "`sh` yok (Windows) — kanca kabuk betiği.");

        Assert.Equal(0, await RunHookAsync("refs/heads/t99-bir-dal", "failure"));
    }

    /// <summary>
    /// <b>Sorgu akış dosyasına göre süzülüyor.</b>
    ///
    /// <para>
    /// Bu iddia metin düzeyinde ve zayıflığı bilinçli: gerçek sorguyu sınamak
    /// bir <c>gh</c> çağrısı ve ağ ister. Ama yakaladığı şey tam olarak
    /// düzeltilen kusur — süzgecin <b>olmaması</b>. Süzgeç kaldırılırsa kanca
    /// yine çalışır, yine yeşil görünür ve yine bekçi işinin sonucunu okur;
    /// hiçbir davranış testi bunu göremez.
    /// </para>
    ///
    /// <para>
    /// Görünen ad (<c>name:</c>) yerine dosya adı (<c>ci.yml</c>) çivilendi:
    /// görünen ad bir düzenlemeyle değişir ve süzgeç sessizce boşa düşer.
    /// </para>
    /// </summary>
    [Fact]
    public void Sorgu_akisi_suzuyor()
    {
        var hook = File.ReadAllText(HookPath);

        var runListCalls = hook
            .Split('\n')
            .Where(static line => line.Contains("gh run list", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(runListCalls);

        Assert.All(runListCalls, line => Assert.Contains("--workflow ci.yml", line, StringComparison.Ordinal));
    }

    private static async Task<int> RunHookAsync(string remoteRef, string ciStatus)
    {
        var info = new ProcessStartInfo("sh", HookPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryLayout.Root,
        };

        info.Environment["BIZIGO_CI_STATUS"] = ciStatus;

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("`sh` başlatılamadı.");

        // stdin biçimi git'in verdiğiyle aynı: <yerel ref> <yerel sha> <uzak ref> <uzak sha>
        await process.StandardInput.WriteLineAsync(
            $"refs/heads/yerel aaaaaaa {remoteRef} bbbbbbb");
        process.StandardInput.Close();

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return process.ExitCode;
    }
}

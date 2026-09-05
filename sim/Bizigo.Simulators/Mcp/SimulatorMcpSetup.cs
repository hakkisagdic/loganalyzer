using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <c>bizigo-sim</c> araçlarının DI kaydı — <b>ve derlemenin adının söylendiği
/// yer</b>.
///
/// <para>
/// ⚠ <b>GEÇİCİ — M05 bunu değiştirecek.</b> Bu tipin ikinci işi
/// (<see cref="ToolAssembly"/>) bir ara çözüm. <c>AddBizigoMcpCore</c> araç
/// derlemelerini <b>açıkça</b> almaya başladığında burası o listeye verilen bir
/// değere dönüşecek; bugün öyle bir parametre yok.
/// </para>
///
/// <para>
/// <b>Neden bir ara çözüm gerekiyor — ölçülmüş bir tuzak.</b> M04 ölçtü:
/// derleyici, <b>kodunda hiçbir tipine dokunulmayan</b> bir
/// <c>ProjectReference</c>'ı derleme meta verisinden <b>buduyor</b>. M04'ün beş
/// aracı ayrı bir derlemedeydi, çözüm <c>0 hata 0 uyarı</c> derlendi, uyum
/// kapısı <b>yeşil kaldı</b> — ve sunucu hâlâ tek araç ilan ediyordu, çünkü
/// <c>McpToolDiscovery.ProductAssemblies</c> referans tablosunu yürüyor ve
/// budanmış derleme orada <b>yok</b>. Hata yok, sayaç yok, belirti yok.
/// </para>
///
/// <para>
/// <b>Bu kolda bugün ölçülen hâl, ve neden yine de buna dayanmıyoruz.</b>
/// <c>bizigo.dll</c>'in derleme referansları okundu:
/// <c>Bizigo.Simulators</c> → <b>VAR</b>, <c>Bizigo.Query</c> → <b>YOK</b>
/// (budanmış, tuzağın depoda canlı duran örneği). Yani bugün
/// <c>bizigo mcp serve --surface bizigo-sim</c> yedi aracı <i>zaten</i>
/// buluyor. <b>Ama kazara:</b> bağ tek bir dosyaya asılı —
/// <c>FleetCommandHandlers</c> <c>bizigo fleet apply</c> için
/// <c>FleetDefinition</c>/<c>FleetStore</c>'a dokunuyor. Biri o komutu
/// kaldırır ya da başka bir derlemeye taşırsa referans budanır ve
/// <b>yedi araç sessizce ilan edilmez</b>.
/// </para>
///
/// <para>
/// Bu uzantı bağı kazadan çıkarıyor: <c>Bizigo.Cli</c> onu <b>MCP yolundan</b>
/// çağırıyor, yani araçların ilan edilmesi araçların kaydına bağlı — filo
/// komutunun varlığına değil.
/// </para>
/// </summary>
public static class SimulatorMcpSetup
{
    /// <summary>Depo kökünü ezen ortam değişkeni.</summary>
    public const string RepositoryRootVariable = "BIZIGO_SIM_REPO_ROOT";

    /// <summary>Durum dosyasının yolunu ezen ortam değişkeni.</summary>
    public const string StatePathVariable = "BIZIGO_SIM_STATE_PATH";

    /// <summary>Collector adresini ezen ortam değişkeni.</summary>
    public const string CollectorHostVariable = "BIZIGO_SIM_COLLECTOR_HOST";

    /// <summary>
    /// <c>sim.*</c> araçlarının yaşadığı derleme.
    ///
    /// <para>
    /// ⚠ <b>Geçici:</b> M05'in açık derleme listesi geldiğinde bu değer oraya
    /// verilecek. Bugün bir <b>kayıt</b>: keşif bu derlemeyi bulamazsa cevabın
    /// <i>"neden bulamadı"</i> olması gereken yer burası.
    /// </para>
    /// </summary>
    public static Assembly ToolAssembly => typeof(SimulatorMcpSetup).Assembly;

    /// <summary>
    /// Araçların bağımlılıklarını kaydeder ve <b>bu derlemeye gerçek bir bağ
    /// kurar</b>.
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="repositoryRoot">
    /// Depo kökü. <see langword="null"/> ise <see cref="RepositoryRootVariable"/>,
    /// o da yoksa <c>Bizigo.sln</c> aranarak bulunuyor.
    /// </param>
    /// <param name="statePath">
    /// Durum dosyası. <see langword="null"/> ise <see cref="StatePathVariable"/>,
    /// o da yoksa depo köküne göre
    /// <see cref="SimulatorStateStore.DefaultRelativePath"/>.
    /// </param>
    /// <param name="collectorHost">Syslog hedefi.</param>
    /// <param name="clock">Zaman kaynağı; testler sabit bir an veriyor.</param>
    public static IServiceCollection AddBizigoSimulatorTools(
        this IServiceCollection services,
        string? repositoryRoot = null,
        string? statePath = null,
        string? collectorHost = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // KÖK TEMBEL ÇÖZÜLÜYOR, KAYIT SIRASINDA DEĞİL — ve bu ölçülerek
        // düzeltildi.
        //
        // İlk hâli kökü burada çözüp bulamazsa FIRLATIYORDU. Sonucu şu:
        // `bizigo mcp serve` bu kaydı her iki yüzey için de yapmak zorunda
        // (aşağıdaki gerekçe), yani depo dışında koşan bir ÜRÜN yüzeyi
        // sunucusu simülatör yüzünden hiç ayağa kalkmazdı. Kaydetmek bir söz
        // vermek değil; söz araç kurulduğunda veriliyor.
        var host = collectorHost ?? Environment.GetEnvironmentVariable(CollectorHostVariable);

        services.AddSingleton(_ =>
        {
            var root = ResolveRepositoryRoot(repositoryRoot);

            var state = statePath
                ?? Environment.GetEnvironmentVariable(StatePathVariable)
                ?? Path.Combine(root, SimulatorStateStore.DefaultRelativePath);

            return new SimulatorStateStore(state, clock);
        });

        services.AddSingleton(provider => new SimulatorMcpContext(
            ResolveRepositoryRoot(repositoryRoot),
            provider.GetRequiredService<SimulatorStateStore>(),
            host));

        // `sim.webhook.emit` için. TryAdd DEĞİL Add: barındıran süreç kendi
        // `HttpClient`'ını kaydetmişse onu ezmek istemiyoruz — ama bugün tek
        // barındıran `Bizigo.Cli` ve orada kayıtlı bir tane yok. TryAdd
        // kullanmamanın sebebi ölçülebilir olması: iki kayıt olsaydı hangisinin
        // kazandığı DI'nın "son kayıt kazanır" kuralına düşerdi ve bu, bu
        // deponun "son satır sessizce kazanıyor" dediği şeklin aynısı olurdu.
        services.AddSingleton(_ => new HttpClient());

        return services;
    }

    private static string ResolveRepositoryRoot(string? supplied) =>
        supplied
        ?? Environment.GetEnvironmentVariable(RepositoryRootVariable)
        ?? SimulatorMcpContext.DiscoverRepositoryRoot()
        ?? throw new InvalidOperationException(
            "Simülatör araçları için depo kökü bulunamadı: `Bizigo.sln` hiçbir üst dizinde yok. "
            + $"`{RepositoryRootVariable}` ile açıkça verin. ATLANMIYOR — köksüz kurulan araçlar "
            + "filoyu boş görür ve boş bir filo 'cihaz yok' diye okunur.");
}

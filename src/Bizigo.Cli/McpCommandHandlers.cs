using System.Reflection;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo mcp serve</c> — MCP'nin <b>stdio</b> taşıması.
///
/// <para>
/// <b>Neden ayrı bir çalıştırılabilir değil.</b> stdio sunucusu için ikinci bir
/// host projesi açmak, M02'nin (<i>komut çekirdeği ve CLI paritesi</i>) tam
/// olarak kaçınmak için var olduğu ikinci kopyayı doğururdu: aynı komutların
/// bir kez CLI'da bir kez MCP host'unda kurulması. M01 yalnızca <b>girişi</b>
/// açıyor; komut çekirdeği M02'nin işi ve o geldiğinde bu dosyanın çözdüğü
/// servis grafiği ortak çekirdeği de taşıyacak.
/// </para>
///
/// <para>
/// <b>stdout protokolün kendisi.</b> Bu komut hiçbir şey yazdırmıyor — tek bir
/// bilgi satırı bile JSON-RPC akışını bozar ve istemci bunu ayrıştırma hatası
/// olarak görür, yani ürün hakkında hiçbir şey söylemeyen bir arıza. Günlükler
/// <c>--verbose</c> ile <b>stderr</b>'e açılıyor.
/// </para>
/// </summary>
public static class McpCommandHandlers
{
    /// <summary>
    /// Sunucuyu koşturur. Süreç kapanana kadar dönmez.
    /// </summary>
    /// <param name="surfaceName">
    /// <c>bizigo</c> ya da <c>bizigo-sim</c>. Tanınmayan ad <b>reddediliyor</b>:
    /// bir varsayılana düşmek, yüzeyini yanlış yazan çağrıyı sessizce ürün
    /// verisi kümesine bağlardı.
    /// </param>
    /// <param name="boundaryName">
    /// <c>internal</c> ya da <c>external</c> — yüzeyin <b>K6 beyanı</b> (M06).
    /// <b>Varsayılanı yok</b> ve olmamalı; gerekçe aşağıda.
    /// </param>
    /// <param name="verbose">Günlükleri stderr'e ver.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task<int> ServeAsync(
        string surfaceName,
        string? boundaryName,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (McpSurfaces.Parse(surfaceName) is not { } surface)
        {
            await Console.Error.WriteLineAsync(
                $"Bilinmeyen MCP yüzeyi: '{surfaceName}'. "
                + $"Beklenen: '{McpSurfaces.ProductName}' ya da '{McpSurfaces.SimulatorName}'.")
                .ConfigureAwait(false);

            return 2;
        }

        // K6 beyanı. `--surface`'in aksine VARSAYILANI YOK — ve bu, ikisinin
        // farklı sorular sorduğunun kaydı: yüzeyin makul bir varsayılanı var
        // (ürün), sınırın YOK. Sınırı varsayan her değer, beyan etmeyi unutan
        // operatörün yerine karar vermiş olurdu.
        //
        // Buradan "stdio, demek ki iç ağ" diye geçilmedi: gerekçe
        // `McpStdioHost.RunAsync`'in `boundary` parametresinde yazılı ve özeti
        // şu — bu taşımanın en olası istemcisi log metnini buluta gönderiyor.
        if (!Enum.TryParse<DataBoundary>(boundaryName, ignoreCase: true, out var declared)
            || declared == DataBoundary.Unspecified)
        {
            await Console.Error.WriteLineAsync(
                $"MCP ağ sınırı beyan edilmedi ya da çözümlenemedi: '{boundaryName}'. "
                + "`--data-boundary internal` ya da `--data-boundary external` yazın. "
                + "Beyansız bir yüzey 'iç ağ' SAYILMIYOR: K6 (`log verisi kurum dışına "
                + "çıkmaz`) bir varsayılan kabul etmiyor.")
                .ConfigureAwait(false);

            return 2;
        }

        McpBoundaryDeclaration boundary;

        try
        {
            boundary = McpBoundaryDeclaration.Declare(declared, "CLI seçeneği: `--data-boundary`");
            McpBoundaryGate.Require(boundary, surface);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            // Kapının reddi burada YAKALANIYOR çünkü bu bir operatör hatası,
            // bir program kusuru değil: yığın izi basmak yerine ne yazması
            // gerektiğini söyleyip çıkış kodu veriyoruz. stdout'a tek satır
            // gitmiyor — orası protokolün.
            await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);

            return 2;
        }

        using var loggerFactory = verbose
            ? LoggerFactory.Create(logging => logging
                .SetMinimumLevel(LogLevel.Debug)

                // stderr — stdout protokolün. Konsol sağlayıcısının varsayılanı
                // stdout ve orada bırakmak akışı ilk günlük satırında bozardı.
                .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace))
            : LoggerFactory.Create(static logging => logging.ClearProviders());

        var services = new ServiceCollection().BuildServiceProvider();

        await McpStdioHost.RunAsync(
            surface,
            boundary,
            Assembly.GetExecutingAssembly(),
            services,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);

        return 0;
    }
}

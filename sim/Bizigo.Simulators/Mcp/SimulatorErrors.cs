using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <c>sim.*</c> araçlarının <b>paylaştığı</b> hata gövdeleri.
///
/// <para>
/// Dört araç <i>"böyle bir cihaz yok"</i> diyebiliyor. Dördü ayrı cümle
/// yazsaydı, biri bilinen cihazları listelemeyi unuturdu ve o araçtan gelen ret
/// modeli <b>tahmin etmeye</b> bırakırdı — <c>Scenarios.Reject</c>'in bilinen
/// senaryoları saymasının sebebiyle aynı sebep.
/// </para>
/// </summary>
public static class SimulatorErrors
{
    /// <summary>
    /// Cihaz bulunamadı. <b>Bilinenleri sayıyor</b>: adı yanlış yazılmış bir
    /// çağrı, doğrusunu görmeden düzeltilemez.
    /// </summary>
    public static McpToolError UnknownDevice(string device, SimulatorMcpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var fleet = context.LoadFleet();
        var known = string.Join(", ", fleet.Profiles.Select(p => p.Id).Order(StringComparer.Ordinal));

        // BOŞ FİLO AYRI BİR CÜMLE. "Bilinenler: " diye biten bir mesaj, filonun
        // okunamadığı hâli "cihaz adını yanlış yazdın" gibi gösterirdi — yani
        // arayan kişiyi profil dosyasının peşine düşürürdü, oysa sorun
        // filonun kendisinde.
        var message = fleet.Profiles.Count == 0
            ? $"'{device}' diye bir cihaz yok — ve filoda HİÇ cihaz okunamadı. "
              + $"Profil kataloğu: {context.ProfileDirectory}."
              + (fleet.Errors.Count > 0 ? $" Filo bulguları: {string.Join("; ", fleet.Errors)}" : string.Empty)
            : $"'{device}' diye bir cihaz yok. Bilinenler: {known}";

        return new McpToolError(
            McpToolError.NotFound,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device"] = device,
                ["known_device_count"] = fleet.Profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }
}

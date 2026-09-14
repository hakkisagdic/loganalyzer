namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <b>Bir cihazın hangi yüzeyleri taklit ettiği</b> — ve o yüzeylerin tel adı.
///
/// <para>
/// <b>Neden ayrı bir tip, neden her araçta iki satır değil.</b> Bu sorunun tek
/// bir cevabı olması gerekiyor: <c>sim.scenario.set</c> yanlış yüzey kararını
/// buradan veriyor, <c>sim.fleet.list</c> aynı listeyi ekrana yazıyor. İki
/// yerde ayrı ayrı yazılsaydı ayrışabilirlerdi ve ayrışma <b>sessiz</b> olurdu:
/// liste cihazı <i>"syslog + config"</i> gösterirken <c>set</c> onu yalnızca
/// syslog sayar, kullanıcı reddi anlamaz. Bu, <c>Scenarios.IsBaseline</c>'ın
/// S04'te öğrettiği dersin aynısı — <i>bir kavramı tekilleştirmek, onu tanıyan
/// predicate'i tekilleştirmekle aynı şey değil.</i>
/// </para>
///
/// <para>
/// <b><see cref="ScenarioSurface.Infrastructure"/> bilerek yok.</b> Altyapı bir
/// cihazın <i>taklit ettiği</i> bir yüzey değil; simülatörün elinde olmayan bir
/// eylem (<c>Scenarios.Reject</c> bunu kendi cümlesiyle söylüyor: <i>"koordinatör
/// ilgili servisi durdurup koşumu yapar"</i>). Bir cihazın altyapı yüzeyi
/// <b>olamaz</b>, o yüzden buradaki küme onu hiç üretmiyor.
/// </para>
/// </summary>
public static class SimulatorSurfaces
{
    /// <summary>
    /// Cihazın taklit ettiği yüzeyler. Sıra <b>belirli</b>: config önce.
    ///
    /// <para>
    /// Belirli olması bir süs değil — <c>sim.scenario.set</c> reddi yazarken
    /// bu listeden bir yüzey seçiyor ve seçim değişkense aynı ret iki farklı
    /// cümle üretirdi. Aynı girdiye aynı cevap, bu depoda bir kabul ölçütü.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScenarioSurface> Of(SimulatorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var surfaces = new List<ScenarioSurface>(2);

        if (profile.Config is not null)
        {
            surfaces.Add(ScenarioSurface.Config);
        }

        if (profile.Syslog is not null)
        {
            surfaces.Add(ScenarioSurface.Syslog);
        }

        return surfaces;
    }

    /// <summary>
    /// Yüzeyin tel adı. <c>Scenarios.Describe</c> ile <b>aynı sözcükler</b>
    /// (<c>config</c> / <c>syslog</c> / <c>altyapı</c> → burada
    /// <c>infrastructure</c>): ret mesajı Türkçe cümlenin içinden, şema alanı
    /// buradan geliyor ve ikisinin aynı kavramı adlandırdığı okunabilir olmalı.
    /// </summary>
    public static string WireName(ScenarioSurface surface) => surface switch
    {
        ScenarioSurface.Config => "config",
        ScenarioSurface.Syslog => "syslog",
        ScenarioSurface.Infrastructure => "infrastructure",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Bilinmeyen senaryo yüzeyi."),
    };
}

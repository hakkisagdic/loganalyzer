namespace Bizigo.Simulators.Mcp;

/// <summary>
/// Uyum kapısının örnek çağrılarının kullandığı <b>sabit</b> domain nesneleri.
///
/// <para>
/// <b>Neden sabit bir profil, neden katalogdan okunan gerçek bir profil değil.</b>
/// Örnek çağrının işi şemanın gerçekten üretilen çıktıyı tarif ettiğini
/// göstermek; katalogdan okusaydı kapı <b>katalogun içeriğine</b> bağlı olurdu ve
/// bir profil silindiğinde <i>şema</i> hakkında hiçbir şey söylemeyen bir
/// kırmızı üretirdi. Yanlış sebeple kırmızı yanan bir kapı, yeşilliği anlamsız
/// olan kadar zararlı: iki turdan sonra kimse ona bakmıyor.
/// </para>
///
/// <para>
/// <b>Ama örnek yine de GERÇEK şekillendirmeden geçiyor</b> — sabit olan girdi,
/// çıktı değil. Elle yazılmış bir JSON sabiti döndürmek şemayı değil kendini
/// doğrulardı (<c>BizigoMcpTool.SampleAsync</c> belgesi bunu adıyla yasaklıyor).
/// </para>
/// </summary>
public static class SimulatorSamples
{
    /// <summary>
    /// Örneklerin zaman damgası — <b>sabit</b>.
    ///
    /// <para>
    /// <c>UtcNow</c> olsaydı örnek çağrının çıktısı her koşumda değişirdi ve
    /// kapı <i>"şema uyuyor mu"</i> ile <i>"saat kaç"</i> sorularını
    /// karıştırırdı. §6'nın maddesi: bir testin geçme sebebinin duvar saatiyle
    /// ilgisi olmamalı.
    /// </para>
    /// </summary>
    public static readonly DateTimeOffset Moment =
        new(2026, 8, 18, 9, 19, 47, TimeSpan.Zero);

    /// <summary>
    /// Örnek cihaz. Gerçek bir profilin şeklinde ama katalogdan bağımsız:
    /// hem config hem syslog yüzeyi taşıyor, çünkü örneğin işi <b>şemanın
    /// bütün alanlarını</b> doldurabilmek.
    /// </summary>
    public static SimulatorProfile Profile { get; } = new()
    {
        Id = "ornek-cihaz-01",
        Vendor = "ornek",
        Product = "ornek-guvenlik-duvari",
        Hostname = "ornek-cihaz-01.ornek.invalid",
        OwnerGroup = "network/core",
        ParserId = "ornek-syslog",
        Encoding = "auto",
        Config = new SimulatorConfigSet { Baseline = "baseline.conf" },
        Syslog = new SimulatorSyslog { RatePerMinute = 60, Transport = "tcp" },
    };
}

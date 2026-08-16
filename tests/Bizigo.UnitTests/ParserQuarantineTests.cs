using Bizigo.Parsing.Engine;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

public sealed class ParserQuarantineTests
{
    [Fact]
    public void Tek_zaman_asimi_karantina_sebebi_degil()
    {
        var quarantine = new ParserQuarantine(threshold: 3);

        Assert.False(quarantine.ReportTimeout("acme.p@1.0.0"));
        Assert.False(quarantine.IsQuarantined("acme.p@1.0.0"));
    }

    [Fact]
    public void Esik_asilinca_karantinaya_alinir()
    {
        var quarantine = new ParserQuarantine(threshold: 3);

        Assert.False(quarantine.ReportTimeout("acme.p@1.0.0"));
        Assert.False(quarantine.ReportTimeout("acme.p@1.0.0"));
        Assert.True(quarantine.ReportTimeout("acme.p@1.0.0"));

        Assert.True(quarantine.IsQuarantined("acme.p@1.0.0"));
        Assert.Single(quarantine.Entries);
    }

    [Fact]
    public void Pencere_disinda_kalan_zaman_asimlari_eskir()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var quarantine = new ParserQuarantine(threshold: 3, window: TimeSpan.FromMinutes(5), timeProvider: time);

        quarantine.ReportTimeout("acme.p@1.0.0");
        quarantine.ReportTimeout("acme.p@1.0.0");

        time.Advance(TimeSpan.FromMinutes(6));

        // Pencere kaydı — eskiyen iki sayım düştü, üçüncü tek başına yetmez.
        Assert.False(quarantine.ReportTimeout("acme.p@1.0.0"));
        Assert.False(quarantine.IsQuarantined("acme.p@1.0.0"));
    }

    [Fact]
    public void Parser_anahtari_surumu_icerir()
    {
        var quarantine = new ParserQuarantine(threshold: 1);
        quarantine.ReportTimeout("acme.p@1.0.0");

        Assert.True(quarantine.IsQuarantined("acme.p@1.0.0"));
        Assert.False(quarantine.IsQuarantined("acme.p@1.1.0"));
    }

    [Fact]
    public void Serbest_birakma_sayaci_da_sifirlar()
    {
        var quarantine = new ParserQuarantine(threshold: 2);
        quarantine.ReportTimeout("acme.p@1.0.0");
        quarantine.ReportTimeout("acme.p@1.0.0");

        Assert.True(quarantine.Release("acme.p@1.0.0"));
        Assert.False(quarantine.IsQuarantined("acme.p@1.0.0"));
        Assert.False(quarantine.ReportTimeout("acme.p@1.0.0"));
    }
}

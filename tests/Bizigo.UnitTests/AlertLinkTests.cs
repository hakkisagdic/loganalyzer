using System.Globalization;
using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Bildirimdeki bağlantı (T22 kabul kriteri).
///
/// <para>
/// "Mesajdaki bağlantı, alarmı üreten aramayı doğru zaman aralığıyla açıyor."
/// Küçük görünen ama alarmın işe yararlığını belirleyen şey bu: yanlış aralık,
/// kullanıcıyı olayın olmadığı bir ekrana götürüp güveni bir kerede bitiriyor.
/// </para>
/// </summary>
public sealed class AlertLinkTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 19, 11, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly AlertRuleEntity Rule = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "deny sağanağı",
        OwnerSubject = "tester",
        OwnerGroups = "network/core",
    };

    [Fact]
    public void Baglanti_tetiklenmenin_kendi_araligini_tasiyor()
    {
        var options = new AlertingOptions { ProductBaseUrl = "https://bizigo.example" };

        var link = AlertLinkBuilder.Build(options, Rule, From, To);

        Assert.NotNull(link);

        var query = new Uri(link).Query;

        Assert.Contains("from=" + Uri.EscapeDataString(From.ToString("O", CultureInfo.InvariantCulture)),
            query, StringComparison.Ordinal);
        Assert.Contains("to=" + Uri.EscapeDataString(To.ToString("O", CultureInfo.InvariantCulture)),
            query, StringComparison.Ordinal);
        Assert.Contains("kural=" + Rule.Id, query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Yerel saat taşınsaydı, aynı bağlantı farklı zaman dilimlerindeki iki
    /// alıcıda farklı aralık açardı.
    /// </summary>
    [Fact]
    public void Baglanti_utc_tasiyor_yerel_saat_degil()
    {
        var options = new AlertingOptions { ProductBaseUrl = "https://bizigo.example" };
        var yerel = new DateTimeOffset(2026, 8, 19, 15, 0, 0, TimeSpan.FromHours(3));

        var link = AlertLinkBuilder.Build(options, Rule, yerel, yerel.AddMinutes(5));

        Assert.NotNull(link);
        Assert.Contains(
            Uri.EscapeDataString(yerel.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            link,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Kok_yapilandirilmamissa_baglanti_uretilmiyor()
    {
        // Tahmini bir adres uydurmak, kullanıcıyı hiçbir yere götürmeyen bir
        // bağlantı vermek olurdu; bağlantısız mesaj bundan dürüst.
        Assert.Null(AlertLinkBuilder.Build(new AlertingOptions(), Rule, From, To));
    }

    [Fact]
    public void Sondaki_egik_cizgi_cift_yol_uretmiyor()
    {
        var options = new AlertingOptions
        {
            ProductBaseUrl = "https://bizigo.example/",
            SearchPath = "/olaylar",
        };

        var link = AlertLinkBuilder.Build(options, Rule, From, To);

        Assert.NotNull(link);
        Assert.StartsWith("https://bizigo.example/olaylar?", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Kaynak_verildiginde_baglantiya_giriyor()
    {
        var options = new AlertingOptions { ProductBaseUrl = "https://bizigo.example" };

        var link = AlertLinkBuilder.Build(options, Rule, From, To, "fw-core-01");

        Assert.NotNull(link);
        Assert.Contains("source_id=fw-core-01", link, StringComparison.Ordinal);
    }
}

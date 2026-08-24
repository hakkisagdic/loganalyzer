using Bizigo.Cli.Seeding;
using Bizigo.Parsing.Samples;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Çapa kuralının bekçileri (T39 · FS S02).
///
/// <para>
/// Kural dört ayrı yerde yazılıydı ve beşincisi simülatör olacaktı. Ayrışmaları
/// <b>sessiz</b>: biri saklama süresine takılır, diğeri takılmaz, ve hiçbir
/// sayaç bunu söylemez — çünkü ClickHouse süresi dolmuş satırı hata vermeden
/// kabul edip siliyor.
/// </para>
/// </summary>
public sealed class SampleClockTests
{
    /// <summary>
    /// Syslog ve HTTP biçimleri saniyenin altını taşımıyor. Kesirli bir an
    /// ekilirse yeniden yazılan satır onu kaybeder ve "ektiğim an ile yazılan an
    /// aynı" doğrulaması her satırda düşer — o doğrulama düşünce onunla birlikte
    /// gerçek kaymalar da görünmez olur.
    /// </summary>
    [Fact]
    public void Capa_tam_saniyeye_iniyor()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 9, 30, 15, 874, TimeSpan.Zero));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 21, 9, 30, 15, TimeSpan.Zero),
            SampleClock.Anchor(time));
    }

    /// <summary>
    /// Çapa bir <b>an</b>, yerel saat değil: ofset taşınırsa iki tüketici aynı
    /// andan farklı metinler üretebilir.
    /// </summary>
    [Fact]
    public void Capa_UTC_donuyor()
    {
        var local = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.FromHours(3));
        var anchor = SampleClock.Truncate(local);

        Assert.Equal(TimeSpan.Zero, anchor.Offset);
        Assert.Equal(local.UtcDateTime, anchor.UtcDateTime);
    }

    /// <summary>
    /// <b>Asıl kapı.</b> Saklama süresinin dışındaki bir an yazıldığında
    /// ClickHouse hata vermiyor — kabul ediyor, sayıyı dönüyor, siliyor.
    /// Sorunun sorulacağı tek yer yazımdan öncesi.
    /// </summary>
    [Fact]
    public void Saklama_suresinin_disindaki_an_yasamiyor()
    {
        var now = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        var retention = TimeSpan.FromDays(90);

        Assert.True(SampleClock.Survives(now - TimeSpan.FromDays(89), now, retention));
        Assert.False(SampleClock.Survives(now - TimeSpan.FromDays(91), now, retention));

        // Sınır dahil: tam 90 gün önce yazılan satır henüz silinmiyor.
        Assert.True(SampleClock.Survives(now - retention, now, retention));
    }

    /// <summary>
    /// Saklama süresi <b>şemadan</b> okunuyor. C# tarafına <c>90</c> yazsaydık
    /// şema değiştiği gün ikisi sessizce ayrışır ve kapı yanlış cevabı verirdi.
    /// </summary>
    [Fact]
    public void Saklama_suresi_goc_dosyasindan_okunuyor()
    {
        Assert.Equal(
            TimeSpan.FromDays(90),
            EventRetention.Read(Path.Combine(RepositoryLayout.Root, "db", "clickhouse")));
    }

    /// <summary>
    /// TTL bulunamazsa "sınırsız saklama" varsayılmıyor, <b>atılıyor</b>:
    /// okuyamama hâli sınırsızlık diye okunsaydı kapı hiç kapanmazdı.
    /// </summary>
    [Fact]
    public void TTL_bulunamazsa_sinirsiz_varsayilmiyor()
    {
        var directory = Directory.CreateTempSubdirectory("bizigo-ttl");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "0001_x.sql"),
                "CREATE TABLE events (ts DateTime64(3)) ENGINE = MergeTree ORDER BY ts;");

            Assert.Throws<InvalidOperationException>(() => EventRetention.Read(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

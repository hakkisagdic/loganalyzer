using Bizigo.Ingest.Discovery;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Keşif kuyruğu ve şablon önbelleği — F1 §9'un "sınırlı kapasite, dolunca
/// düşür" ve "asla ingest'i bloklama" maddeleri.
/// </summary>
public sealed class DiscoveryQueueTests
{
    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);

    [Fact]
    public void Kuyruk_dolunca_dusuruyor_ve_sayiyor()
    {
        var stats = new DiscoveryStats();
        var queue = new DiscoveryQueue(new SidecarOptions { QueueCapacity = 2 }, stats);

        Assert.True(queue.TryEnqueue(new DiscoveryItem("fw", "a", "a")));
        Assert.True(queue.TryEnqueue(new DiscoveryItem("fw", "b", "b")));
        Assert.False(queue.TryEnqueue(new DiscoveryItem("fw", "c", "c")));

        Assert.Equal(2, stats.Enqueued);
        Assert.Equal(1, stats.DroppedQueueFull);
    }

    [Fact]
    public void Sinirsiz_kuyruk_kurulamiyor()
    {
        // Sınırsız kuyruk ingest'i bloklamaz, belleği tüketir — daha kötüsü.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DiscoveryQueue(new SidecarOptions { QueueCapacity = 0 }, new DiscoveryStats()));
    }

    [Fact]
    public void Kuyruk_dolu_olsa_bile_etiketleme_bloklamiyor()
    {
        // Kabul kriteri: "kuyruk dolduğunda istekler düşüyor, ingest bloklanmıyor".
        var stats = new DiscoveryStats();
        var options = new SidecarOptions { QueueCapacity = 1, SampleRate = 0 };
        var queue = new DiscoveryQueue(options, stats);
        var annotator = new DiscoveryAnnotator(
            options, Masks, new TemplateCache(1024), queue, stats);

        for (var index = 0; index < 5_000; index++)
        {
            var templateId = annotator.Annotate(
                "firewall", $"deny tcp 10.0.{index / 256}.{index % 256} -> 10.9.9.9", parseFailed: true);

            Assert.Equal(string.Empty, templateId);
        }

        Assert.Equal(1, stats.Enqueued);
        Assert.Equal(4_999, stats.DroppedQueueFull);
    }

    [Fact]
    public void Onbellekteki_imza_sidecar_a_gitmeden_etiketleniyor()
    {
        var stats = new DiscoveryStats();
        var options = new SidecarOptions { QueueCapacity = 8, SampleRate = 0 };
        var queue = new DiscoveryQueue(options, stats);
        var cache = new TemplateCache(1024);
        var annotator = new DiscoveryAnnotator(options, Masks, cache, queue, stats);

        const string First = "Failed password for admin from 10.1.2.3 port 51234 ssh2";
        const string Second = "Failed password for admin from 192.168.0.7 port 22 ssh2";

        // İlk satır bilinmiyor: etiketsiz kalıyor, kuyruğa giriyor.
        Assert.Equal(string.Empty, annotator.Annotate("linux", First, parseFailed: true));
        Assert.Equal(1, stats.Enqueued);

        // Sidecar'ın cevabı geldi.
        cache.Set(Masks.Signature(First), "linux:7");

        // Aynı imzalı ikinci satır artık ücretsiz.
        Assert.Equal("linux:7", annotator.Annotate("linux", Second, parseFailed: true));
        Assert.Equal(1, stats.Enqueued);
        Assert.Equal(1, stats.CacheHits);
    }

    [Fact]
    public void Basarili_olaylar_ornekleme_kapaliyken_kuyruga_girmiyor()
    {
        var stats = new DiscoveryStats();
        var options = new SidecarOptions { QueueCapacity = 64, SampleRate = 0 };
        var queue = new DiscoveryQueue(options, stats);
        var annotator = new DiscoveryAnnotator(
            options, Masks, new TemplateCache(64), queue, stats);

        for (var index = 0; index < 100; index++)
        {
            annotator.Annotate("linux", $"accepted connection {index}", parseFailed: false);
        }

        Assert.Equal(0, stats.Enqueued);
    }

    [Fact]
    public void Sidecar_kapaliyken_etiketleme_hicbir_sey_yapmiyor()
    {
        var stats = new DiscoveryStats();
        var options = new SidecarOptions { Enabled = false, QueueCapacity = 8 };
        var queue = new DiscoveryQueue(options, stats);
        var annotator = new DiscoveryAnnotator(
            options, Masks, new TemplateCache(8), queue, stats);

        Assert.Equal(string.Empty, annotator.Annotate("linux", "deny 10.0.0.1", parseFailed: true));
        Assert.Equal(0, stats.Enqueued);
        Assert.Equal(0, stats.CacheMisses);
    }

    [Fact]
    public void Sablon_onbellegi_sinirini_asmiyor()
    {
        // Kabul kriteri: bellek sınırlı kalıyor.
        var cache = new TemplateCache(100);

        for (var index = 0; index < 10_000; index++)
        {
            cache.Set($"imza-{index}", $"t:{index}");
        }

        Assert.True(cache.Count <= 100, $"Önbellek {cache.Count} kayda çıktı.");
        // En yeni kayıt hâlâ orada: tahliye edilen en eskiler.
        Assert.True(cache.TryGet("imza-9999", out var last));
        Assert.Equal("t:9999", last);
    }

    [Fact]
    public void Sinirsiz_onbellek_kurulamiyor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateCache(0));
    }
}

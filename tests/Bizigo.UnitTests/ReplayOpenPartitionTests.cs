using Bizigo.Replay;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

/// <summary>
/// F1'in devrettiği ölçümlerden biri: <b>"replay sırasında canlı ingest
/// bozulmuyor"</b> (T27).
///
/// <para>
/// F1 bunu "<c>REPLACE PARTITION</c> atomik olduğu için beklenen doğru davranış"
/// diye bırakmıştı, ölçmeden. Ölçünce iddia <b>yanlış</b> çıktı ve sebebi
/// atomikliğin ne söylediğiyle ilgili: değiştirme işlemi bölünmez, ama motor
/// önce mevcut satırları okuyup gölge tabloyu kuruyor, sonra değiştiriyor. O iki
/// adım arasında canlı ingest'in aynı bölüme yazdığı her satır gölgede yok — ve
/// değiştirme onu <b>sessizce siliyor</b>. Atomiklik "yarım bölüm görünmez"
/// diyor, "anlık görüntüden sonra gelen korunur" demiyor.
/// </para>
///
/// <para>
/// Kapatma biçimi: açık bölüm (bugünün bölümü) varsayılan olarak replay'in
/// dışında ve atlandığı <b>rapora yazılıyor</b>. Sessiz veri kaybı, görünür bir
/// karara çevrildi.
/// </para>
///
/// <para>
/// Bu testler konteyner istemiyor: karar saf bir fonksiyonda ve saat dışarıdan
/// veriliyor. F1'in en pahalı dersi, tam tersini yapmanın bedeliydi.
/// </para>
/// </summary>
public sealed class ReplayOpenPartitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 30, 0, TimeSpan.Zero);

    private static ReplayPlan Plan(bool allowOpen = false) => new()
    {
        From = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        To = Now,
        AllowOpenPartition = allowOpen,
    };

    private static PartitionInfo[] Partitions() =>
    [
        new("20260817", 1_000),
        new("20260818", 2_000),
        new("20260819", 1_500),
        // Bugünün bölümü: hâlâ yazılıyor.
        new("20260820", 300),
    ];

    [Fact]
    public void Bugunun_bolumu_varsayilan_olarak_atlaniyor()
    {
        var (replayable, skipped) = ReplayEngine.SplitOpen(Plan(), Partitions(), Now);

        Assert.Equal(["20260817", "20260818", "20260819"], replayable.Select(p => p.Partition));
        Assert.Equal(["20260820"], skipped);
    }

    [Fact]
    public void Atlama_sessiz_degil_raporda()
    {
        // Sessizce kısalmak, manifest'in (K25 koruma #4) kapattığı hatanın
        // aynısı: replay "7 gün yerine 5 gün" döner ve kimse fark etmez.
        var (_, skipped) = ReplayEngine.SplitOpen(Plan(), Partitions(), Now);

        Assert.NotEmpty(skipped);
    }

    [Fact]
    public void Gecmis_bolumler_dokunulmadan_geciyor()
    {
        // Geçmiş bölüme yeni yazma olmuyor; tehlike yok ve replay'in olağan
        // kullanımı zaten bu.
        var past = Partitions().Where(p => p.Partition != "20260820").ToArray();

        var (replayable, skipped) = ReplayEngine.SplitOpen(Plan(), past, Now);

        Assert.Equal(3, replayable.Count);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Bayrak_acikken_acik_bolum_de_dahil()
    {
        // İngest'i durdurduğunu bilen operatör bugünü de kapsayabilmeli — ama
        // bunu AÇIKÇA söyleyerek.
        var (replayable, skipped) = ReplayEngine.SplitOpen(Plan(allowOpen: true), Partitions(), Now);

        Assert.Equal(4, replayable.Count);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Gelecek_tarihli_bolum_de_acik_sayiliyor()
    {
        // Cihaz saati ileri kaymış bir kaynak gelecekteki bir bölüme yazabiliyor
        // ve o bölüm de hâlâ yazılmaya açık. `>=` karşılaştırması bunu kapsıyor.
        PartitionInfo[] partitions = [new("20260821", 10)];

        var (replayable, skipped) = ReplayEngine.SplitOpen(Plan(), partitions, Now);

        Assert.Empty(replayable);
        Assert.Equal(["20260821"], skipped);
    }

    [Fact]
    public void Gun_donumunde_sinir_kayiyor()
    {
        // Saat gece yarısını geçtiğinde açık bölüm de değişiyor: dünün bölümü
        // artık replay edilebilir. Sabit bir "bugün" varsayımı bunu kaçırırdı.
        var afterMidnight = new DateTimeOffset(2026, 8, 21, 0, 5, 0, TimeSpan.Zero);

        var (replayable, skipped) = ReplayEngine.SplitOpen(Plan(), Partitions(), afterMidnight);

        Assert.Equal(4, replayable.Count);
        Assert.Empty(skipped);
    }
}

using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// Kanıt sıralaması (T36) — <b>bu ürünün en kolay sessizce yanlış olan
/// kararı</b>.
///
/// <para>
/// Sağlayıcıların ağırlıkları farklı birimlerde: kaynak sayısı, z-score, kat,
/// 1/(1+saniye). Doğrudan karşılaştırıldıklarında sıralamayı yargı değil
/// <b>ölçek</b> belirler — z-score her zaman kazanır, yayılma hiçbir zaman üste
/// çıkamaz. Ve bu hiçbir yerde hata vermez: rapor üretilir, bulgular sıralanır,
/// sıra yanlıştır.
/// </para>
/// </summary>
public sealed class EvidenceRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private static EvidenceSlice Slice(string providerId, params double[] weights) => new()
    {
        ProviderId = providerId,
        Kind = EvidenceKind.Log,
        Status = EvidenceStatus.Gathered,
        Items =
        [
            .. weights.Select((w, i) => new EvidenceItem(
                $"{providerId}:{i}",
                providerId,
                EvidenceKind.Log,
                Now.AddSeconds(i),
                w,
                $"{providerId} #{i}",
                new Dictionary<string, string>(StringComparer.Ordinal)))
        ],
    };

    /// <summary>
    /// <b>Kararın kendisi:</b> sınıf içi hiçbir büyüklük bir üst sınıfı geçemez.
    ///
    /// <para>
    /// z=40'lık bir hacim sapması, tek kaynakta görülen bir ilk-görülen imzayı
    /// geçmiyor. İstenen tam olarak bu: "ne patladı" ile "yeni ne oldu" aynı
    /// soruya cevap vermiyor ve hangisinin kök nedene yakın olduğu bir yargı,
    /// ölçek kazası değil.
    /// </para>
    /// </summary>
    [Fact]
    public void Buyuk_z_score_ust_sinifi_gecemiyor()
    {
        var ranked = EvidenceRanking.RankAll([
            Slice("logs.volume", 40.0),
            Slice("logs.first-seen", 1.0),
        ]);

        Assert.Equal("logs.first-seen", ranked[0].Item.ProviderId);
        Assert.Equal("logs.volume", ranked[1].Item.ProviderId);
    }

    /// <summary>
    /// Ham ağırlıkla sıralasaydık ne olurdu — <b>bekçinin neyi yakaladığının
    /// doğrudan ölçümü</b>. Ham sıralama tersini veriyor.
    /// </summary>
    [Fact]
    public void Ham_agirlik_sirasi_yanlis_cevabi_verirdi()
    {
        var slices = new[] { Slice("logs.volume", 40.0), Slice("logs.first-seen", 1.0) };

        var byRawWeight = slices
            .SelectMany(s => s.Items)
            .OrderByDescending(i => i.Weight)
            .First();

        // Ham ağırlık hacmi öne koyuyor; sınıflı sıralama ilk-görüleni.
        Assert.Equal("logs.volume", byRawWeight.ProviderId);
        Assert.Equal("logs.first-seen", EvidenceRanking.RankAll(slices)[0].Item.ProviderId);
    }

    /// <summary>
    /// Sınıf <b>içinde</b> büyüklük korunuyor: sıra (rank) yerine oran
    /// seçilmesinin sebebi, "z=20 ile z=3,1" farkının sağlayıcının söylemek
    /// istediği şey olması.
    /// </summary>
    [Fact]
    public void Sinif_icinde_buyukluk_korunuyor()
    {
        var ranked = EvidenceRanking.RankAll([Slice("logs.volume", 3.1, 20.0, 8.0)]);

        Assert.Equal(["logs.volume:1", "logs.volume:2", "logs.volume:0"], ranked.Select(r => r.Item.Id));
        Assert.Equal(1.0, ranked[0].RelativeWeight, 6);
        Assert.Equal(8.0 / 20.0, ranked[1].RelativeWeight, 6);
    }

    /// <summary>
    /// Bütün ağırlıklar sıfırken sağlayıcının verdiği <b>sıra korunuyor</b>.
    /// Yayılma sırası tam olarak bunu gerektiriyor: satırların sırası zaten
    /// sinyalin kendisi ve yeniden sıralamak sinyali yok ederdi.
    /// </summary>
    [Fact]
    public void Agirliksiz_dilimde_saglayicinin_sirasi_korunuyor()
    {
        var ranked = EvidenceRanking.RankAll([Slice("logs.propagation", 0, 0, 0)]);

        Assert.Equal(
            ["logs.propagation:0", "logs.propagation:1", "logs.propagation:2"],
            ranked.Select(r => r.Item.Id));
    }

    /// <summary>
    /// Eşit skorlu satırların sırası <b>kararlı</b>. Kanıt paketi aynı girdiyle
    /// aynı çıktıyı vermek zorunda; sıralamanın makineye göre değişmesi paketi
    /// deterministik olmaktan çıkarırdı.
    /// </summary>
    [Fact]
    public void Esit_skorlarda_sira_kararli()
    {
        var slices = new[] { Slice("logs.volume", 5.0, 5.0, 5.0), Slice("logs.silence", 2.0, 2.0) };

        var first = EvidenceRanking.RankAll(slices).Select(r => r.Item.Id).ToArray();
        var second = EvidenceRanking.RankAll(slices.Reverse()).Select(r => r.Item.Id).ToArray();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// <b>Bekçi:</b> kayıtlı her sağlayıcının sıralaması yazılı.
    ///
    /// <para>
    /// Listede olmayan bir sağlayıcı <see cref="EvidenceRanking.UnrankedClass"/>'a
    /// düşer ve kanıtı raporun en dibinde, sessizce görünür. F5'te trace
    /// sağlayıcısı geldiğinde onu tabloya yazmayı unutmak, kanıtını "en
    /// önemsiz" ilan etmek olurdu ve hiçbir şey kırmızı yanmazdı — bu test
    /// tam o günü bugünden koşuyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kayitli_her_saglayicinin_sirasi_yazili()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScopedQuery>(new RecordingScopedQuery());
        services.AddBizigoEvidence();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var unranked = scope.ServiceProvider.GetServices<IEvidenceProvider>()
            .Select(p => p.Id)
            .Where(id => EvidenceRanking.ClassRank(id) == EvidenceRanking.UnrankedClass)
            .ToArray();

        Assert.True(
            unranked.Length == 0,
            "Sıralaması yazılı olmayan sağlayıcı var; kanıtı raporun dibinde sessizce görünür: "
            + string.Join(", ", unranked));
    }

    /// <summary>
    /// Tanınmayan bir sağlayıcı <b>düşürülmüyor</b>, en dibe konuyor. Rapordan
    /// silmek, bilinmeyen bir kanıt türünü sessizce yok saymak olurdu.
    /// </summary>
    [Fact]
    public void Taninmayan_saglayici_dusurulmuyor()
    {
        var ranked = EvidenceRanking.RankAll([
            Slice("f5.topology", 99.0),
            Slice("logs.window", 1.0),
        ]);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("logs.window", ranked[0].Item.ProviderId);
        Assert.Equal("f5.topology", ranked[^1].Item.ProviderId);
    }

    /// <summary>
    /// Sıralama <b>pakete yazılmıyor</b>: skor türetilmiş bir değer.
    ///
    /// <para>
    /// Sıralama yargısı zamanla değişebilir, kanıt değişmez. Skoru pakete
    /// yazmak, altı ay sonra sıralamayı düzelttiğimizde geçmiş paketleri eski
    /// yargıyla dondururdu — oysa paketin saklanma sebebi tam tersi.
    /// </para>
    /// </summary>
    [Fact]
    public void Skor_pakete_yazilmiyor()
    {
        var json = BundleSerializer.Serialize(EvidenceBundleTests.Bundle(
            Slice("logs.first-seen", 3.0)));

        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class_rank", json, StringComparison.OrdinalIgnoreCase);
    }
}

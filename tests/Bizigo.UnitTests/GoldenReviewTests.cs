using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Altın kümenin veri katmanı (T38).
///
/// <para>
/// Buradaki testlerin ortak iddiası <b>aritmetik ve zorunluluk</b>: bir
/// incelemenin paketsiz yazılamaması, "bilmiyorum"un doğruluk oranına
/// karışmaması, ve boş kümenin sıfır göstermesi. Üçü de F4'ün ölçümünün
/// dayanağı — yanlış olsalar hata vermezler, yalnızca yanlış bir sayı üretirler.
/// </para>
/// </summary>
public sealed class GoldenReviewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Now);

    private GoldenReviewStore Store() => new(_factory, _time);

    private static AccessScope Scope(params string[] groups) =>
        AccessScope.ForGroups("analyst.core", groups);

    private async Task<Guid> SeedBundleAsync()
    {
        var id = Guid.CreateVersion7(Now);

        await using var db = _factory.CreateDbContext();
        db.EvidenceBundles.Add(new EvidenceBundleEntity
        {
            Id = id,
            GatheredAt = Now,
            SchemaVersion = 1,
            ContentHash = "hash",
            WindowFrom = Now.AddHours(-1),
            WindowTo = Now,
            BaselineFrom = Now.AddDays(-8),
            BaselineTo = Now.AddHours(-1),
            Payload = "{}",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private async Task WriteAsync(Guid bundleId, ReviewVerdict verdict, string group = "network-core")
    {
        await Store().AddAsync(
            new ReviewInput(bundleId, null, verdict, ContradictingEvidenceVerdict.NotPresent, string.Empty),
            Scope(group),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// İnceleme bir kanıt paketine bağlanmak <b>zorunda</b>.
    ///
    /// <para>
    /// Paketsiz bir kayıt F4'ün karşılaştırmasına giremez ama altın kümede
    /// sayılır — yani küme büyümüş görünür, ölçülebilirliği artmaz. Sessiz
    /// yanlışın tam şekli.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Paketsiz_inceleme_yazilamiyor()
    {
        var error = await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            Store().AddAsync(
                new ReviewInput(
                    Guid.CreateVersion7(Now),
                    null,
                    ReviewVerdict.Correct,
                    ContradictingEvidenceVerdict.NotPresent,
                    "paket yok"),
                Scope("network-core"),
                TestContext.Current.CancellationToken));

        Assert.Contains("paket", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(db.GoldenReviews);
    }

    /// <summary>
    /// <b>"Bilmiyorum" doğruluk oranının paydasına girmiyor.</b>
    ///
    /// <para>
    /// Girseydi zorunlu soruya "bilmiyorum" diyen her kullanıcı oranı aşağı
    /// çekerdi ve dürüst cevap, rapora kötü not vermekle aynı şeye dönerdi.
    /// Oranın ölçtüğü şey <b>karar verilmiş</b> incelemeler.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bilmiyorum_dogruluk_oranina_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteAsync(bundle, ReviewVerdict.Correct);
        await WriteAsync(bundle, ReviewVerdict.Wrong);
        await WriteAsync(bundle, ReviewVerdict.Unknown);
        await WriteAsync(bundle, ReviewVerdict.Unknown);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(4, quality.Total);
        Assert.Equal(2, quality.Unknown);

        // Payda 4 değil 2: bir doğru, bir yanlış.
        Assert.Equal(2, quality.Decided);
        Assert.Equal(0.5, quality.Accuracy);

        // "Bilmiyorum" oranı ayrı bir gösterge ve paydası TOPLAM.
        Assert.Equal(0.5, quality.UnknownRatio);
    }

    /// <summary>
    /// Hepsi "bilmiyorum" ise doğruluk oranı <b>yok</b>, sıfır değil.
    ///
    /// <para>
    /// Sıfır dönseydi ekran "%0 doğru" yazardı — oysa doğru cümle "henüz karar
    /// verilmiş inceleme yok". İkisi aynı sayıyla gösterilirse ölçülmemiş bir
    /// şey kötü ölçülmüş gibi görünür.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Karar_verilmemisse_dogruluk_orani_yok_sifir_degil()
    {
        var bundle = await SeedBundleAsync();

        await WriteAsync(bundle, ReviewVerdict.Unknown);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(1, quality.Total);
        Assert.Equal(0, quality.Decided);
        Assert.Null(quality.Accuracy);
        Assert.Equal(1.0, quality.UnknownRatio);
    }

    /// <summary>
    /// Boş kümede de <b>bir sonuç dönüyor</b> — sıfırlarla.
    ///
    /// <para>
    /// Boş dönmek ekranın göstergeyi gizlemesine izin verirdi ve gizlenen bir
    /// sıfır, "henüz ölçülmedi" ile "ölçüldü, sıfır" arasındaki farkı siler.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kume_bosken_sayi_gorunuyor()
    {
        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(0, quality.Total);
        Assert.Equal(0, quality.Correct);
        Assert.Equal(0, quality.Unknown);

        // Sıfır kayıtta oran diye bir şey yok; "%0" demek yanlış olurdu.
        Assert.Null(quality.Accuracy);
        Assert.Null(quality.UnknownRatio);
    }

    /// <summary>
    /// Başka grubun incelemesi göstergeye <b>karışmıyor</b>.
    ///
    /// <para>
    /// Birim seviyesinde sınanan şey filtrenin kendisi; SQL'in doğruluğu
    /// entegrasyon testinin işi (`ScopeNegativeTests` genişletmesi).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Baska_grubun_incelemesi_gostergeye_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteAsync(bundle, ReviewVerdict.Correct, "network-core");
        await WriteAsync(bundle, ReviewVerdict.Wrong, "network-edge");

        var core = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(1, core.Total);
        Assert.Equal(1, core.Correct);
        Assert.Equal(1.0, core.Accuracy);
    }

    /// <summary>
    /// Boş kapsam hiçbir şey görmüyor — "filtre yok"a düşmüyor.
    /// </summary>
    [Fact]
    public async Task Bos_kapsam_hicbir_inceleme_gormuyor()
    {
        var bundle = await SeedBundleAsync();
        await WriteAsync(bundle, ReviewVerdict.Correct);

        var quality = await Store().QualityAsync(AccessScope.Denied, TestContext.Current.CancellationToken);

        Assert.Equal(0, quality.Total);
    }

    /// <summary>
    /// Kapsamı çok gruplu bir kullanıcı, kaydın hangi gruba yazılacağını
    /// <b>söylemek zorunda</b>.
    ///
    /// <para>
    /// Sistemin onun yerine seçmesi, incelemeyi yanlış ekibin göstergesine
    /// yazmanın sessiz yoluydu — ve yanlış ekibin doğruluk oranı, kimsenin
    /// bakmadığı bir sayı olarak kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cok_gruplu_kapsamda_hedef_grup_belirtilmeli()
    {
        var bundle = await SeedBundleAsync();

        var error = await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            Store().AddAsync(
                new ReviewInput(bundle, null, ReviewVerdict.Correct, ContradictingEvidenceVerdict.NotPresent, ""),
                Scope("network-core", "network-edge"),
                TestContext.Current.CancellationToken));

        Assert.Contains("grup", error.Message, StringComparison.OrdinalIgnoreCase);

        // Belirtilince yazılıyor — ve kapsam içinde olduğu doğrulanıyor.
        var written = await Store().AddAsync(
            new ReviewInput(
                bundle, null, ReviewVerdict.Correct, ContradictingEvidenceVerdict.NotPresent, "", "network-edge"),
            Scope("network-core", "network-edge"),
            TestContext.Current.CancellationToken);

        Assert.Equal("network-edge", written.OwnerGroup);
    }

    /// <summary>Kapsam dışı bir grup adı verilirse reddediliyor.</summary>
    [Fact]
    public async Task Kapsam_disi_grup_adi_reddediliyor()
    {
        var bundle = await SeedBundleAsync();

        await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            Store().AddAsync(
                new ReviewInput(
                    bundle, null, ReviewVerdict.Correct, ContradictingEvidenceVerdict.NotPresent, "", "network-edge"),
                Scope("network-core"),
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Kayıt şema sürümünü taşıyor.
    ///
    /// <para>
    /// F4 alanları göç ile eklendiğinde eski satırların hangi şemayla yazıldığı
    /// bilinmek zorunda: kolonun <i>var olması</i> ile <i>doldurulmuş olması</i>
    /// ayrı şeyler ve sürüm olmadan ikisi ayırt edilemez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Inceleme_semasi_surumu_tasiniyor()
    {
        var bundle = await SeedBundleAsync();
        await WriteAsync(bundle, ReviewVerdict.Correct);

        await using var db = _factory.CreateDbContext();
        var row = Assert.Single(db.GoldenReviews);

        Assert.Equal(GoldenReviewEntity.CurrentSchemaVersion, row.SchemaVersion);
    }

    /// <summary>
    /// Çelişen kanıt kararı <b>bugünden itibaren</b> her kayıtta.
    ///
    /// <para>
    /// Alan sonradan eklenseydi geçmiş kayıtlar onu taşımaz ve altın kümenin en
    /// eski yarısı bu boyutta kör kalırdı (RCA riski #5, "çelişen kanıt
    /// tiyatrosu").
    /// </para>
    /// </summary>
    [Fact]
    public async Task Celisen_kanit_karari_kaydediliyor()
    {
        var bundle = await SeedBundleAsync();

        var written = await Store().AddAsync(
            new ReviewInput(
                bundle,
                null,
                ReviewVerdict.Correct,
                ContradictingEvidenceVerdict.Trivial,
                "çelişen kanıt bölümü doldurulmuş ama önemsiz"),
            Scope("network-core"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ContradictingEvidenceVerdict.Trivial, written.ContradictingEvidence);
    }

    // ---------------------------------------------------------------------
    // Çelişen kanıt tiyatrosu (T47) — RCA risk #5'in ölçüsü
    // ---------------------------------------------------------------------

    private async Task WriteContradictingAsync(
        Guid bundleId,
        ContradictingEvidenceVerdict contradicting,
        string group = "network-core")
    {
        await Store().AddAsync(
            new ReviewInput(bundleId, null, ReviewVerdict.Correct, contradicting, string.Empty),
            Scope(group),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <b>Tiyatro oranının paydası "değerlendirilmiş" bölümler, toplam inceleme
    /// değil.</b>
    ///
    /// <para>
    /// Payda toplam olsaydı, çelişen kanıt bölümü <i>hiç üretmeyen</i> bir model
    /// en iyi skoru alırdı: bütün kayıtlar <c>NotPresent</c>, tiyatro oranı
    /// sıfır. Ölçünün amacı tam tersi — alanı doldurmak için önemsiz bir şey
    /// uyduran modeli yakalamak.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bolumu_olmayan_rapor_tiyatro_paydasina_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.NotPresent);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.NotPresent);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Sound);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Trivial);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(4, quality.Total);
        Assert.Equal(2, quality.ContradictingEvaluated);
        Assert.Equal(0.5, quality.ContradictingTrivialRatio);
    }

    /// <summary>
    /// <b>Değerlendirilemeyen de paydaya girmiyor</b> — <c>Unknown</c>'ın
    /// doğruluk oranındaki davranışıyla aynı, ve aynı sebeple.
    /// </summary>
    [Fact]
    public async Task Degerlendirilemeyen_celisen_kanit_paydaya_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Unknown);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Trivial);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(1, quality.ContradictingUnknown);
        Assert.Equal(1, quality.ContradictingEvaluated);
        Assert.Equal(1.0, quality.ContradictingTrivialRatio);
    }

    /// <summary>
    /// <b>Hiç değerlendirilmemişse oran <c>null</c>, sıfır değil.</b>
    ///
    /// <para>
    /// Bu ayrım burada <see cref="GoldenSetQuality.Accuracy"/>'dekinden daha
    /// keskin, çünkü <b>iyi olan uç sıfır</b>: "%0 tiyatro" en iyi sonuç,
    /// "değerlendirilmedi" ise hiçbir sonuç. İkisi tek sayıya inerse ekran,
    /// <b>ölçülmemiş bir boyutu mükemmel diye gösterir</b> — koordinatörün
    /// işaret ettiği tuzağın tam olarak bu ölçüdeki hâli.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hic_degerlendirilmemisse_tiyatro_orani_yok_sifir_degil()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.NotPresent);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Unknown);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(2, quality.Total);
        Assert.Equal(0, quality.ContradictingEvaluated);
        Assert.Null(quality.ContradictingTrivialRatio);
    }

    /// <summary>
    /// <b>Gerçek bir sıfır gizlenmiyor.</b> Yukarıdakinin ters yönü: hepsi
    /// <c>Sound</c> ise oran <c>0</c> ve bu <b>ölçülmüş</b> bir sonuç.
    /// </summary>
    [Fact]
    public async Task Hepsi_yerindeyse_oran_sifir_ve_null_degil()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Sound);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Sound);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(2, quality.ContradictingEvaluated);
        Assert.Equal(0.0, quality.ContradictingTrivialRatio);
    }

    /// <summary>
    /// Çelişen kanıt sayıları da <b>kapsam filtresinden</b> geçiyor.
    ///
    /// <para>
    /// Doğruluk oranı için ayrı bir test var; bu boyut sonradan eklendiği için
    /// aynı kapıdan geçtiği <b>ayrıca</b> ölçülüyor. Yeni bir toplama, var olan
    /// kapsam filtresinin dışına düşerse sızıntı hiçbir yerde hata vermez —
    /// yalnızca başka grubun sayısı bizim göstergemize karışır.
    /// </para>
    ///
    /// <para>
    /// <b>Dış grupta her iki cinsten de satır var ve bu şart.</b> İlk yazımda
    /// dışarıya yalnızca <c>Sound</c> konmuştu; <c>Trivial</c> sayımının kapsam
    /// filtresi kaldırıldığında test <b>yeşil kaldı</b> — çünkü kapsam dışında
    /// sayılabilecek tek bir <c>Trivial</c> satır yoktu. Bekçi doğru şeyi
    /// iddia ediyor ama <b>yanlış sebeple</b> geçiyordu, ve bunu ancak §6'nın
    /// mutasyon adımı gösterdi. Her sayaç için dışarıda en az bir satır
    /// olmalı, yoksa o sayacın sızıntısı ölçülemez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Baska_grubun_celisen_kaniti_gostergeye_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Trivial, "network-core");

        // Dışarıda ÜÇ sayacın da karşılığı var: biri sızarsa sayı oynar.
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Trivial, "app-team");
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Sound, "app-team");
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Unknown, "app-team");

        var core = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(1, core.Total);
        Assert.Equal(1, core.ContradictingTrivial);
        Assert.Equal(0, core.ContradictingSound);
        Assert.Equal(0, core.ContradictingUnknown);
        Assert.Equal(1, core.ContradictingEvaluated);
        Assert.Equal(1.0, core.ContradictingTrivialRatio);
    }

    // ---------------------------------------------------------------------
    // accuracy@k ve "kimse söylemedi" (T47)
    // ---------------------------------------------------------------------

    private async Task WriteRankAsync(Guid bundleId, int? rank, string group = "network-core")
    {
        await Store().AddAsync(
            new ReviewInput(
                bundleId, null, ReviewVerdict.Correct,
                ContradictingEvidenceVerdict.NotPresent, string.Empty,
                CorrectFindingRank: rank,
                CorrectFindingRankAsked: true),
            Scope(group),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>accuracy@1</c> ve <c>accuracy@3</c> <b>aynı alandan</b> çıkıyor.
    /// </summary>
    [Fact]
    public async Task Accuracy_at_k_ayni_alandan_cikiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteRankAsync(bundle, 1);
        await WriteRankAsync(bundle, 3);
        await WriteRankAsync(bundle, 7);
        await WriteRankAsync(bundle, null); // hiçbiri doğru değildi — ölçüm

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(4, quality.RankAsked);
        Assert.Equal(0.25, quality.AccuracyAtOne);
        Assert.Equal(0.5, quality.AccuracyAtThree);
    }

    /// <summary>
    /// <b>Rank sorulmamış inceleme paydaya girmiyor</b> — ve ayrım kaydın
    /// <b>şema sürümünden</b> geliyor, alanın <c>null</c> olmasından değil.
    ///
    /// <para>
    /// İkisi tek <c>null</c>'a inseydi, sorunun hiç sorulmadığı bir kümede
    /// <c>accuracy@1</c> <b>%0</b> çıkardı — yani ölçülmemiş bir şey
    /// "ölçüldü, berbat" diye okunurdu. T38 <c>SchemaVersion</c>'ı tam olarak
    /// bu gün için taşıyordu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rank_sorulmamis_inceleme_paydaya_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteRankAsync(bundle, 1);

        // Soru sorulmadan yazılmış eski bir kayıt.
        await using (var db = _factory.CreateDbContext())
        {
            db.GoldenReviews.Add(new GoldenReviewEntity
            {
                BundleId = bundle,
                OwnerGroup = "network-core",
                Verdict = ReviewVerdict.Correct,
                ContradictingEvidence = ContradictingEvidenceVerdict.NotPresent,
                ReviewerSubject = "analyst.core",
                ReviewedAt = Now,
                SchemaVersion = 1,
                CorrectFindingRank = null,
                CorrectFindingRankAsked = false,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(2, quality.Total);
        Assert.Equal(1, quality.RankAsked);
        Assert.Equal(1.0, quality.AccuracyAtOne);
    }

    /// <summary>
    /// <b>Bugünkü şemayla yazılmış ama sorusu sorulmamış</b> inceleme paydaya
    /// girmiyor — ve bu, ayrımın şema sürümüne bağlanamamasının sebebi.
    ///
    /// <para>
    /// Gerçek hâli: alarm kapatma ekranı bulguları <b>göstermiyor</b>,
    /// dolayısıyla soruyu soramıyor, ama kaydı bugünkü sürümle yazıyor. Payda
    /// sürüme bağlansaydı bu kayıt sorulmamış bir soruyla paydaya girer ve
    /// <c>accuracy@1</c>'i sessizce aşağı çekerdi.
    /// </para>
    ///
    /// <para>
    /// Bu testin varlık sebebi ölçüldü: <c>SchemaVersion</c> tabanlı bir payda,
    /// yalnızca eski sürüm satırıyla sınandığında <b>yeşil kalıyor</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bugunku_semayla_yazilmis_ama_sorulmamis_kayit_paydaya_girmiyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteRankAsync(bundle, 1);

        // Kapatma yolu: bugünkü sürüm, soru sorulmadı.
        await Store().AddAsync(
            new ReviewInput(
                bundle, null, ReviewVerdict.Correct,
                ContradictingEvidenceVerdict.NotPresent, string.Empty,
                CorrectFindingRank: null,
                CorrectFindingRankAsked: false),
            Scope("network-core"),
            TestContext.Current.CancellationToken);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(2, quality.Total);
        Assert.Equal(1, quality.RankAsked);
        Assert.Equal(1.0, quality.AccuracyAtOne);
    }

    /// <summary>
    /// Sıra verilmiş ama soru sorulmamış olarak işaretlenmiş: <b>tutarsız</b>.
    ///
    /// <para>
    /// Sessizce kabul edilseydi kayıt paya girer, paydaya girmezdi — %100'ü
    /// aşabilen bir oran.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sira_verilmis_ama_sorulmamis_reddediliyor()
    {
        var bundle = await SeedBundleAsync();

        var error = await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            Store().AddAsync(
                new ReviewInput(
                    bundle, null, ReviewVerdict.Correct,
                    ContradictingEvidenceVerdict.NotPresent, string.Empty,
                    CorrectFindingRank: 1,
                    CorrectFindingRankAsked: false),
                Scope("network-core"),
                TestContext.Current.CancellationToken));

        Assert.Contains("sorulmamış", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Hiç sorulmamışsa oran <c>null</c>, sıfır <b>değil</b>.</summary>
    [Fact]
    public async Task Hic_sorulmamissa_accuracy_yok_sifir_degil()
    {
        var bundle = await SeedBundleAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.GoldenReviews.Add(new GoldenReviewEntity
            {
                BundleId = bundle,
                OwnerGroup = "network-core",
                Verdict = ReviewVerdict.Correct,
                ContradictingEvidence = ContradictingEvidenceVerdict.NotPresent,
                ReviewerSubject = "analyst.core",
                ReviewedAt = Now,
                SchemaVersion = 1,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(0, quality.RankAsked);
        Assert.Null(quality.AccuracyAtOne);
        Assert.Null(quality.AccuracyAtThree);
    }

    /// <summary>
    /// Sıfır ve negatif sıra <b>reddediliyor</b>.
    ///
    /// <para>
    /// Sessizce kabul edilseydi kayıt paydaya girer, hiçbir <c>accuracy@k</c>
    /// kovasına düşmezdi: oranı aşağı çeken, sebebi görünmeyen bir satır.
    /// "Hiçbiri doğru değildi"nin ifadesi <c>null</c>, sıfır değil.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Gecersiz_sira_reddediliyor(int rank)
    {
        var bundle = await SeedBundleAsync();

        var error = await Assert.ThrowsAsync<ReviewRejectedException>(() =>
            WriteRankAsync(bundle, rank));

        Assert.Contains("1 tabanlı", error.Message, StringComparison.Ordinal);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(db.GoldenReviews);
    }

    /// <summary>
    /// <b>"Kimse söylemedi" ile "bölüm yoktu" ayrı sayılıyor.</b>
    ///
    /// <para>
    /// <c>Unspecified</c> varsayılan değer (<c>0</c>) olduğu için alanı hiç
    /// doldurmayan bir çağıran artık bir karar <b>uydurmuyor</b>. Eskiden
    /// varsayılan <c>NotPresent</c>'tı ve o, tiyatro oranının paydasını
    /// etkileyen bir cümleydi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kimse_soylemedi_bolum_yoktu_ile_ayri_sayiliyor()
    {
        var bundle = await SeedBundleAsync();

        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Unspecified);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.NotPresent);
        await WriteContradictingAsync(bundle, ContradictingEvidenceVerdict.Trivial);

        var quality = await Store().QualityAsync(Scope("network-core"), TestContext.Current.CancellationToken);

        Assert.Equal(1, quality.ContradictingUnspecified);
        Assert.Equal(1, quality.ContradictingEvaluated);
        Assert.Equal(1.0, quality.ContradictingTrivialRatio);
    }

    /// <summary>
    /// Varsayılan <c>default(ContradictingEvidenceVerdict)</c> bir <b>anlam
    /// taşımıyor</b>.
    ///
    /// <para>
    /// Bu testin tek işi sıfırın hangi değere denk geldiğini çivilemek. Enum'a
    /// bir gün başka bir değer <c>0</c> konumuna eklenirse — ya da
    /// <c>Unspecified</c> kaldırılırsa — alanı doldurmayan her çağıran sessizce
    /// bir karar vermeye başlar. EF'in <c>enabled → status</c> göçünün her
    /// pasif kuralı açmasının sebebi buydu.
    /// </para>
    /// </summary>
    [Fact]
    public void Varsayilan_celisen_kanit_karari_bir_anlam_tasimiyor()
    {
        Assert.Equal(ContradictingEvidenceVerdict.Unspecified, default);
        Assert.NotEqual(ContradictingEvidenceVerdict.NotPresent, default);
    }

    public void Dispose() => _factory.Dispose();
}

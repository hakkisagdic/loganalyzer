using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Evidence;

/// <summary>İnceleme yazılamadı — çağıran 400 dönmeli.</summary>
public sealed class ReviewRejectedException(string reason) : InvalidOperationException(reason);

/// <param name="Total">Kapsam altındaki inceleme sayısı.</param>
/// <param name="Correct">Doğru bulunan rapor sayısı.</param>
/// <param name="Unknown">"Bilmiyorum" sayısı — orana <b>girmiyor</b>.</param>
/// <param name="ContradictingSound">
/// Çelişen kanıt bölümü vardı ve <b>yerindeydi</b>.
/// </param>
/// <param name="ContradictingTrivial">
/// Çelişen kanıt bölümü vardı ama <b>önemsizdi</b> — alanı doldurmak için
/// üretilmiş. RCA risk #5'in ("çelişen kanıt tiyatrosu") sayacı.
/// </param>
/// <param name="ContradictingUnknown">
/// Değerlendirilemedi. <see cref="Unknown"/> gibi paydaya <b>girmiyor</b> ve
/// kendisi bir gösterge.
/// </param>
/// <param name="ContradictingUnspecified">
/// Alan hiç doldurulmadı — <i>"kimse söylemedi"</i>. <c>NotPresent</c>
/// (<i>"bölüm yoktu"</i>) ile <b>ayrı</b> sayılıyor ve paydaya girmiyor.
/// </param>
/// <param name="RankAsked">
/// Rank sorusunun <b>sorulduğu</b> inceleme sayısı — <c>accuracy@k</c>'nın
/// paydası. Soruyu soramayan yakalama yollarından gelen incelemeler burada yok.
/// </param>
/// <param name="RankFirst">Doğru bulgu <b>ilk sıradaydı</b>.</param>
/// <param name="RankTopThree">Doğru bulgu <b>ilk üçteydi</b>.</param>
public sealed record GoldenSetQuality(
    long Total,
    long Correct,
    long Unknown,
    long ContradictingSound = 0,
    long ContradictingTrivial = 0,
    long ContradictingUnknown = 0,
    long ContradictingUnspecified = 0,
    long RankAsked = 0,
    long RankFirst = 0,
    long RankTopThree = 0)
{
    /// <summary>
    /// Karar verilmiş incelemeler: <c>Total - Unknown</c>. Doğruluk oranının
    /// paydası bu.
    /// </summary>
    public long Decided => Total - Unknown;

    /// <summary>
    /// Doğruluk oranı. Payda sıfırsa <see langword="null"/> — sıfır <b>değil</b>.
    ///
    /// <para>
    /// Ayrım bu üründe pahalı bir hata sınıfının önüne geçiyor: "%0 doğru" ile
    /// "henüz karar verilmiş inceleme yok" aynı sayıyla gösterilirse ekran,
    /// ölçülmemiş bir şeyi kötü ölçülmüş gibi gösterir.
    /// </para>
    /// </summary>
    public double? Accuracy => Decided > 0 ? (double)Correct / Decided : null;

    /// <summary>
    /// "Bilmiyorum" oranı — <b>kendisi bir gösterge</b>. Yüksekse ya kanıt
    /// paketi yetersiz ya soru yanlış soruluyor.
    /// </summary>
    public double? UnknownRatio => Total > 0 ? (double)Unknown / Total : null;

    /// <summary>
    /// Çelişen kanıt boyutunda <b>gerçekten değerlendirilmiş</b> inceleme sayısı:
    /// <see cref="ContradictingSound"/> + <see cref="ContradictingTrivial"/>.
    ///
    /// <para>
    /// <c>NotPresent</c> ve <c>Unknown</c> dışarıda ve bu paydanın tamamı bu
    /// yüzden var. <c>NotPresent</c> "bölüm yoktu" demek — değerlendirilecek bir
    /// şey olmadığı için tiyatro da olamaz; onu paydaya koymak, çelişen kanıt
    /// bölümü hiç üretmeyen bir modeli <b>dürüst</b> gösterirdi.
    /// </para>
    /// </summary>
    public long ContradictingEvaluated => ContradictingSound + ContradictingTrivial;

    /// <summary>
    /// Değerlendirilmiş çelişen kanıt bölümlerinin kaçta kaçı önemsizdi —
    /// <b>RCA risk #5'in ölçüsü</b>.
    ///
    /// <para>
    /// Payda sıfırsa <see langword="null"/>, sıfır <b>değil</b>. Ayrım burada
    /// <see cref="Accuracy"/>'dekinden daha keskin: "%0 tiyatro" en iyi sonuç,
    /// "hiç değerlendirilmemiş" ise <b>hiçbir sonuç</b>. İkisi tek sayıya
    /// inerse ekran, ölçülmemiş bir boyutu mükemmel diye gösterir — ve bu,
    /// göstergenin engellemek için var olduğu hatanın kendisi olurdu.
    /// </para>
    /// </summary>
    public double? ContradictingTrivialRatio =>
        ContradictingEvaluated > 0 ? (double)ContradictingTrivial / ContradictingEvaluated : null;

    /// <summary>
    /// <c>accuracy@1</c> — doğru bulgunun <b>ilk sırada</b> olduğu oran.
    ///
    /// <para>
    /// Payda <see cref="RankAsked"/>: sorunun <b>gerçekten sorulduğu</b>
    /// incelemeler. Ayrım alanın <see langword="null"/> olmasından okunmuyor —
    /// <c>null</c> burada *"hiçbir bulgu doğru değildi"* demek, yani bir
    /// <b>ölçüm</b>. İkisini tek <c>null</c>'a indirmek, ölçülmemiş bir
    /// incelemeyi başarısız bir ölçüm gibi göstermek olurdu.
    /// </para>
    /// </summary>
    public double? AccuracyAtOne => RankAsked > 0 ? (double)RankFirst / RankAsked : null;

    /// <summary>
    /// <c>accuracy@3</c> — doğru bulgunun <b>ilk üçte</b> olduğu oran.
    /// <see cref="AccuracyAtOne"/> ile <b>aynı alandan</b> çıkıyor
    /// (<c>rank &lt;= 3</c>); ikinci bir eksen yok.
    /// </summary>
    public double? AccuracyAtThree => RankAsked > 0 ? (double)RankTopThree / RankAsked : null;
}

/// <summary>
/// Altın küme üzerinde <b>atılan cümle</b> ölçümü (T47, Karar 1).
///
/// <para>
/// <b>Eksen incelenmiş paket.</b> Altın küme bir <i>(paket, gerçek kök neden)</i>
/// çiftleri kümesi, dolayısıyla ölçümün birimi paket. Paket başına <b>son</b>
/// rapor sayılıyor — ekranın gösterdiği rapor o (<c>LatestForAsync</c>), ve
/// aynı sıralama kullanılıyor.
/// </para>
///
/// <para>
/// <b>Üç hâl ayrı sayılıyor ve ikisi toplamların içinde görünmüyor:</b>
/// </para>
///
/// <list type="table">
///   <item>
///     <term>A · <see cref="ReasoningAbsent"/></term>
///     <description>
///       İncelenmiş paket, raporu <b>yok</b> — model hiç koşmadı. Paydaya
///       <b>girmiyor</b>: koşmamış bir model ölçülemez.
///     </description>
///   </item>
///   <item>
///     <term>B · <see cref="ProducedNothing"/></term>
///     <description>Koştu, hiç cümle üretmedi. Toplamlara <b>0</b> katıyor.</description>
///   </item>
///   <item>
///     <term>C · <see cref="AllDropped"/></term>
///     <description>
///       Koştu, ürettiklerinin <b>hepsi atıldı</b>. En pahalısı, ve saf sayımda
///       B ile aynı görünüyor: ikisi de boş bulgu listesi.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// B ve C ayrı sayılmasaydı toplamlar onları gizlerdi — B iki toplama da sıfır
/// katıyor, C ikisine de eşit katıyor. İkisi de oranı hareket ettirmiyor ama
/// <b>zıt</b> şeyler söylüyor.
/// </para>
/// </summary>
/// <param name="ReviewedBundles">İncelenmiş <b>ayrık</b> paket sayısı.</param>
/// <param name="ReasoningAbsent">A · raporu olmayan incelenmiş paket.</param>
/// <param name="ReportsMeasured">Ölçüme giren rapor sayısı — paket başına bir tane.</param>
/// <param name="ProducedNothing">B · koştu, sıfır cümle.</param>
/// <param name="AllDropped">C · koştu, hepsi atıldı.</param>
/// <param name="ProducedSentences">Toplam üretilen cümle — <b>oranın paydası</b>.</param>
/// <param name="DroppedSentences">Toplam atılan cümle — payı.</param>
/// <param name="FabricatedSentences">
/// Atıf uydurmuş cümleler; <paramref name="DroppedSentences"/>'in alt kümesi.
/// </param>
public sealed record GoldenSetReasoningQuality(
    long ReviewedBundles,
    long ReasoningAbsent,
    long ReportsMeasured,
    long ProducedNothing,
    long AllDropped,
    long ProducedSentences,
    long DroppedSentences,
    long FabricatedSentences)
{
    /// <summary>
    /// Atılan cümle oranı — <b>cümle başına</b>, rapor başına değil.
    ///
    /// <para>
    /// Rapor başına oranların ortalaması alınsaydı iki cümle yazan bir rapor,
    /// iki yüz cümle yazanla aynı ağırlığı taşırdı. Sorulan soru <i>"bu korpusta
    /// modelin yazdığı cümlelerin kaçta kaçı desteksizdi"</i>, yani payda
    /// <b>cümle</b>.
    /// </para>
    ///
    /// <para>
    /// Payda sıfırsa <see langword="null"/>, sıfır <b>değil</b> — telin kendi
    /// kuralının toplam hâli. <c>0.0</c> burada <i>"hiç cümle atılmadı"</i>
    /// yani mükemmel kalite demek.
    /// </para>
    /// </summary>
    public double? DroppedSentenceRatio =>
        ProducedSentences > 0 ? (double)DroppedSentences / ProducedSentences : null;

    /// <summary>
    /// Atılan cümlelerin kaçta kaçı <b>atıf uydurmuştu</b>.
    ///
    /// <para>
    /// Paydası <see cref="DroppedSentences"/>, üretilen değil: <i>"hiç atıf
    /// yapmadı"</i> ile <i>"atıf uydurdu"</i> iki farklı kalite sorunu — biri
    /// prompt'un, diğeri modelin — ve ikincinin payı yalnızca birincinin
    /// içinde anlamlı.
    /// </para>
    /// </summary>
    public double? FabricatedCitationRatio =>
        DroppedSentences > 0 ? (double)FabricatedSentences / DroppedSentences : null;

    /// <summary>
    /// Ölçülebilen incelenmiş paketlerin oranı — <b>kapsamın kendisi</b>.
    ///
    /// <para>
    /// Düşükse yukarıdaki oranlar altın kümenin küçük bir diliminden geliyor
    /// demektir. Bu sayı olmadan <c>DroppedSentenceRatio</c> temsil ettiğinden
    /// daha geniş okunur.
    /// </para>
    /// </summary>
    public double? MeasuredCoverage =>
        ReviewedBundles > 0 ? (double)ReportsMeasured / ReviewedBundles : null;
}

/// <param name="BundleId">Zorunlu — paketsiz inceleme F4'te ölçülemez.</param>
/// <param name="TriggerId">Alarm tetikliyse dolu, kullanıcı tetikliyse boş.</param>
/// <param name="OwnerGroup">
/// Kaydın yazılacağı grup. Alarm tetikli incelemede <b>yok sayılıyor</b> —
/// grup tetiklenmeden geliyor. Kullanıcı tetiklide, kapsamı birden çok grup
/// olan kişi bunu vermek zorunda.
/// </param>
/// <param name="ActualRootCause">
/// İnceleyenin bildiği doğru cevap; rapor yanlışsa ne olmalıydı. Boş
/// bırakılabilir ve boşluğu bilgi taşıyor — bkz.
/// <see cref="GoldenReviewEntity.ActualRootCause"/>.
/// </param>
/// <param name="CorrectFindingRank">
/// Kaçıncı bulgu doğruydu (1 tabanlı). <see langword="null"/> = <b>hiçbiri</b>,
/// ve bu bir ölçüm.
/// </param>
/// <param name="CorrectFindingRankAsked">
/// Soru bu incelemede <b>soruldu mu</b>. <c>accuracy@k</c>'nın paydası bu.
/// Bulguları göstermeyen bir ekran soruyu soramıyor ve <c>false</c> gönderiyor.
/// </param>
public sealed record ReviewInput(
    Guid BundleId,
    Guid? TriggerId,
    ReviewVerdict Verdict,
    ContradictingEvidenceVerdict ContradictingEvidence,
    string Note,
    string? OwnerGroup = null,
    string? ActualRootCause = null,
    int? CorrectFindingRank = null,
    bool CorrectFindingRankAsked = false);

/// <summary>
/// Altın kümenin deposu ve <b>kapsam kapısı</b> (T38).
///
/// <para>
/// <b>Kapsam neden burada:</b> inceleme ClickHouse'ta değil kontrol
/// düzleminde, dolayısıyla <see cref="Bizigo.Query.IScopedQuery"/> onu
/// kendiliğinden korumuyor — <c>AlertRuleService</c> ile aynı durum ve aynı
/// çözüm. K17'nin dersi "kapsamı ikinci bir yere koyma" değil, "kapsamı
/// <i>dağıtma</i>": incelemenin kapsamı bu sınıfta, sadece bu sınıfta
/// doğrulanıyor.
/// </para>
///
/// <para>
/// Filtre <c>golden_reviews.owner_group</c> kolonundan geçiyor, pakete
/// <c>JOIN</c> atıp JSON açmaktan değil. Paketin kapsamı yalnızca gövdesindeki
/// <c>BundleScope</c>'ta duruyor ve orası <b>sorgulanamaz</b>.
/// </para>
/// </summary>
public sealed class GoldenReviewStore(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// İncelemeyi yazar.
    /// </summary>
    /// <exception cref="ReviewRejectedException">
    /// Paket yok, ya da kapsam tek bir gruba çözülemiyor.
    /// </exception>
    public async Task<GoldenReviewEntity> AddAsync(
        ReviewInput input,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Paket bağı zorunlu ve VAR OLMAK zorunda. Var olmayan bir kimliği
        // kabul etmek, F4'ün karşılaştırmasını sessizce eksik kümeye indirger:
        // kayıt sayılır, karşılaştırmaya giremez.
        var bundleExists = await db.EvidenceBundles
            .AsNoTracking()
            .AnyAsync(b => b.Id == input.BundleId, cancellationToken);

        if (!bundleExists)
        {
            throw new ReviewRejectedException(
                "İnceleme bir kanıt paketine bağlanmak zorunda; verilen paket bulunamadı.");
        }

        // Rank 1 tabanlı. 0 ve negatif REDDEDILIYOR: sessizce kabul edilseydi
        // kayıt paydaya girer ama hiçbir `accuracy@k` kovasına düşmezdi — yani
        // oranı aşağı çeken, sebebi görünmeyen bir satır. "Hiçbiri doğru
        // değildi"nin ifadesi `null`, sıfır değil.
        // Sıra verilmiş ama soru sorulmamış: tutarsız. Sessizce kabul etmek,
        // paydaya girmeyen bir kaydın paya girmesi demek olurdu — yani %100'ü
        // aşabilen bir oran.
        if (input.CorrectFindingRank is not null && !input.CorrectFindingRankAsked)
        {
            throw new ReviewRejectedException(
                "Bulgu sırası verilmiş ama soru sorulmamış olarak işaretlenmiş; ikisi birlikte gelir.");
        }

        if (input.CorrectFindingRank is { } rank && rank < 1)
        {
            throw new ReviewRejectedException(
                $"Bulgu sırası 1 tabanlı; {rank} geçersiz. Hiçbir bulgu doğru değilse sıra boş bırakılır.");
        }

        var entity = new GoldenReviewEntity
        {
            BundleId = input.BundleId,
            TriggerId = input.TriggerId,
            CorrectFindingRank = input.CorrectFindingRank,
            CorrectFindingRankAsked = input.CorrectFindingRankAsked,
            OwnerGroup = await ResolveGroupAsync(db, input, scope, cancellationToken),
            Verdict = input.Verdict,
            ContradictingEvidence = input.ContradictingEvidence,
            Note = input.Note ?? string.Empty,
            ActualRootCause = input.ActualRootCause ?? string.Empty,
            ReviewerSubject = scope.Subject,
            ReviewedAt = _time.GetUtcNow(),
        };

        db.GoldenReviews.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <summary>
    /// Kalite göstergesi (T38 kabul kriteri).
    ///
    /// <para>
    /// Sayım veritabanında yapılıyor, satırlar çekilip bellekte değil: gösterge
    /// altın küme büyüdükçe pahalılaşırsa ilk kaldırılacak şey gösterge olur.
    /// </para>
    ///
    /// <para>
    /// Boş kümede de <b>bir sonuç dönüyor</b> — sıfırlarla. Boş dönmek ekranın
    /// göstergeyi gizlemesine izin verirdi ve gizlenen bir sıfır, "henüz
    /// ölçülmedi" ile "ölçüldü, sıfır" arasındaki farkı siler.
    /// </para>
    /// </summary>
    public async Task<GoldenSetQuality> QualityAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = Visible(db, scope);

        var total = await query.LongCountAsync(cancellationToken);
        var correct = await query.LongCountAsync(r => r.Verdict == ReviewVerdict.Correct, cancellationToken);
        var unknown = await query.LongCountAsync(r => r.Verdict == ReviewVerdict.Unknown, cancellationToken);

        // Çelişen kanıt boyutu ayrı sayılıyor: bir rapor bütün olarak doğru
        // olup çelişen kanıt alanını uydurmuş olabilir (RCA risk #5). Tek bir
        // "doğru muydu?" sorusu bu boyutu ölçemiyor, o yüzden T38 alanı ayrı
        // açtı ve burada ayrı toplanıyor.
        var contradictingSound = await query.LongCountAsync(
            r => r.ContradictingEvidence == ContradictingEvidenceVerdict.Sound, cancellationToken);
        var contradictingTrivial = await query.LongCountAsync(
            r => r.ContradictingEvidence == ContradictingEvidenceVerdict.Trivial, cancellationToken);
        var contradictingUnknown = await query.LongCountAsync(
            r => r.ContradictingEvidence == ContradictingEvidenceVerdict.Unknown, cancellationToken);
        var contradictingUnspecified = await query.LongCountAsync(
            r => r.ContradictingEvidence == ContradictingEvidenceVerdict.Unspecified, cancellationToken);

        // accuracy@k'nın paydası SORUNUN SORULDUĞU kayıtlar.
        //
        // `CorrectFindingRank == null` iki farklı şey olabilir: "hiçbir bulgu
        // doğru değildi" (bir ölçüm) ya da "soru sorulmadı" (ölçümün yokluğu).
        //
        // Ayrım şema sürümüne bağlanamıyor ve sebebi ölçüldü: iki yakalama
        // yolu aynı sürümle yazıyor ama yalnızca biri soruyu sorabiliyor —
        // rapor ekranı bulguları gösteriyor, alarm kapatma ekranı göstermiyor.
        // Sürüme bağlansaydı kapatma yoluyla yazılan her inceleme sorulmamış
        // bir soruyla paydaya girer ve oranı sessizce aşağı çekerdi.
        var rankQuery = query.Where(r => r.CorrectFindingRankAsked);

        var rankAsked = await rankQuery.LongCountAsync(cancellationToken);
        var rankFirst = await rankQuery.LongCountAsync(r => r.CorrectFindingRank == 1, cancellationToken);
        var rankTopThree = await rankQuery.LongCountAsync(
            r => r.CorrectFindingRank != null && r.CorrectFindingRank <= 3, cancellationToken);

        return new GoldenSetQuality(
            total, correct, unknown,
            contradictingSound, contradictingTrivial, contradictingUnknown, contradictingUnspecified,
            rankAsked, rankFirst, rankTopThree);
    }

    /// <summary>
    /// Altın küme üzerinde atılan cümle ölçümü (T47, Karar 1).
    ///
    /// <para>
    /// <b>Kapsam kapısı yine <see cref="Visible"/>'dan geçiyor:</b> hangi
    /// paketlerin ölçüleceği, kullanıcının görebildiği <i>incelemelerden</i>
    /// türüyor. <c>rca_reports</c>'un kendi <c>owner_group</c> kolonu yok —
    /// kapsam pakete bağlı — dolayısıyla filtreyi rapor tarafına koymak
    /// mümkün değil ve olmamalı: ikinci bir kapsam yolu, K17'nin dağıtılmasını
    /// yasakladığı şey.
    /// </para>
    ///
    /// <para>
    /// <b>Paket başına <i>son</i> rapor.</b> "Daha yenisi yok" olarak
    /// yazılıyor, ve sıralama <c>RcaReportStore.LatestForAsync</c>'inkiyle
    /// birebir aynı (<c>CreatedAt</c>, eşitlikte <c>Id</c>). Ayrışsalardı ekran
    /// bir raporu gösterir, gösterge başkasını sayardı — ve hiçbir şey bunu
    /// söylemezdi.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Bilinen sınır:</b> bir paket incelendikten <i>sonra</i> yeniden
    /// koşturulursa ölçülen rapor, insanın yargıladığı rapor olmayabilir.
    /// Bugün bunu ayırt edecek bir bağ yok (inceleme pakete bağlı, rapora
    /// değil). Sayım yine de "son söz"ü ölçüyor, ki ekranın gösterdiği o.
    /// </para>
    /// </summary>
    public async Task<GoldenSetReasoningQuality> ReasoningQualityAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var reviewedBundles = Visible(db, scope).Select(r => r.BundleId).Distinct();

        // "Daha yenisi yok" — GroupBy + First'ten farklı olarak her sağlayıcıda
        // aynı SQL'e çeviriliyor, ve sıralaması LatestForAsync ile aynı.
        var latest = db.RcaReports
            .AsNoTracking()
            .Where(rep => reviewedBundles.Contains(rep.BundleId))
            .Where(rep => !db.RcaReports.Any(other =>
                other.BundleId == rep.BundleId
                && (other.CreatedAt > rep.CreatedAt
                    || (other.CreatedAt == rep.CreatedAt && other.Id > rep.Id))));

        var reviewed = await reviewedBundles.LongCountAsync(cancellationToken);
        var measured = await latest.LongCountAsync(cancellationToken);

        // B ve C: toplamların gizlediği iki hâl. B iki toplama da sıfır katıyor,
        // C ikisine de eşit katıyor — ikisi de oranı hareket ettirmiyor ama zıt
        // şeyler söylüyor.
        var producedNothing = await latest.LongCountAsync(
            rep => rep.ProducedSentenceCount == 0, cancellationToken);
        var allDropped = await latest.LongCountAsync(
            rep => rep.ProducedSentenceCount > 0
                && rep.DroppedSentenceCount == rep.ProducedSentenceCount,
            cancellationToken);

        // Boş kümede `SumAsync` sağlayıcıya göre NULL dönebiliyor; `(long?)`
        // ile alınıp sıfıra düşürülüyor. Sessizce patlayan bir gösterge,
        // gösterilmeyen bir göstergeden kötü.
        var produced = await latest.SumAsync(rep => (long?)rep.ProducedSentenceCount, cancellationToken) ?? 0;
        var dropped = await latest.SumAsync(rep => (long?)rep.DroppedSentenceCount, cancellationToken) ?? 0;
        var fabricated = await latest.SumAsync(
            rep => (long?)rep.FabricatedCitationSentenceCount, cancellationToken) ?? 0;

        return new GoldenSetReasoningQuality(
            ReviewedBundles: reviewed,

            // A: incelenmiş ama raporu olmayan paket. Çıkarma ile bulunuyor,
            // ayrı bir sorguyla değil: iki sorgu arasında yeni bir rapor
            // yazılırsa sayılar tutmaz ve fark NEGATİF görünebilirdi.
            ReasoningAbsent: reviewed - measured,
            ReportsMeasured: measured,
            ProducedNothing: producedNothing,
            AllDropped: allDropped,
            ProducedSentences: produced,
            DroppedSentences: dropped,
            FabricatedSentences: fabricated);
    }

    /// <summary>Bir paketin kapsam altındaki incelemeleri, en yeniden eskiye.</summary>
    public async Task<IReadOnlyList<GoldenReviewEntity>> ForBundleAsync(
        Guid bundleId,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        return await Visible(db, scope)
            .Where(r => r.BundleId == bundleId)
            .OrderByDescending(r => r.ReviewedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Kapsam filtresi — <b>tek yer</b>.
    ///
    /// <para>
    /// Boş kapsam <see cref="Enumerable.Empty{T}"/>'ye değil, hiçbir satırın
    /// eşleşemeyeceği bir sorguya çevriliyor: çağıran yine <c>IQueryable</c>
    /// alıyor ve "filtre yok"a düşen bir yol kalmıyor.
    /// </para>
    /// </summary>
    private static IQueryable<GoldenReviewEntity> Visible(ControlPlaneDbContext db, AccessScope scope)
    {
        var query = db.GoldenReviews.AsNoTracking();

        if (scope.IsUnrestricted)
        {
            return query;
        }

        if (scope.OwnerGroups.Count == 0)
        {
            return query.Where(_ => false);
        }

        var groups = scope.OwnerGroups.ToArray();
        return query.Where(r => groups.Contains(r.OwnerGroup));
    }

    /// <summary>
    /// İncelemenin yazılacağı grup.
    ///
    /// <para>
    /// <b>Grup incelenen şeyden geliyor, inceleyenden değil.</b> Alarm tetikli
    /// incelemede tetiklenmenin kendi <c>OwnerGroup</c>'u kullanılıyor: alarm
    /// hangi ekibin verisinde çıktıysa incelemesi o ekibin göstergesine
    /// yazılmalı. Kapsamı geniş bir kişinin başka bir ekibin alarmını kapatıp
    /// kaydı <i>kendi</i> grubuna yazması, göstergeyi sessizce yanlış ekibe
    /// mal ederdi.
    /// </para>
    ///
    /// <para>
    /// Tetiklenmenin grubu ayrıca <b>kapsam içinde olmak zorunda</b> — aksi
    /// hâlde bir kullanıcı görmediği bir alarmı kapatabilirdi.
    /// </para>
    ///
    /// <para>
    /// Kullanıcı tetikli incelemede alarm yok, dolayısıyla grup açıkça
    /// veriliyor ya da kapsam tek bir gruba çözülüyor. Sistemin çok gruplu bir
    /// kapsamdan kendi başına seçmesi, kaydı yanlış ekibe yazmanın sessiz
    /// yoluydu.
    /// </para>
    /// </summary>
    private static async Task<string> ResolveGroupAsync(
        ControlPlaneDbContext db,
        ReviewInput input,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        if (input.TriggerId is { } triggerId)
        {
            var group = await db.AlertTriggers
                .AsNoTracking()
                .Where(t => t.Id == triggerId)
                .Select(t => t.OwnerGroup)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new ReviewRejectedException("Verilen alarm tetiklenmesi bulunamadı.");

            return scope.Allows(group)
                ? group
                : throw new ReviewRejectedException("Bu alarm kapsamınızın dışında.");
        }

        if (!string.IsNullOrWhiteSpace(input.OwnerGroup))
        {
            var requested = input.OwnerGroup.Trim();

            return scope.Allows(requested)
                ? requested
                : throw new ReviewRejectedException("İstenen grup kapsamınızın dışında.");
        }

        if (scope.OwnerGroups.Count == 1)
        {
            return scope.OwnerGroups.First();
        }

        throw new ReviewRejectedException(
            scope.OwnerGroups.Count == 0
                ? "İnceleme yazmak için bir kapsam grubu gerekiyor."
                : "Kapsamınız birden çok grup içeriyor; incelemenin yazılacağı grup belirtilmeli.");
    }
}

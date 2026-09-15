using System.Globalization;

namespace Bizigo.Capacity;

/// <summary>Defterin hükmü — dört hâl, ve dördü ayrı şey söylüyor.</summary>
public enum LedgerVerdict
{
    /// <summary>
    /// Beklenen her satır ürün tarafında <b>eşleşti</b>. Bu iddianın gücü
    /// <see cref="ArrivalLedger.ProductArchived"/>'in bir <i>sayım</i> değil bir
    /// <b>eşleşme</b> olmasına bağlı — gerekçesi orada yazılı.
    /// </summary>
    Consistent,

    /// <summary>
    /// Kayıp var ve <b>bir katmana yerleştirildi</b>. Bugün yapamadığımız tam
    /// olarak bu.
    /// </summary>
    LossLocated,

    /// <summary>
    /// <b>Üçüncü hâl.</b> Kayıp var ama hiçbir katman sahiplenmiyor — ya da
    /// beklenenden <b>fazla</b> satır var. Kayba saymak sayıyı bozardı,
    /// yok saymak arızayı gizlerdi.
    /// </summary>
    Uncertain,

    /// <summary>
    /// <b><c>LEDGER-LIMITED</c>.</b> Sayaçlardan biri güvenilmez, dolayısıyla
    /// defter <b>kayıp demiyor, ölçemedim diyor</b>. Dış aracın
    /// <c>OBSERVER-LIMITED</c>'ının karşılığı.
    /// </summary>
    Limited,
}

/// <param name="Layer">Suçlanan katman.</param>
/// <param name="Count">O katmana yerleştirilen satır sayısı.</param>
/// <param name="Reason">
/// <b>Hangi sayaca dayanarak</b> suçlandığı. Bu alan defterin bitti
/// ölçütünün yarısı: *"ayrıştıklarında hangi katmanın suçlandığı yazılı"*.
/// </param>
public sealed record LedgerAttribution(LedgerLayer Layer, long Count, string Reason);

/// <summary>
/// <b>B02 — üç katmanlı varış defteri.</b>
///
/// <para>
/// Bir yük koşumunun sonunda üç bağımsız sayaç okunuyor ve tek bir raporda yan
/// yana konuyor. Amaç bir EPS sayısı üretmek <b>değil</b>: kaybı bir
/// <b>katmana yerleştirmek</b>.
/// </para>
///
/// <h3>Üç sayı aynı şeyi saymıyor — ve bu bilinçli</h3>
///
/// <list type="bullet">
/// <item><b>Tel</b> düşürmeyi sayıyor (negatif kanıt): sayaç sıfırsa "vardı"
/// demiyor, "çekirdek düşürdüğünü bilmiyor" diyor.</item>
/// <item><b>Collector</b> kabul ettiğini ve <b>reddettiğini</b> ayrı sayıyor.</item>
/// <item><b>Ürün</b> iki ayrı soru cevaplıyor: <i>dayanıklı yazıldı mı</i> ve
/// <i>aranabilir oldu mu</i>. İkisini tek sayıya indirmek, ham arşivde duran
/// ama <c>events</c>'e girmemiş bir satırı görünmez yapardı — ve o gerçek bir
/// arıza sınıfı.</item>
/// </list>
///
/// <h3>Kutunun dışına çıkamıyor — kapı varmış gibi okunmasın</h3>
///
/// <para>
/// Defterin kanıtladığı şey <i>bizim ürettiğimiz yükün</i> nerede kaybolduğu.
/// <c>events</c>'te 999.000 görmek, müşterinin 1.000.000 gönderdiğini
/// kanıtlamıyor. Ve <b>çekirdek ile collector arasındaki kaybı ayıramıyor</b>:
/// OS sayaçları daraltıyor, paket yakalama kapatırdı — o yüzden bu hâlin adı
/// <see cref="LedgerVerdict.Uncertain"/> ve gerekçesi rapora <b>yazılıyor</b>.
/// </para>
/// </summary>
/// <param name="RunId">
/// Koşum kimliği. Ayrı olması şart: bir kademenin gecikmiş olayları diğerinin
/// sayısını kirletiyor (kapasite belgesi §4).
/// </param>
/// <param name="Expected">
/// Üretecin <b>bastığını iddia ettiği</b> satır sayısı. Defter bu sayıyı
/// üretmiyor, B01'in manifestinden alıyor — ve bunun bir <i>iddia</i> olduğu
/// yazılı: üreteç istediği hıza ulaşamadıysa suçlu hedef değil
/// (<c>GENERATOR-LIMITED</c>, B01).
/// </param>
/// <param name="WireDrops">OS soket sayacının düşürdüğünü söylediği paket sayısı.</param>
/// <param name="CollectorAccepted">Collector'ın kabul ettiği log kaydı sayısı.</param>
/// <param name="CollectorRefused">Collector'ın <b>reddettiği</b> kayıt sayısı.</param>
/// <param name="ProductArchived">
/// <b>Manifest ile ham arşivin EŞLEŞEN kayıt sayısı</b> — bir sayım değil.
/// Ayrım <see cref="LedgerVerdict.Consistent"/>'ın gücünü belirliyor: düz bir
/// sayım, bir satırın kaybolup başka birinin iki kez yazılmasını
/// <b>görmez</b>. Eşleşme sha256 üzerinden kurulduğu için görüyor.
/// </param>
/// <param name="ProductSearchable">
/// <c>events</c> tablosunda koşum penceresinde ve <c>owner_group</c> içinde
/// sayılan satır.
/// </param>
public sealed record ArrivalLedger(
    string RunId,
    long Expected,
    LedgerReading WireDrops,
    LedgerReading CollectorAccepted,
    LedgerReading CollectorRefused,
    LedgerReading ProductArchived,
    LedgerReading ProductSearchable)
{
    private const string KernelGap =
        "çekirdek ile collector arasında: collector beklenenden az kayıt gördü ve " +
        "OS sayacı düşürme bildirmiyor. Defter burayı KAPATAMIYOR (paket yakalama yok).";

    /// <summary>
    /// Hüküm. <b>Sıra gerekçeli</b> ve aşağıdaki her adım bir hata sınıfını
    /// kapatıyor.
    /// </summary>
    public LedgerVerdict Verdict => Evaluate().Verdict;

    /// <summary>Kaybın yerleştirildiği katmanlar; boş olabilir.</summary>
    public IReadOnlyList<LedgerAttribution> Attributions => Evaluate().Attributions;

    /// <summary>Hükmün tek satırlık gerekçesi — rapora <b>bu</b> giriyor.</summary>
    public string Rationale => Evaluate().Rationale;

    /// <summary>Ölçülemeyen okumalar — <c>LEDGER-LIMITED</c>'ın gövdesi.</summary>
    public IReadOnlyList<LedgerReading> LimitedReadings =>
        [.. Readings().Where(static r => !r.IsMeasured)];

    private IEnumerable<LedgerReading> Readings()
    {
        yield return WireDrops;
        yield return CollectorAccepted;
        yield return CollectorRefused;
        yield return ProductArchived;
        yield return ProductSearchable;
    }

    private (LedgerVerdict Verdict, IReadOnlyList<LedgerAttribution> Attributions, string Rationale) Evaluate()
    {
        // 1 · Sonuç kolonu okunamıyorsa defterin söyleyecek hiçbir şeyi yok.
        //     Bunu "kayıp" saymak, ölçüm aracının kendi körlüğünü hedefin
        //     suçu olarak raporlamak olurdu.
        if (!ProductSearchable.IsMeasured || !ProductArchived.IsMeasured)
        {
            return (
                LedgerVerdict.Limited,
                [],
                "Ürün katmanı okunamadı: " + string.Join(
                    " · ",
                    new[] { ProductArchived, ProductSearchable }
                        .Where(static r => !r.IsMeasured)
                        .Select(static r => r.Describe())));
        }

        var searchable = ProductSearchable.Value!.Value;
        var archived = ProductArchived.Value!.Value;

        // 2 · Beklenenden FAZLA satır. Kayıp değil, ama "tutarlı" da değil:
        //     çift yazma da olabilir, aynı pencerede yabancı trafik de. Defter
        //     ikisini AYIRT EDEMİYOR ve edemediğini söylüyor.
        if (searchable > Expected)
        {
            return (
                LedgerVerdict.Uncertain,
                [],
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"`events` beklenenden {searchable - Expected} satır FAZLA " +
                    $"({searchable} > {Expected}). Çift yazma ile aynı pencereye düşen " +
                    $"yabancı trafik defterde aynı görünüyor — kayba da tutarlıya da sayılmıyor."));
        }

        var gap = Expected - searchable;

        // 3 · Boşluk yok. Tel ve collector sayaçları okunamamış olsa bile hüküm
        //     ayakta: yerleştirilecek bir kayıp yok. Bu bir yargı çağrısı ve
        //     dayandığı şey `ProductArchived`'in EŞLEŞME olması — düz bir sayım
        //     olsaydı "biri kayıp, biri iki kez" hâli buradan sessizce geçerdi.
        if (gap == 0 && archived == Expected)
        {
            var limited = LimitedReadings;

            return (
                LedgerVerdict.Consistent,
                [],
                limited.Count == 0
                    ? $"Beklenen {Expected} satırın hepsi ham arşivde eşleşti ve `events`'te sayıldı."
                    : $"Beklenen {Expected} satırın hepsi eşleşti; yerleştirilecek kayıp yok. " +
                      "Okunamayan sayaçlar hükmü değiştirmiyor ama yazılı duruyor: " +
                      string.Join(" · ", limited.Select(static r => r.Describe())));
        }

        // 4 · Boşluk VAR. Yerleştirmek için tel ve collector sayaçları şart;
        //     biri okunamıyorsa suçu ürüne yıkmak, dış aracın
        //     OBSERVER-LIMITED ile reddettiği şeyin aynısı olurdu.
        var missingCounters = new[] { WireDrops, CollectorAccepted, CollectorRefused }
            .Where(static r => !r.IsMeasured)
            .ToArray();

        if (missingCounters.Length > 0)
        {
            return (
                LedgerVerdict.Limited,
                [],
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{gap} satır eksik ama yerleştirilemiyor — sayaç okunamadı: ") +
                string.Join(" · ", missingCounters.Select(static r => r.Describe())));
        }

        var drops = WireDrops.Value!.Value;
        var accepted = CollectorAccepted.Value!.Value;
        var refused = CollectorRefused.Value!.Value;

        var attributions = new List<LedgerAttribution>();
        var residual = gap;

        // Collector'ın HİÇ GÖRMEDİĞİ satır sayısı. Tel sayacının açıklayabildiği
        // en fazla bu kadar — ve bu sınır ölçüm turunda bir kusur kapattı:
        // düşürme sayacı sıfırdan büyük olduğu için kayıp tele yazılıyordu,
        // OYSA collector beklenen kadar kayıt görmüşse o düşürmeler bu koşumun
        // satırları değil (aynı porta gelen başka trafik de aynı sayaca yazıyor).
        // Yanlış katmanı suçlamak, hiç suçlamamaktan kötü: arama yanlış yerde
        // başlar.
        var unseen = Math.Max(0, Expected - accepted - refused);
        var wireNote = string.Empty;

        if (drops > 0 && unseen > 0)
        {
            var taken = Math.Min(residual, Math.Min(drops, unseen));
            residual -= taken;
            attributions.Add(new LedgerAttribution(
                LedgerLayer.Wire,
                taken,
                $"{WireDrops.Source} {drops} düşürme bildiriyor ve collector {unseen} satırı hiç görmedi"));
        }
        else if (drops > 0)
        {
            wireNote =
                $" NOT: {WireDrops.Source} {drops} düşürme bildiriyor ama collector beklenen kadar " +
                "kayıt gördü — o düşürmeler bu koşumun satırları değil (aynı sayaca yazan başka trafik).";
        }

        if (residual > 0 && refused > 0)
        {
            var taken = Math.Min(residual, refused);
            residual -= taken;
            attributions.Add(new LedgerAttribution(
                LedgerLayer.Collector,
                taken,
                $"{CollectorRefused.Source} {refused} ret bildiriyor"));
        }

        if (residual > 0)
        {
            // Collector beklenenden az kayıt gördü ve düşürmeyi kimse
            // sahiplenmiyor: defterin YAZILI kör noktası.
            if (unseen > 0)
            {
                return (
                    LedgerVerdict.Uncertain,
                    attributions,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{gap} satır eksik; {gap - residual} yerleştirildi, {residual} satır ") +
                    KernelGap);
            }

            if (archived < accepted)
            {
                attributions.Add(new LedgerAttribution(
                    LedgerLayer.Product,
                    residual,
                    $"collector {accepted} kabul etti, ham arşivde {archived} eşleşti — " +
                    "kayıp boru hattında (ingest → arşiv)"));
                residual = 0;
            }
            else if (searchable < archived)
            {
                attributions.Add(new LedgerAttribution(
                    LedgerLayer.Product,
                    residual,
                    $"ham arşivde {archived} eşleşti, `events`'te {searchable} sayıldı — " +
                    "satır DAYANIKLI yazıldı ama ARANAMIYOR"));
                residual = 0;
            }
        }

        if (residual > 0)
        {
            // SAVUNMA DALI. Yukarıdaki daralmalar bugünkü sayaç kümesinde
            // boşluğun tamamını sahipleniyor; buraya düşmek sayaçların
            // birbiriyle çeliştiği anlamına gelir (ör. arşivde kabul edilenden
            // fazla kayıt). O hâl bir ölçüm arızası ve KAYIP diye
            // raporlanmamalı — bu yüzden dal duruyor ve sayıları basıyor.
            return (
                LedgerVerdict.Uncertain,
                attributions,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{gap} satır eksik; {gap - residual} yerleştirildi, {residual} satır hiçbir " +
                    $"katmana yerleşmiyor. Sayaçlar çelişiyor: tel={drops} kabul={accepted} " +
                    $"ret={refused} arşiv={archived} aranabilir={searchable} beklenen={Expected}."));
        }

        return (
            LedgerVerdict.LossLocated,
            attributions,
            string.Create(CultureInfo.InvariantCulture, $"{gap} satır eksik ve tamamı yerleştirildi.")
                + wireNote);
    }

    /// <summary>
    /// <b>Tek rapor.</b> Üç sayı yan yana, hüküm, ve suçlanan katman(lar) —
    /// bitti ölçütünün birinci yarısı.
    ///
    /// <para>
    /// Okunamayan sayaçlar <b>hükümden bağımsız</b> olarak da basılıyor: hüküm
    /// <see cref="LedgerVerdict.Consistent"/> olsa bile hangi sayacın
    /// güvenilmez olduğu görünmeli, yoksa bir sonraki koşum aynı körlükle
    /// koşar ve kimse bilmez.
    /// </para>
    /// </summary>
    public string Report()
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"varış defteri · koşum={RunId} beklenen={Expected}"),
            "  " + WireDrops.Describe(),
            "  " + CollectorAccepted.Describe(),
            "  " + CollectorRefused.Describe(),
            "  " + ProductArchived.Describe(),
            "  " + ProductSearchable.Describe(),
            $"hüküm: {(Verdict == LedgerVerdict.Limited ? "LEDGER-LIMITED" : Verdict.ToString().ToUpperInvariant())}",
            "  " + Rationale,
        };

        foreach (var attribution in Attributions)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  suçlanan katman: {attribution.Layer} ({attribution.Count} satır) — {attribution.Reason}"));
        }

        var limited = LimitedReadings;

        if (limited.Count > 0 && Verdict != LedgerVerdict.Limited)
        {
            lines.Add("  UYARI · okunamayan sayaç: " + string.Join(" · ", limited.Select(static r => r.Source)));
        }

        return string.Join('\n', lines);
    }
}

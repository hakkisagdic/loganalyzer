using Bizigo.Contracts;

namespace Bizigo.Evidence;

/// <summary>Derleme işareti — mimari testler bu türden derlemeyi buluyor.</summary>
public static class EvidenceMarker;

/// <summary>
/// Kanıt türleri (K21). <b>Beşi de tanımlı, üçü karşılanıyor, ikisi kalıcı
/// olarak kapsam dışı.</b>
///
/// <para>
/// Sözleşmenin beşini birden tanımasının sebebi motorun yeniden yazılmaması:
/// <see cref="EvidenceCollector"/> bu enum'u geziyor, kayıtlı sağlayıcı
/// listesini değil. F5 kapsam kararı (S1) türlerin kaderini ayırdı ve ayrımın
/// kendisi <see cref="EvidenceKinds"/>'de yazılı.
/// </para>
///
/// <para>
/// <b>Değerler kalıcı</b> — T36 kanıt paketini saklıyor. Kapsam dışı kalan bir
/// tür bu enum'dan <b>silinmiyor</b>: silmek, o değerle yazılmış geçmiş
/// paketleri okunamaz yapardı.
/// </para>
/// </summary>
public enum EvidenceKind
{
    /// <summary>ClickHouse <c>events</c> — F3. Asıl değer burada.</summary>
    Log = 1,

    /// <summary><c>change_events</c> — F3. RCA'nın en güçlü sinyali.</summary>
    Change = 2,

    /// <summary>
    /// <b>Kapsam dışı</b> (F5 · S1). Metrik ingest+depolama gerektiriyordu ve
    /// K1'i aşıyordu; karar "bu ürün metriğe bakmıyor" oldu.
    /// <see cref="EvidenceKinds.Exempt"/>.
    /// </summary>
    Metric = 3,

    /// <summary>
    /// <b>Kapsam dışı</b> (F5 · S1). Ağ cihazları trace üretmiyor (K2); türün
    /// değerli olması ürünün uygulama gözlemlenebilirliğine girmesine bağlıydı
    /// ve girilmedi. <see cref="EvidenceKinds.Exempt"/>.
    /// </summary>
    Trace = 4,

    /// <summary>
    /// <b>Karşılanıyor — sınırıyla birlikte</b> (F5 · S1).
    /// <c>TopologyProvider</c> envanterdeki düz öznitelikleri (upstream, vlan,
    /// firmware) okuyor; <b>ilişki grafiği yok</b>, yani iki kademe yukarıdaki
    /// ortak ata hesaplanmıyor. Sınır sağlayıcının <c>Detail</c>'ına da giriyor.
    /// </summary>
    Topology = 5,
}

/// <summary>
/// Kanıt türlerinin kapsam kaderi — <b>"bir gün gelecek" ile "hiç
/// gelmeyecek" ayrı listelerde</b> (§8).
///
/// <para>
/// Bu ayrımın olmaması ölçülmüş bir sorundu. F5 kapsam kararından önce
/// <see cref="EvidenceKind.Metric"/>, <see cref="EvidenceKind.Trace"/> ve
/// <see cref="EvidenceKind.Topology"/> tek bir listede duruyordu ve ekran
/// üçü için de <i>"(F5)"</i> yazıyordu — yani bir <b>söz</b>. Karar ikisini
/// kalıcı olarak kapsam dışına aldı; söz orada kalsaydı ekran, verilmiş bir
/// karardan sonra hâlâ bekletiyor olurdu ve yanlış olduğu hiçbir yerde kırmızı
/// yanmazdı.
/// </para>
///
/// <para>
/// <b>Muafiyet eklemek iki bilinçli hareket gerektiriyor:</b> listeye ad
/// eklemek <b>ve</b> <see cref="ExpectedExemptCount"/>'u artırmak. Emsali
/// <c>ProducesContractTests</c>'in <c>ExpectedExemptCount</c>'u; gerekçe aynı —
/// tek hareketle büyüyen bir muafiyet listesi, muafiyeti kararsız hâle getirir.
/// </para>
/// </summary>
public static class EvidenceKinds
{
    /// <summary>
    /// Bu ürünün <b>bakmayacağı</b> kanıt türleri. Sağlayıcısı yok ve
    /// <b>olmayacak</b>; <see cref="EvidenceStatus.NotRegistered"/> değil
    /// <see cref="EvidenceStatus.OutOfScope"/> üretiyorlar.
    /// </summary>
    public static readonly IReadOnlySet<EvidenceKind> Exempt =
        new HashSet<EvidenceKind> { EvidenceKind.Metric, EvidenceKind.Trace };

    /// <summary>
    /// Muafiyet sayısının <b>çivisi</b>. Değiştirmek bilinçli bir hareket
    /// olmak zorunda; bir test ikisini eşitliyor.
    /// </summary>
    public const int ExpectedExemptCount = 2;

    public static bool IsExempt(EvidenceKind kind) => Exempt.Contains(kind);
}

/// <summary>
/// Bir kanıt sağlayıcısının koşusunun <b>sonucu değil, sonucun cinsi</b>.
///
/// <para>
/// Bu enum'un varlık sebebi tek bir hata sınıfı: <b>iki farklı olguyu tek
/// değerde toplamak.</b> "Değişiklik kaydı yok" ile "değişiklik kayıtlarına
/// bakamıyoruz" aynı boş listeye düşerse rapor, ölçmediği bir şeyi ölçmüş gibi
/// yazar — ve bunu hiçbir hata mesajı bozmaz. RCA artifact'ının 4. riski
/// tam olarak bu: change beslemesi bağlanmamışsa "değişiklik yok" diyen bir
/// sağlayıcı olur.
/// </para>
///
/// <para>
/// Sıra anlamlı değil; değerler kalıcı (T36 kanıt paketini saklıyor).
/// </para>
/// </summary>
public enum EvidenceStatus
{
    /// <summary>Koştu, kanıt buldu.</summary>
    Gathered = 1,

    /// <summary>
    /// Koştu, pencerede eşleşme <b>yok</b> — ve bu bir kanıt. "Bu pencerede
    /// hiçbir şey değişmedi" cümlesi kurulabiliyor.
    /// </summary>
    Empty = 2,

    /// <summary>
    /// Kaynak <b>hiç</b> beslenmemiş: pencerede de, dışında da tek satır yok.
    /// Kanıt <b>değil</b>, ölçümün yokluğu. Rapor "değişiklik olmadı" diyemez,
    /// "değişiklik akışı bağlı değil" demek zorunda.
    /// </summary>
    NeverFed = 3,

    /// <summary>
    /// Sağlayıcı kayıtlı ama koşamıyor — bağımlılığı kapalı ya da fazı gelmemiş.
    /// </summary>
    Unavailable = 4,

    /// <summary>
    /// Koştu ve patladı. Paket üretilmeye devam ediyor (tek sağlayıcının arızası
    /// raporu düşürmemeli) ama kanıt <b>eksik</b> ve rapor bunu söylüyor.
    /// </summary>
    Failed = 5,

    /// <summary>
    /// Bu tür için hiç sağlayıcı kayıtlı değil — <b>ama bir gün olabilir</b>.
    /// <b>Sağlayıcının kendisi bu değeri üretmez</b>; <see cref="EvidenceCollector"/>
    /// türü kayıtlı listede bulamadığında üretiyor.
    ///
    /// <para>
    /// F5 · S1'den sonra bugün <b>hiçbir tür burada değil</b>: ikisi
    /// <see cref="OutOfScope"/>, üçünün sağlayıcısı var. Değer duruyor çünkü
    /// enum'a yeni bir tür eklenmesi hâlâ mümkün ve o gün sessizce atlanmamalı.
    /// </para>
    /// </summary>
    NotRegistered = 6,

    /// <summary>
    /// Bu ürün bu türe <b>bakmıyor</b> — ve bakmayacak (<see cref="EvidenceKinds.Exempt"/>).
    ///
    /// <para>
    /// <see cref="NotRegistered"/>'dan ayrı olması bu deponun §8 kuralının
    /// karşılığı: <i>"bir gün kapanacak"</i> ile <i>"hiç kapanmayacak"</i> aynı
    /// listede duramaz. Tek değerde toplansaydı *"liste boşaldı mı"* sorusunun
    /// cevabı asla evet olamazdı, ve rapor okuyan bir eksikliği bekleyen bir söz
    /// sanardı.
    /// </para>
    /// </summary>
    OutOfScope = 7,
}

/// <summary>
/// RCA'nın baktığı iki pencere: olayın kendisi ve karşılaştırma tabanı.
///
/// <para>
/// İkisi <b>birlikte</b> taşınıyor çünkü F3'ün korelasyonlarının çoğu bir
/// karşılaştırma: "baseline'da yoktu, pencerede var", "baseline'a göre kaç kat".
/// Sağlayıcıya yalnızca olay penceresini vermek, taban seçimini her sağlayıcının
/// kendi başına yapması demek olurdu ve iki sağlayıcı farklı taban seçtiğinde
/// rapor kendi içinde tutarsız olurdu — hiçbir yerde görünmeden.
/// </para>
///
/// <para>
/// Baseline uzunluğunun <b>varsayılanı bu tipte yok</b>, bilerek: T35 onu gerçek
/// veriyle ölçüp gerekçesiyle seçecek. Çok kısa seçilirse her yeni şey
/// "ilk-görülen" olur, çok uzun seçilirse gerçek yenilik gürültüde kaybolur;
/// ikisi de tahminle karar verilecek şey değil.
/// </para>
/// </summary>
public sealed record RcaWindow
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required DateTimeOffset BaselineFrom { get; init; }
    public required DateTimeOffset BaselineTo { get; init; }

    /// <summary>Kapsam <b>daraltması</b> — kullanıcının kapsamını genişletemez.</summary>
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public void Validate()
    {
        if (To <= From)
        {
            throw new ArgumentException($"Olay penceresi geçersiz: {From:O}..{To:O}.", nameof(RcaWindow));
        }

        if (BaselineTo <= BaselineFrom)
        {
            throw new ArgumentException(
                $"Baseline penceresi geçersiz: {BaselineFrom:O}..{BaselineTo:O}.", nameof(RcaWindow));
        }

        // Örtüşen taban, "ilk-görülen"i tanım gereği boşaltır: pencerede beliren
        // her imza tabanda da görünür ve sinyal sessizce hiçbir şey döndürmez.
        if (BaselineTo > From)
        {
            throw new ArgumentException(
                $"Baseline olay penceresiyle örtüşüyor ({BaselineTo:O} > {From:O}); " +
                "ilk-görülen imza sinyali sessizce boşalırdı.",
                nameof(RcaWindow));
        }
    }
}

/// <summary>
/// Bir toplama koşusunun tavanları (risk #6, gürültülü komşu).
///
/// <para>
/// Tavan <b>sağlayıcı başına</b> uygulanıyor: tek bir sağlayıcının geniş
/// sorgusu, diğerlerinin hiç koşamamasına yol açmamalı.
/// </para>
/// </summary>
/// <param name="MaxItems">Sağlayıcı başına kanıt satırı tavanı.</param>
/// <param name="MaxDuration">Sağlayıcı başına süre tavanı.</param>
public sealed record GatherBudget(int MaxItems, TimeSpan MaxDuration)
{
    public static GatherBudget Default { get; } = new(400, TimeSpan.FromSeconds(20));
}

/// <summary>
/// Tek bir kanıt satırı (RCA özelliği §4.1).
/// </summary>
/// <param name="Weight">
/// Sağlayıcının kendi ölçeğinde önem — sıralama için. Sağlayıcılar arası
/// karşılaştırılabilir <b>değil</b>; hipotez sıralaması T36'nın işi.
/// </param>
/// <param name="Drilldown">
/// Kanıttan ham loga inen yol. <b>Ham SQL değil</b>, yapılandırılmış bir sorgu:
/// UI onu <c>IScopedQuery</c>'ye veriyor ve kapsam kapısı yeniden uygulanıyor.
/// SQL dizgisi taşımak, kanıt paketinin kapsam kapısını atlayan bir yol
/// taşıması demek olurdu (K17).
/// </param>
public sealed record EvidenceItem(
    string Id,
    string ProviderId,
    EvidenceKind Kind,
    DateTimeOffset Timestamp,
    double Weight,
    string Summary,
    IReadOnlyDictionary<string, string> Payload,
    EventQuery? Drilldown = null);

/// <summary>
/// Bir sağlayıcının tek koşusunun tamamı — satırlar <b>ve</b> koşunun kendisi
/// hakkında bilinmesi gerekenler.
/// </summary>
public sealed record EvidenceSlice
{
    public required string ProviderId { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required EvidenceStatus Status { get; init; }

    /// <summary>
    /// İnsan-okunur gerekçe — <see cref="EvidenceStatus.Gathered"/> dışındaki
    /// her durumda <b>dolu olmalı</b>. Rapor okuyanın "neden bakılmadı"
    /// sorusuna cevabı burada.
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    public IReadOnlyList<EvidenceItem> Items { get; init; } = [];

    /// <summary>
    /// Kapsam dışında kalan eşleşme <b>sayısı</b> — içeriği değil (K17, RCA §3.2).
    ///
    /// <para>
    /// Rapordaki "kapsamınız dışında 342 ilişkili olay var" satırının kaynağı.
    /// Bilgi sızdırmadan yanlış güveni engelliyor: kök neden başka grubun
    /// cihazındaysa rapor bunu <b>bilmeden</b> yanlış sonuca varırdı.
    /// </para>
    /// </summary>
    public long OutOfScopeCount { get; init; }

    /// <summary>
    /// Bütçe tavanına takıldı mı. <b>Sessiz kırpma en kötüsü:</b> kırpılmış bir
    /// liste "hepsi bu" gibi okunur ve rapor eksik kanıta tam kanıt muamelesi
    /// yapar.
    /// </summary>
    public bool Truncated { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Rapor bu dilime kanıt olarak dayanabilir mi.</summary>
    public bool IsEvidence => Status is EvidenceStatus.Gathered or EvidenceStatus.Empty;

    public static EvidenceSlice NotAvailable(string providerId, EvidenceKind kind, string detail) =>
        new() { ProviderId = providerId, Kind = kind, Status = EvidenceStatus.Unavailable, Detail = detail };
}

/// <summary>
/// Kanıtın <b>nereden geldiğini</b> soyutlayan sözleşme (K21, K22, RCA §3).
///
/// <para>
/// RCA motoru ClickHouse'u <b>doğrudan sorgulamıyor</b>; sağlayıcılara soruyor.
/// Sağlayıcılar da <c>IScopedQuery</c>'den geçiyor, yani kapsam kapısı (K17)
/// kanıt yolunda da tek kapı olarak kalıyor. Mimari test bu derlemenin
/// ClickHouse sürücüsüne bağımlı olmasını yasaklıyor.
/// </para>
///
/// <para>
/// <b>Sözleşmenin taşıyıcı özelliği:</b> yeni bir sağlayıcı eklemek motorda
/// hiçbir şeyi değiştirmiyor. F5'te trace sağlayıcısı geldiğinde yapılacak tek
/// şey onu DI'ye kaydetmek; <see cref="EvidenceCollector"/> tek satır bile
/// değişmiyor. Bu, <c>EvidenceCollectorTests</c>'te bugün sınanıyor.
/// </para>
/// </summary>
public interface IEvidenceProvider
{
    /// <summary>
    /// Kararlı kimlik — <c>"logs.window"</c>, <c>"change.feed"</c>. Kanıt paketi
    /// saklandığı için (T36) bu dizgi <b>şema kadar kalıcı</b>: değişirse
    /// geçmiş paketler kaynaklarını kaybeder.
    /// </summary>
    string Id { get; }

    EvidenceKind Kind { get; }

    /// <summary>
    /// Şu an koşabilir mi. <c>false</c> olan sağlayıcı raporda <b>"bu kanıt türü
    /// kapalı"</b> olarak görünüyor — sessizce atlanmıyor. Rapor okuyanın neye
    /// <b>bakılmadığını</b> bilmesi şart.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// <b>Asla istisna fırlatmamalı.</b> Fırlatırsa
    /// <see cref="EvidenceCollector"/> yakalıyor ve dilimi
    /// <see cref="EvidenceStatus.Failed"/> yapıyor — tek sağlayıcının arızası
    /// paketi düşürmüyor ama sessizce de geçmiyor.
    /// </summary>
    Task<EvidenceSlice> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken);
}

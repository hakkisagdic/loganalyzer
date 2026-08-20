using Bizigo.Contracts;

namespace Bizigo.Query;

/// <summary>
/// <b>Kapsam zorlamasının tek kapısı</b> (K17, F1 §10.2).
///
/// REST uçları, CLI, replay okuma, F3'ün kanıt toplayıcısı ve F4'ün MCP sunucusu —
/// hepsi buradan geçer. ClickHouse row policy tercih edilmedi çünkü tek kapı
/// olması gereken yer bu: agent'lar ve MCP de aynı API'yi kullanacak.
///
/// Her metot <see cref="AccessScope"/> istiyor; kapsamsız çağrı yazılamıyor.
/// Mimari test (T02) ayrıca bu derlemenin dışından <c>ClickHouse.Driver</c>'a
/// referans verilmesini yasaklıyor, yani kimse kapıyı atlayamıyor.
/// </summary>
public interface IScopedQuery
{
    Task<EventPage> SearchEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    Task<LogEvent?> GetEventAsync(Guid eventId, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Olayın OCSF ya da OTel görünümündeki hâli (T16 olay detayı).
    ///
    /// <para>
    /// Alan adlarını API <b>türetmiyor</b>, ClickHouse görünümünden okuyor.
    /// Türetmeyi buraya taşımak eşlemenin ikinci kopyası olurdu; oysa aynı adları
    /// F3'ün Sigma derleyicisi ve doğrudan SQL konuşan araçlar da görüyor (K8).
    /// </para>
    ///
    /// <para>Kapsam dışı olayda <b>boş liste</b> dönüyor — 404'ün bilgi gizleme
    /// gerekçesi burada da geçerli.</para>
    /// </summary>
    Task<IReadOnlyList<EventFieldView>> GetEventViewAsync(
        Guid eventId,
        EventViewKind view,
        AccessScope scope,
        CancellationToken cancellationToken = default);

    Task<long> CountEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kapsam dışında kaç eşleşme olduğunu sayar, içeriği döndürmez.
    /// RCA raporundaki "kapsamınız dışında N ilişkili olay var" satırının kaynağı
    /// (RCA özelliği §3.2) — bilgi sızdırmadan yanlış güveni engelliyor.
    /// </summary>
    Task<long> CountOutOfScopeEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangeEvent>> SearchChangesAsync(ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kapsam dışındaki <b>değişiklik</b> sayısı — olay tarafındaki
    /// <see cref="CountOutOfScopeEventsAsync"/>'in ikizi (T34).
    ///
    /// <para>
    /// Kanıt sağlayıcısı bunu sayamasaydı <c>0</c> dönmek zorunda kalırdı, ve
    /// <c>0</c> "dışarıda ilişkili değişiklik yok" diye okunur. Kök neden başka
    /// grubun cihazındaki bir config değişikliğiyse rapor bunu <b>bilmeden</b>
    /// yanlış sonuca varırdı — sayamadığını sıfır sanmak, bu projedeki en
    /// pahalı hata sınıfı.
    /// </para>
    /// </summary>
    Task<long> CountOutOfScopeChangesAsync(ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ham arşiv nesnesinin bu kapsamdan okunabilir olup olmadığı.
    /// Ham okuma yolu da bu kapıdan geçmeli — yoksa kapsam ayrımı arka kapıdan
    /// delinir (F1 §6.1 madde 3).
    /// </summary>
    Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Değişiklik olayı yazar (T10, RCA'nın F3'teki en güçlü sinyali).
    ///
    /// <para>
    /// Yazma da bu kapıdan geçiyor: çağıran yalnızca <b>kendi kapsamındaki</b> bir
    /// gruba yazabilir. Aksi halde bir ekip başka bir ekibin zaman çizelgesine
    /// olay düşürebilirdi ve RCA yanlış kanıtla çalışırdı.
    /// </para>
    /// </summary>
    Task WriteChangeAsync(ChangeEvent change, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kapsam içindeki kaynak envanteri (T10).
    ///
    /// <para>
    /// Envanter de ClickHouse verisi kadar kapsamlı: bir ekip başka bir ekibin
    /// cihaz listesini görmemeli. Filtreyi uç katmanında elle uygulamak, kapsam
    /// zorlamasını <b>ikinci bir yere</b> koymak olurdu — K17'nin kaçındığı şey
    /// tam olarak bu.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kaynak başına <b>son görülme</b> ve olay sayısı — kaynak sayısından bağımsız,
    /// tek sorgu (T21).
    ///
    /// <para>
    /// <b>Bu metodun varlık sebebi tek bir cümle:</b> "bu kaynaktan en son ne zaman
    /// veri geldi" sorusunun üç yerde ayrı ayrı cevaplanmasını engellemek.
    /// Sessizlik alarmı, envanter listesindeki "son görülme" sütunu ve
    /// <c>/v1/health/pipeline</c> — üçü de <b>buradan</b> okumalı. Üçüncü bir kopya
    /// yazmak, üç farklı zaman kolonu seçimi ve üç farklı kapsam davranışı demek
    /// olurdu; ilk ikisinin ayrışması ancak alarm yanlış tetiklendiğinde fark edilirdi.
    /// </para>
    ///
    /// <para>
    /// Dönen küme <b>yalnızca pencerede verisi olan</b> kaynakları içerir. Hiç
    /// verisi olmayan kaynak burada görünmez — "yokluk" bilgisi envanterle
    /// birleştirilerek elde edilir, çünkü olay tablosu var olmayan bir şeyi
    /// listeleyemez.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window,
        AccessScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Zaman kovalarına bölünmüş sayım — alarm önizlemesinin tek sorgusu (T23).
    ///
    /// <para>
    /// Eşik <b>uygulanmadan</b> dönüyor: önizleme ekranı eşiği değiştirdiğinde
    /// yeni bir sorgu atmasın diye. Kaydırıcıyı sürükleyen bir kullanıcı aksi
    /// hâlde saniyede onlarca sorgu üretirdi ve K16'nın uyardığı "tek kötü kural"
    /// senaryosu kural yazılmadan, sadece yazılırken gerçekleşirdi.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<HistogramBucket>> GetEventHistogramAsync(
        EventHistogramQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default);
}

/// <param name="IsKnownToDispatcher"><c>parser_id</c> bağlı mı — dispatcher kademe 1.</param>
/// <param name="CreatedAt">
/// Envantere girdiği an. Sessizlik alarmının ihtiyacı: henüz hiç veri göndermemiş
/// bir kaynağın "susuyor" sayılıp sayılmayacağı buna bakılarak kararlaştırılıyor —
/// aksi halde her yeni kaynak eklendiği dakika alarm üretirdi.
/// </param>
public sealed record SourceSummary(
    string SourceId,
    string OwnerGroup,
    string? PeerAddress,
    string? Hostname,
    string Vendor,
    string Product,
    string? ParserId,
    string Encoding,
    string SourceClass,
    bool Enabled,
    bool IsKnownToDispatcher,
    DateTimeOffset CreatedAt);

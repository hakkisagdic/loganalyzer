namespace Bizigo.Parsing.Dispatch;

/// <param name="Loaded">Katalogda etkin parser sayısı.</param>
/// <param name="Errors">Yüklenemeyenler; boş değilse bir kısmı dışarıda kalmış.</param>
/// <param name="Notes">
/// Hata olmayan ama <b>görünmesi gereken</b> durumlar. Bugünkü tek örneği:
/// yayınlanmış bir taslağın repodaki dosyayı gölgelemesi.
/// </param>
public sealed record CatalogRefreshOutcome(
    int Loaded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes)
{
    public static CatalogRefreshOutcome Empty { get; } = new(0, [], []);
}

/// <summary>
/// Kataloğun nereden beslendiğini soyutlar.
///
/// <para>
/// Boru hattı kataloğun <b>güncel</b> olmasını istiyor; nereden geldiği onu
/// ilgilendirmiyor. F1'de tek kaynak repodaki dosyalardı; F2'de yayınlanmış
/// taslaklar eklendi (T18). Bu arayüz olmasaydı ingest katmanı taslak deposunu
/// tanımak zorunda kalırdı.
/// </para>
/// </summary>
public interface IParserCatalogSource
{
    Task<CatalogRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default);
}

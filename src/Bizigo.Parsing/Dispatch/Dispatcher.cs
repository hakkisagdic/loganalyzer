using Bizigo.Contracts;
using Bizigo.Parsing.Engine;

namespace Bizigo.Parsing.Dispatch;

public enum DispatchTier
{
    /// <summary>Envanterde <c>source_id → parser_id</c> bağlı. En hızlı ve en doğru yol.</summary>
    InventoryBound = 1,

    /// <summary>Literal ön filtreden geçen adaylardan biri tuttu.</summary>
    Candidate = 2,

    /// <summary>Hiçbiri tutmadı.</summary>
    Unmatched = 3,
}

/// <param name="Tier">Sonucun hangi kademeden geldiği — <c>bound_ratio</c>'nun kaynağı.</param>
/// <param name="Attempts">Kaç parser denendiği. Sıfır değilse ön filtre yeterince daraltmıyor.</param>
public sealed record DispatchResult(ParseResult Result, DispatchTier Tier, int Attempts);

/// <summary>
/// Satırın hangi parser'a gideceğine karar verir (F1 §4.2).
///
/// <para>
/// Kademelerin sırası <b>performans için değil doğruluk için</b>: envanter bağı
/// aynı anda hem en hızlı hem en güvenilir yol, çünkü cihazın ne gönderdiğini
/// tahmin etmek yerine biliyoruz. Literal filtre yalnızca envanteri eksik
/// kaynaklar için bir güvenlik ağı — üretimde trafiğin büyük kısmının oraya
/// düşmesi bir arıza belirtisidir, normal çalışma değil.
/// </para>
/// </summary>
public sealed class Dispatcher(ParserCatalog catalog, DispatchStats stats)
{
    public DispatchResult Dispatch(string body, string? boundParserId)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Anlık görüntü BAŞTA alınıyor: sıcak yeniden yükleme tam bu sırada
        // gerçekleşirse satır tutarlı tek bir katalogla işlenir.
        var snapshot = catalog.Current;

        // Kademe 1 — envanter bağı.
        if (!string.IsNullOrWhiteSpace(boundParserId)
            && snapshot.ByParserId.TryGetValue(boundParserId, out var bound))
        {
            var result = bound.Parse(body);

            if (result.Status != ParseStatus.Failed)
            {
                stats.Record(DispatchTier.InventoryBound, 1);
                return new DispatchResult(result, DispatchTier.InventoryBound, 1);
            }

            // Bağlı parser tutmadı: cihaz yazılımı güncellenmiş olabilir. Sessizce
            // aday taramasına düşüyoruz ama sayaç bunu ayrı tutuyor.
            stats.RecordBoundMiss();
        }

        // Kademe 2 — literal ön filtre.
        var candidates = snapshot.Automaton.Match(body);

        foreach (var index in snapshot.LiteralFree)
        {
            candidates.Add(index);
        }

        if (candidates.Count == 0)
        {
            stats.Record(DispatchTier.Unmatched, 0);
            return new DispatchResult(Unmatched, DispatchTier.Unmatched, 0);
        }

        // Kademe 3 — adaylar specificity sırasıyla; ilk `ok` kazanır.
        // Katalog anlık görüntüsü zaten sıralı olduğu için burada sıralama yok.
        var attempts = 0;
        ParseResult? partial = null;

        foreach (var index in candidates.Order())
        {
            var parser = snapshot.Parsers[index];
            attempts++;

            var result = parser.Parse(body);

            if (result.Status == ParseStatus.Ok)
            {
                stats.Record(DispatchTier.Candidate, attempts);
                return new DispatchResult(result, DispatchTier.Candidate, attempts);
            }

            // Kısmi sonucu aklımızda tutuyoruz: hiçbiri tam tutmazsa, hiç
            // ayrıştırılmamış bir satır yerine kısmi olanı vermek daha iyi.
            partial ??= result.Status == ParseStatus.Partial ? result : null;
        }

        if (partial is not null)
        {
            stats.Record(DispatchTier.Candidate, attempts);
            return new DispatchResult(partial, DispatchTier.Candidate, attempts);
        }

        // Kademe 4 — düşüş. Olay REDDEDİLMEZ: ham arşivde zaten duruyor ve
        // sidecar keşif kuyruğuna bu statüyle giriyor (F1 §9).
        stats.Record(DispatchTier.Unmatched, attempts);
        return new DispatchResult(Unmatched, DispatchTier.Unmatched, attempts);
    }

    private static ParseResult Unmatched { get; } =
        ParseResult.Failure(string.Empty, string.Empty, "Hiçbir parser eşleşmedi.");
}

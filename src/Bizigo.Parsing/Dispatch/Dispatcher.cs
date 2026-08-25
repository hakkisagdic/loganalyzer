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

/// <summary>Zaman aşımına uğrayan bir parser — kimliğiyle.</summary>
///
/// <para>
/// Anahtar biçimi (<c>id@sürüm</c> gibi) <b>bilerek</b> burada kurulmuyor:
/// <see cref="ParserQuarantine"/> kendi anahtarını kendi seçiyor ve dispatcher'ın
/// o seçimi baştan sabitlemesi için bir sebep yok.
/// </para>
public sealed record TimedOutParser(string ParserId, string ParserVersion);

/// <param name="Tier">Sonucun hangi kademeden geldiği — <c>bound_ratio</c>'nun kaynağı.</param>
/// <param name="Attempts">Kaç parser denendiği. Sıfır değilse ön filtre yeterince daraltmıyor.</param>
public sealed record DispatchResult(ParseResult Result, DispatchTier Tier, int Attempts)
{
    /// <summary>
    /// Bu satırda zaman aşımına uğrayan parser'lar — <b>kazanan sonuç bunlardan
    /// biri olmasa bile</b>.
    ///
    /// <para>
    /// Ayrı bir alan olmak zorunda, çünkü <see cref="ParseResult.TimedOut"/>
    /// yalnızca <b>dönen</b> sonucun bayrağı ve zaman aşımına uğrayan aday
    /// çoğu zaman dönmüyor: varsayılan <c>on_failure: fail</c> ile zaman aşımı
    /// <see cref="ParseStatus.Failed"/> üretiyor, dispatcher da <c>Failed</c>
    /// sonucu "bu satır bu parser'a uymadı" diye eleyip bir sonrakine geçiyor.
    /// Bayrak o elemeyle birlikte gidiyordu — sevk edilen kataloğun
    /// <b>tamamında</b>, çünkü 14 grok adımının hiçbiri <c>on_failure</c>
    /// yazmıyor (T05 ölçümü).
    /// </para>
    ///
    /// <para>
    /// <b>Buradaki iş yalnızca bilgiyi taşımak.</b> "Zaman aşımına uğramış bir
    /// aday nasıl ele alınmalı" ayrı bir davranış kararı ve bu listede
    /// verilmiyor: dispatcher'ın kimi seçtiği, hangi sayacı arttırdığı ve
    /// ürettiği statü değişmedi. Karar ancak bu bilgi taşındıktan sonra
    /// ölçülebilir hâle geliyor.
    /// </para>
    /// </summary>
    public IReadOnlyList<TimedOutParser> TimedOutParsers { get; init; } = [];
}

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

        // Zaman aşımına uğrayan parser'lar. Sıcak yolda **tahsisat yok**: liste
        // ilk zaman aşımına kadar `null` kalıyor ve zaman aşımı olağan değil.
        List<TimedOutParser>? timedOut = null;

        // Kademe 1 — envanter bağı.
        if (!string.IsNullOrWhiteSpace(boundParserId)
            && snapshot.ByParserId.TryGetValue(boundParserId, out var bound))
        {
            var result = bound.Parse(body);
            Collect(ref timedOut, result);

            if (result.Status != ParseStatus.Failed)
            {
                stats.Record(DispatchTier.InventoryBound, 1);
                return Finish(result, DispatchTier.InventoryBound, 1, timedOut);
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
            return Finish(Unmatched, DispatchTier.Unmatched, 0, timedOut);
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
            Collect(ref timedOut, result);

            if (result.Status == ParseStatus.Ok)
            {
                stats.Record(DispatchTier.Candidate, attempts);
                return Finish(result, DispatchTier.Candidate, attempts, timedOut);
            }

            // Kısmi sonucu aklımızda tutuyoruz: hiçbiri tam tutmazsa, hiç
            // ayrıştırılmamış bir satır yerine kısmi olanı vermek daha iyi.
            partial ??= result.Status == ParseStatus.Partial ? result : null;
        }

        if (partial is not null)
        {
            stats.Record(DispatchTier.Candidate, attempts);
            return Finish(partial, DispatchTier.Candidate, attempts, timedOut);
        }

        // Kademe 4 — düşüş. Olay REDDEDİLMEZ: ham arşivde zaten duruyor ve
        // sidecar keşif kuyruğuna bu statüyle giriyor (F1 §9).
        stats.Record(DispatchTier.Unmatched, attempts);
        return Finish(Unmatched, DispatchTier.Unmatched, attempts, timedOut);
    }

    /// <summary>
    /// Zaman aşımına uğrayan parser'ı kimliğiyle biriktirir — <b>hangi kademede
    /// olduğuna ve sonucun dönüp dönmeyeceğine bakmadan</b>. Kırık tam buradaydı:
    /// zaman aşımı bilgisi, elenen sonucun içinde eleniyordu.
    ///
    /// <para>
    /// Aynı parser iki kez sayılmıyor. Bağlı parser tutmazsa aday taramasına
    /// düşülüyor ve <b>aynı parser orada yeniden deneniyor</b>; tek satırın iki
    /// zaman aşımı gibi okunması, tüketicinin (karantina) göreceği oranı
    /// şişirirdi. Liste "bu satırda hangi parser'lar zaman aşımına uğradı"
    /// sorusunun cevabı, "kaç kez denendi"nin değil.
    /// </para>
    /// </summary>
    private static void Collect(ref List<TimedOutParser>? timedOut, ParseResult result)
    {
        if (!result.TimedOut)
        {
            return;
        }

        timedOut ??= [];

        foreach (var entry in timedOut)
        {
            if (string.Equals(entry.ParserId, result.ParserId, StringComparison.Ordinal)
                && string.Equals(entry.ParserVersion, result.ParserVersion, StringComparison.Ordinal))
            {
                return;
            }
        }

        timedOut.Add(new TimedOutParser(result.ParserId, result.ParserVersion));
    }

    /// <summary>
    /// Her çıkış aynı yerden geçiyor: beş dönüş noktasından birinde listeyi
    /// eklemeyi unutmak, kırığı sessizce geri getirirdi.
    /// </summary>
    private static DispatchResult Finish(
        ParseResult result,
        DispatchTier tier,
        int attempts,
        List<TimedOutParser>? timedOut) =>
        new(result, tier, attempts) { TimedOutParsers = timedOut ?? (IReadOnlyList<TimedOutParser>)[] };

    private static ParseResult Unmatched { get; } =
        ParseResult.Failure(string.Empty, string.Empty, "Hiçbir parser eşleşmedi.");
}

using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;

namespace Bizigo.Normalization;

/// <param name="Raw">Orijinal baytlarıyla ham kayıt.</param>
/// <param name="Decoded">Kodlama tespiti sonrası metin gövde.</param>
/// <param name="EncodingName">Seçilen kodlama — <c>encoding_detected</c> kolonuna gider.</param>
/// <param name="Source">Envanterden çözülen kimlik ve kapsam.</param>
/// <param name="Parsed">Dispatcher'ın seçtiği parser'ın çıktısı.</param>
/// <param name="Tier">Hangi dispatcher kademesinden geldiği.</param>
/// <param name="TemplateId">
/// Drain3 şablon kimliği (T12) — <b>bilinmiyorsa boş</b>, ki olağan durum bu.
/// Sidecar sıcak yolda olmadığı için değer sidecar'a sorularak değil, daha önce
/// öğrenilmiş imza önbelleğinden geliyor (<c>Bizigo.Ingest.Discovery</c>).
/// Boş olması bir hata değil: F3'ün "ilk görülen imza" korelasyonu zaten
/// yalnızca etiketlenebilmiş olaylar üzerinde çalışıyor.
/// </param>
public sealed record ParsedEvent(
    RawRecord Raw,
    string Decoded,
    string EncodingName,
    ResolvedSource Source,
    ParseResult Parsed,
    DispatchTier Tier,
    string TemplateId = "");

/// <summary>
/// Ayrıştırılmış olayların çıkışı.
///
/// <para>
/// Burada durmasının sebebi bağımlılık yönü: ingest katmanı ClickHouse'u
/// tanımıyor (K17 mimari testi), depolama katmanı da ingest'i tanımak zorunda
/// değil. İkisi de bu sözleşmeye bakıyor.
/// </para>
/// </summary>
public interface IParsedEventSink
{
    ValueTask HandleAsync(IReadOnlyList<ParsedEvent> batch, CancellationToken cancellationToken);
}

public sealed class CountingParsedEventSink : IParsedEventSink
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public ValueTask HandleAsync(IReadOnlyList<ParsedEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Add(ref _count, batch.Count);
        return ValueTask.CompletedTask;
    }
}

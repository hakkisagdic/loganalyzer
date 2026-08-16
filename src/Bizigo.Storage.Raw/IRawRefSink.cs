namespace Bizigo.Storage.Raw;

/// <summary>
/// Yükleyicinin ürettiği <c>event_id → raw_ref</c> eşlemesinin çıkışı.
///
/// <para>
/// <b>Neden bir arayüz:</b> ingest boru hattı ile arşiv yükleyici bilinçli
/// olarak birbirinden bağımsız çalışıyor (F1 §2.3) — biri parse edip ClickHouse'a
/// yazarken diğeri segmentleri arşive taşıyor. Bu yüzden olay satırı yazılırken
/// <c>raw_ref</c> henüz bilinmiyor. Eşlemeyi burada dışarı vermek, T06/T07'nin
/// bu boşluğu yükleyiciyi değiştirmeden kapatmasını sağlıyor.
/// </para>
///
/// <para>
/// F1'de varsayılan uygulama hiçbir şey yapmıyor; olayları ClickHouse'a yazan
/// katman henüz yok. <b>Açık kalem:</b> eşlemenin nerede saklanacağı (ayrı bir
/// ClickHouse indeksi mi, olay satırının sonradan güncellenmesi mi) T07'de
/// karara bağlanmalı.
/// </para>
/// </summary>
public interface IRawRefSink
{
    ValueTask RecordAsync(
        string objectKey,
        IReadOnlyList<RawRefEntry> refs,
        CancellationToken cancellationToken);
}

public sealed class NullRawRefSink : IRawRefSink
{
    public ValueTask RecordAsync(
        string objectKey,
        IReadOnlyList<RawRefEntry> refs,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

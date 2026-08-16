namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Ingest sayaçları. Sağlık ekranının ve T10'daki <c>/healthz</c> ayrıntısının
/// kaynağı; tam bir metrik altyapısı değil, "boru hattı akıyor mu" sorusunun
/// cevabı.
/// </summary>
public sealed class IngestStats
{
    private long _acceptedBatches;
    private long _acceptedRecords;
    private long _rejectedFull;
    private long _rejectedInvalid;
    private long _processedRecords;
    private long _nonUtf8Records;
    private long _declaredEncodingMismatches;

    public long AcceptedBatches => Interlocked.Read(ref _acceptedBatches);
    public long AcceptedRecords => Interlocked.Read(ref _acceptedRecords);
    public long RejectedFull => Interlocked.Read(ref _rejectedFull);
    public long RejectedInvalid => Interlocked.Read(ref _rejectedInvalid);
    public long ProcessedRecords => Interlocked.Read(ref _processedRecords);

    /// <summary>UTF-8 dışında bir kodlamayla çözülen kayıtlar — K4'ün ölçüsü.</summary>
    public long NonUtf8Records => Interlocked.Read(ref _nonUtf8Records);

    /// <summary>
    /// Envanterin/gönderenin bildirdiği kodlamanın tutmadığı kayıtlar. Sıfırdan
    /// büyükse envanterde yanlış bir <c>encoding</c> alanı var demektir.
    /// </summary>
    public long DeclaredEncodingMismatches => Interlocked.Read(ref _declaredEncodingMismatches);

    public void Accepted(int recordCount)
    {
        Interlocked.Increment(ref _acceptedBatches);
        Interlocked.Add(ref _acceptedRecords, recordCount);
    }

    public void RejectFull() => Interlocked.Increment(ref _rejectedFull);

    public void RejectInvalid() => Interlocked.Increment(ref _rejectedInvalid);

    public void Processed(string encodingName, bool declaredHonored)
    {
        Interlocked.Increment(ref _processedRecords);

        if (!string.Equals(encodingName, "utf-8", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _nonUtf8Records);
        }

        if (!declaredHonored)
        {
            Interlocked.Increment(ref _declaredEncodingMismatches);
        }
    }
}

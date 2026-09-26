using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Wal;

/// <summary>WAL kapasitesi doldu — istek kabul edilmemeli (503).</summary>
public sealed class WalFullException(long totalBytes, long limitBytes)
    : InvalidOperationException(string.Create(
        CultureInfo.InvariantCulture,
        $"WAL doldu: {totalBytes} bayt / {limitBytes} bayt sınır."))
{
    public long TotalBytes { get; } = totalBytes;
    public long LimitBytes { get; } = limitBytes;
}

public sealed record WalSegmentInfo(long Sequence, string Path, long Length, bool IsOpen);

public sealed record WalRecoveryReport(int SegmentCount, int FrameCount, long TruncatedBytes)
{
    public IReadOnlyList<string> CorruptSegments { get; init; } = [];
}

/// <summary>
/// Ürünün <b>dayanıklılık sınırı</b> (F1 §2.3).
///
/// <para>
/// Ack, ham batch buraya yazılıp <c>fsync</c> edildikten <b>sonra</b> verilir.
/// Bunun sonucu, riski kabul edilebilir kılan şeydir: RustFS çökse de, parser
/// hata verse de, ClickHouse dolsa da <b>ack'lenmiş hiçbir olay kaybolmaz</b> —
/// en kötü durum "işlenmemiş veri birikti"dir, "veri gitti" değil.
/// </para>
///
/// <para>
/// <b>Bilinen verim kaldıracı:</b> her ekleme ayrı <c>fsync</c> yapıyor. Grup
/// commit (N istek tek fsync'te birleştirilir) bunu katlarca hızlandırır, ama
/// dayanıklılık sınırını inceltir. Ölçmeden yapılmaz; şimdilik en güvenli hal.
/// </para>
/// </summary>
public sealed class WriteAheadLog : IDisposable
{
    private const string SegmentPrefix = "wal-";
    private const string SegmentSuffix = ".log";

    private readonly WalOptions _options;
    private readonly ILogger<WriteAheadLog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IWalDurability _durability;

    private FileStream? _current;
    private long _currentSequence;
    private long _totalBytes;
    private bool _disposed;
    private string? _failure;

    public WriteAheadLog(IOptions<WalOptions> options, ILogger<WriteAheadLog> logger, IWalDurability? durability = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _durability = durability ?? new FileWalDurability();

        System.IO.Directory.CreateDirectory(_options.Directory);
        Recovery = Recover();
        if (Recovery.CorruptSegments.Count > 0) _failure = "WAL corruption quarantined; operator recovery required.";
    }

    /// <summary>Açılışta yapılan kurtarmanın raporu — sağlık ekranında görünür.</summary>
    public WalRecoveryReport Recovery { get; }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public bool IsFull => TotalBytes >= _options.MaxTotalBytes;
    public string? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// Ham batch'i yazar ve diske indirir. Dönüş, çağıranın ack verebileceği andır.
    /// </summary>
    /// <exception cref="WalFullException">Kapasite aşıldı — çağıran 503 dönmeli.</exception>
    public async Task<WalSegmentInfo> AppendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (payload.IsEmpty)
        {
            throw new ArgumentException("Boş batch WAL'a yazılmaz.", nameof(payload));
        }

        var frame = WalFrame.Encode(payload.Span);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_failure is not null) throw new IOException(_failure);
            // Sınır kontrolü kilidin İÇİNDE: dışarıda yapılırsa eşzamanlı istekler
            // sınırı birlikte aşar ve disk dolar.
            if (_totalBytes + frame.Length > _options.MaxTotalBytes)
            {
                throw new WalFullException(_totalBytes, _options.MaxTotalBytes);
            }

            try
            {
                var stream = EnsureSegment(frame.Length);
                await stream.WriteAsync(frame, cancellationToken);
                if (_options.FlushToDisk)
                    await _durability.FlushAsync(stream, cancellationToken);
                else
                    await stream.FlushAsync(cancellationToken);
                Interlocked.Add(ref _totalBytes, frame.Length);
                return new WalSegmentInfo(_currentSequence, stream.Name, stream.Length, IsOpen: true);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                _failure = "WAL append did not complete durably; reopen and recover before further writes.";
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SealAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_failure is not null) throw new IOException(_failure);
            if (_current is null) return;
            _current.Flush(flushToDisk: true);
            _current.Dispose();
            _current = null;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Yazımı kapanmış segmentler — yükleyicinin (T04) işleyeceği birimler.
    /// Açık segment listelenmez: hâlâ yazılıyor.
    /// </summary>
    public IReadOnlyList<WalSegmentInfo> ListSealedSegments()
    {
        var openPath = _current?.Name;

        return EnumerateSegmentFiles()
            .Where(f => !string.Equals(f.Path, openPath, StringComparison.Ordinal))
            .Select(f => new WalSegmentInfo(f.Sequence, f.Path, new FileInfo(f.Path).Length, IsOpen: false))
            .ToArray();
    }

    /// <summary>Segmentteki çerçeveleri sırayla okur. Bozuk çerçevede durur.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> ReadFrames(string path) => ReadFrames(path, strict: false);

    public static IEnumerable<ReadOnlyMemory<byte>> ReadFrames(string path, bool strict)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = 0;

        while (offset < bytes.Length)
        {
            var read = WalFrame.TryDecode(bytes.AsSpan(offset), out var payload);
            if (read == 0)
            {
                if (strict) throw new InvalidDataException("WAL frame integrity failure: " + path);
                yield break;
            }

            yield return payload.ToArray();
            offset += read;
        }
    }

    /// <summary>
    /// Segmenti siler. Çağıran, içeriğin arşive yüklendiğini <b>doğruladıktan</b>
    /// sonra çağırmalı (koruma #3: doğrulama + 48 saat, F1 §7.0).
    /// </summary>
    public void Delete(WalSegmentInfo segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (segment.IsOpen)
        {
            throw new InvalidOperationException("Açık segment silinmez.");
        }

        var length = new FileInfo(segment.Path).Length;
        File.Delete(segment.Path);
        Interlocked.Add(ref _totalBytes, -length);
    }

    private FileStream EnsureSegment(int incomingBytes)
    {
        if (_current is not null && _current.Length + incomingBytes <= _options.MaxSegmentBytes)
        {
            return _current;
        }

        _current?.Flush(flushToDisk: true);
        _current?.Dispose();

        _currentSequence++;
        var path = Path.Combine(
            _options.Directory,
            string.Create(CultureInfo.InvariantCulture, $"{SegmentPrefix}{_currentSequence:D10}{SegmentSuffix}"));

        _current = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });

        return _current;
    }

    /// <summary>
    /// Açılışta bütün segmentleri tarar, ilk bozuk/yarım çerçeveden itibaren
    /// <b>budar</b>.
    ///
    /// <para>
    /// Budama neden doğru: yarım çerçeve, ack verilmemiş bir yazmanın kalıntısıdır
    /// — gönderen onu yeniden gönderecek. Bırakılırsa sonraki yazma bozuk baytların
    /// ardına eklenir ve segment kalıcı olarak okunamaz hale gelir.
    /// </para>
    /// </summary>
    private WalRecoveryReport Recover()
    {
        var segments = EnumerateSegmentFiles();
        var frames = 0;
        long truncated = 0;
        long total = 0;
        var corrupt = new List<string>();

        foreach (var (sequence, path) in segments)
        {
            var bytes = File.ReadAllBytes(path);
            var offset = 0;

            while (offset < bytes.Length)
            {
                var read = WalFrame.TryDecode(bytes.AsSpan(offset), out _);
                if (read == 0)
                {
                    break;
                }

                frames++;
                offset += read;
            }

            if (offset < bytes.Length)
            {
                if (_options.StrictRecovery && !IncompleteTail(bytes.AsSpan(offset)))
                {
                    // Preserve the entire file, including every acknowledged suffix.
                    // Reopening reports the same fault; no fresh append can hide it.
                    corrupt.Add(path);
                    total += bytes.Length;
                    _currentSequence = Math.Max(_currentSequence, sequence);
                    _logger.LogError("WAL segment {Segment} quarantined in place at offset {Offset}.", path, offset);
                    continue;
                }
                truncated += bytes.Length - offset;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(offset);
                stream.Flush(flushToDisk: true);

                _logger.LogWarning(
                    "WAL segmenti {Segment} budandı: {Bytes} bayt yarım çerçeve atıldı.",
                    path,
                    bytes.Length - offset);
            }

            total += offset;
            _currentSequence = Math.Max(_currentSequence, sequence);
        }

        _totalBytes = total;

        if (frames > 0 || truncated > 0)
        {
            _logger.LogInformation(
                "WAL kurtarma: {Segments} segment, {Frames} çerçeve, {Truncated} bayt budandı.",
                segments.Count,
                frames,
                truncated);
        }

        return new WalRecoveryReport(segments.Count, frames, truncated) { CorruptSegments = corrupt };
    }

    private static bool IncompleteTail(ReadOnlySpan<byte> bytes)
    {
        // A valid later frame proves this is not merely an incomplete EOF.
        // In particular a damaged length header must not erase an ACKed suffix.
        for (var offset = WalFrame.HeaderBytes; offset <= bytes.Length - WalFrame.HeaderBytes; offset++)
            if (WalFrame.TryDecode(bytes[offset..], out _) > 0) return false;
        if (bytes.Length < 4) return true;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) != WalFrame.Magic) return false;
        if (bytes.Length < WalFrame.HeaderBytes) return true;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(4, 4));
        return length <= int.MaxValue - WalFrame.HeaderBytes && length > bytes.Length - WalFrame.HeaderBytes;
    }

    private IReadOnlyList<(long Sequence, string Path)> EnumerateSegmentFiles() =>
        System.IO.Directory
            .EnumerateFiles(_options.Directory, SegmentPrefix + "*" + SegmentSuffix)
            .Select(path => (Sequence: ParseSequence(path), Path: path))
            .Where(x => x.Sequence > 0)
            .OrderBy(x => x.Sequence)
            .ToArray();

    private static long ParseSequence(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.AsSpan());
        var digits = name[SegmentPrefix.Length..];
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current?.Flush(flushToDisk: true);
        _current?.Dispose();
        _writeLock.Dispose();
    }
}

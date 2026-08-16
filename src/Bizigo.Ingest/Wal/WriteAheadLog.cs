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

public sealed record WalRecoveryReport(int SegmentCount, int FrameCount, long TruncatedBytes);

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

    private FileStream? _current;
    private long _currentSequence;
    private long _totalBytes;
    private bool _disposed;

    public WriteAheadLog(IOptions<WalOptions> options, ILogger<WriteAheadLog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        System.IO.Directory.CreateDirectory(_options.Directory);
        Recovery = Recover();
    }

    /// <summary>Açılışta yapılan kurtarmanın raporu — sağlık ekranında görünür.</summary>
    public WalRecoveryReport Recovery { get; }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public bool IsFull => TotalBytes >= _options.MaxTotalBytes;

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
            // Sınır kontrolü kilidin İÇİNDE: dışarıda yapılırsa eşzamanlı istekler
            // sınırı birlikte aşar ve disk dolar.
            if (_totalBytes + frame.Length > _options.MaxTotalBytes)
            {
                throw new WalFullException(_totalBytes, _options.MaxTotalBytes);
            }

            var stream = EnsureSegment(frame.Length);
            await stream.WriteAsync(frame, cancellationToken);

            if (_options.FlushToDisk)
            {
                // flushToDisk: true — işletim sistemi önbelleği yetmez, ack veriyoruz.
                stream.Flush(flushToDisk: true);
            }
            else
            {
                await stream.FlushAsync(cancellationToken);
            }

            Interlocked.Add(ref _totalBytes, frame.Length);
            return new WalSegmentInfo(_currentSequence, stream.Name, stream.Length, IsOpen: true);
        }
        finally
        {
            _writeLock.Release();
        }
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
    public static IEnumerable<ReadOnlyMemory<byte>> ReadFrames(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = 0;

        while (offset < bytes.Length)
        {
            var read = WalFrame.TryDecode(bytes.AsSpan(offset), out var payload);
            if (read == 0)
            {
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

        return new WalRecoveryReport(segments.Count, frames, truncated);
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

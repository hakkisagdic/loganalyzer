using System.Text;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>
/// T03 kabul kriteri: <c>kill -9</c> altında ack'lenmiş hiçbir olay kaybolmuyor.
///
/// <para>
/// Süreci gerçekten öldürmek yerine <b>sonucunu</b> üretiyoruz: yarım yazılmış
/// çerçeve. Aradaki fark, testin saniyeler yerine milisaniyeler sürmesi ve
/// yarım yazmanın tam olarak istenen baytta olmasını sağlayabilmek.
/// </para>
/// </summary>
public sealed class WriteAheadLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bizigo-wal-" + Guid.NewGuid().ToString("N"));

    private WriteAheadLog Open(Action<WalOptions>? configure = null)
    {
        var options = new WalOptions { Directory = _directory };
        configure?.Invoke(options);
        return new WriteAheadLog(Options.Create(options), NullLogger<WriteAheadLog>.Instance);
    }

    private static ReadOnlyMemory<byte> Payload(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Yazilan_cerceve_aynen_geri_okunuyor()
    {
        using var wal = Open();

        var segment = await wal.AppendAsync(Payload("merhaba dünya"), TestContext.Current.CancellationToken);

        var frames = WriteAheadLog.ReadFrames(segment.Path).ToArray();
        Assert.Single(frames);
        Assert.Equal("merhaba dünya", Encoding.UTF8.GetString(frames[0].Span));
    }

    [Fact]
    public async Task Cok_sayida_cerceve_sirayla_okunuyor()
    {
        using var wal = Open();

        for (var i = 0; i < 50; i++)
        {
            await wal.AppendAsync(
                Payload($"kayıt-{i}"),
                TestContext.Current.CancellationToken);
        }

        var frames = Directory
            .GetFiles(_directory)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(WriteAheadLog.ReadFrames)
            .Select(f => Encoding.UTF8.GetString(f.Span))
            .ToArray();

        Assert.Equal(50, frames.Length);
        Assert.Equal("kayıt-0", frames[0]);
        Assert.Equal("kayıt-49", frames[49]);
    }

    [Fact]
    public async Task Yarim_yazma_budaniyor_onceki_cerceveler_duruyor()
    {
        string path;

        using (var wal = Open())
        {
            await wal.AppendAsync(Payload("birinci"), TestContext.Current.CancellationToken);
            var segment = await wal.AppendAsync(Payload("ikinci"), TestContext.Current.CancellationToken);
            path = segment.Path;
        }

        // kill -9 taklidi: son çerçevenin gövdesi yarıda kesilmiş.
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path, bytes[..^3], TestContext.Current.CancellationToken);

        using var recovered = Open();

        Assert.Equal(1, recovered.Recovery.FrameCount);
        Assert.True(recovered.Recovery.TruncatedBytes > 0);

        var frames = WriteAheadLog.ReadFrames(path)
            .Select(f => Encoding.UTF8.GetString(f.Span))
            .ToArray();

        Assert.Equal(["birinci"], frames);
    }

    [Fact]
    public async Task Budamadan_sonra_yazmaya_devam_edilebiliyor()
    {
        string path;

        using (var wal = Open())
        {
            var segment = await wal.AppendAsync(Payload("saglam"), TestContext.Current.CancellationToken);
            path = segment.Path;
        }

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            path,
            [.. bytes, .. new byte[] { 0x42, 0x5A, 0x47 }],
            TestContext.Current.CancellationToken);

        using var recovered = Open();
        await recovered.AppendAsync(Payload("yeni"), TestContext.Current.CancellationToken);

        // Yeni çerçeve, budanmış konuma yazılmalı — çöp baytların ardına DEĞİL.
        var all = Directory
            .GetFiles(_directory)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(WriteAheadLog.ReadFrames)
            .Select(f => Encoding.UTF8.GetString(f.Span))
            .ToArray();

        Assert.Equal(["saglam", "yeni"], all);
    }

    [Fact]
    public async Task Kapasite_asilinca_WalFullException_atiliyor()
    {
        using var wal = Open(o => o.MaxTotalBytes = 64);

        await wal.AppendAsync(Payload(new string('a', 40)), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<WalFullException>(
            () => wal.AppendAsync(Payload(new string('b', 40)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Segment_boyutu_asilinca_yeni_segment_aciliyor()
    {
        using var wal = Open(o => o.MaxSegmentBytes = 64);

        await wal.AppendAsync(Payload(new string('a', 40)), TestContext.Current.CancellationToken);
        await wal.AppendAsync(Payload(new string('b', 40)), TestContext.Current.CancellationToken);

        Assert.Equal(2, Directory.GetFiles(_directory).Length);

        // Kapanmış segment yükleyiciye görünür; açık olan görünmez.
        Assert.Single(wal.ListSealedSegments());
    }

    [Fact]
    public async Task Silinen_segmentin_boyutu_toplam_bayttan_dusuluyor()
    {
        using var wal = Open(o => o.MaxSegmentBytes = 64);

        await wal.AppendAsync(Payload(new string('a', 40)), TestContext.Current.CancellationToken);
        await wal.AppendAsync(Payload(new string('b', 40)), TestContext.Current.CancellationToken);

        var before = wal.TotalBytes;
        var sealedSegment = wal.ListSealedSegments().Single();
        wal.Delete(sealedSegment);

        Assert.True(wal.TotalBytes < before);
        Assert.False(File.Exists(sealedSegment.Path));
    }

    [Fact]
    public async Task Yeniden_acilista_toplam_bayt_geri_hesaplaniyor()
    {
        long expected;

        using (var wal = Open())
        {
            await wal.AppendAsync(Payload("bir"), TestContext.Current.CancellationToken);
            await wal.AppendAsync(Payload("iki"), TestContext.Current.CancellationToken);
            expected = wal.TotalBytes;
        }

        using var reopened = Open();

        // Sayaç sıfırlanırsa kapasite kontrolü anlamsızlaşır ve disk sessizce dolar.
        Assert.Equal(expected, reopened.TotalBytes);
    }

    [Fact]
    public async Task Bos_batch_reddediliyor()
    {
        using var wal = Open();

        await Assert.ThrowsAsync<ArgumentException>(
            () => wal.AppendAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

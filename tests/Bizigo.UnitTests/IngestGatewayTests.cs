using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>
/// F1 §2.3. Buradaki asıl iddia sıralamayla ilgili: <b>WAL'a yazılmadan ack
/// verilmiyor</b> ve <b>WAL dolduğunda 503 dönüyor</b>. İkisi de veri kaybının
/// önündeki tek engel.
/// </summary>
public sealed class IngestGatewayTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bizigo-gw-" + Guid.NewGuid().ToString("N"));

    private WriteAheadLog? _wal;

    private IngestGateway Build(Action<WalOptions>? configureWal = null, int channelCapacity = 64)
    {
        var walOptions = new WalOptions { Directory = _directory };
        configureWal?.Invoke(walOptions);

        _wal = new WriteAheadLog(Options.Create(walOptions), NullLogger<WriteAheadLog>.Instance);

        return new IngestGateway(
            new OtlpLogsDecoder(),
            _wal,
            new IngestChannel(Options.Create(new IngestOptions { ChannelCapacity = channelCapacity })),
            new IngestStats(),
            Options.Create(walOptions),
            NullLogger<IngestGateway>.Instance);
    }

    private static byte[] Request(params string[] bodies)
    {
        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    scopeLogs = new[]
                    {
                        new
                        {
                            logRecords = bodies
                                .Select(b => new { body = new { stringValue = b } })
                                .ToArray(),
                        },
                    },
                },
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    [Fact]
    public async Task Kabul_edilen_batch_WAL_a_yazilmis_oluyor()
    {
        var gateway = Build();

        var result = await gateway.AcceptAsync(
            Request("satır-1", "satır-2"),
            OtlpLogsDecoder.JsonContentType,
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal(2, result.RecordCount);

        // Ack verildiyse veri diskte olmalı — testin bütün amacı bu.
        var lines = Directory
            .GetFiles(_directory)
            .SelectMany(WriteAheadLog.ReadFrames)
            .SelectMany(f => Encoding.UTF8.GetString(f.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        Assert.Equal(2, lines.Length);

        var restored = lines.Select(l => RawRecordCodec.Read(Encoding.UTF8.GetBytes(l))).ToArray();
        Assert.Equal("satır-1", Encoding.UTF8.GetString(restored[0].Body.Span));
        Assert.Equal("satır-2", Encoding.UTF8.GetString(restored[1].Body.Span));
    }

    [Fact]
    public async Task WAL_dolunca_503_ve_Retry_After_donuyor()
    {
        var gateway = Build(o =>
        {
            o.MaxTotalBytes = 32;
            o.RetryAfterSeconds = 7;
        });

        var result = await gateway.AcceptAsync(
            Request("çok uzun bir satır olsun ki kapasiteyi aşsın"),
            OtlpLogsDecoder.JsonContentType,
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Full, result.Outcome);
        Assert.Equal(7, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Bozuk_govde_400_veriyor_ve_WAL_a_yazilmiyor()
    {
        var gateway = Build();

        var result = await gateway.AcceptAsync(
            "{bozuk"u8.ToArray(),
            OtlpLogsDecoder.JsonContentType,
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Invalid, result.Outcome);

        // Çözülemeyen gövde yeniden denense de çözülmez; WAL'ı kirletmemeli.
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Bos_export_kabul_ediliyor_ama_WAL_a_yazilmiyor()
    {
        var gateway = Build();

        var result = await gateway.AcceptAsync(
            """{"resourceLogs":[]}"""u8.ToArray(),
            OtlpLogsDecoder.JsonContentType,
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal(0, result.RecordCount);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WAL_yuku_NDJSON_ve_satir_basina_bir_kayit()
    {
        var gateway = Build();

        await gateway.AcceptAsync(
            Request("a", "b", "c"),
            OtlpLogsDecoder.JsonContentType,
            TestContext.Current.CancellationToken);

        var frame = Directory.GetFiles(_directory).SelectMany(WriteAheadLog.ReadFrames).Single();
        var text = Encoding.UTF8.GetString(frame.Span);

        // Yükleyici (T04) bu yükü dönüştürmeden arşive kopyalayabilmeli.
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _wal?.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

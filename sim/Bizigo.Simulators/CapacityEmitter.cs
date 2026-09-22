using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Bizigo.Capacity;

namespace Bizigo.Simulators;

public enum CapacityPayloadMode
{
    Raw = 0,
    Tagged = 1,
}

/// <summary>B01 kapasite koşumunun taşıma seçenekleri.</summary>
public sealed record CapacityEmitOptions
{
    public required string RunId { get; init; }
    public required PaceProfile Pace { get; init; }
    public int? MaxLines { get; init; }
    public CapacityPayloadMode PayloadMode { get; init; } = CapacityPayloadMode.Tagged;
    public int Connections { get; init; } = 1;
    public int BatchSize { get; init; } = 1;
    public int? Port { get; init; }
    public bool? GeneratorOnSameHost { get; init; }
}

/// <summary>
/// Taşıma sonucu ile B02'nin tüketeceği manifest aynı kayıtta. Sayıyı
/// hükümsüz döndüren ikinci bir API yoktur.
/// </summary>
public sealed record CapacityEmitResult(EmitResult Transport, CapacityRunManifest Manifest);

/// <summary>
/// B01'in gerçek yük üreteci. <see cref="SyslogEmitter"/> yalnızca satır
/// sadakatini sağlar; hız, toplu yazma, bağlantı havuzu ve manifest burada.
/// </summary>
public static class CapacityEmitter
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static async Task<CapacityEmitResult> EmitAsync(
        SimulatorProfile profile,
        string repositoryRoot,
        string host,
        CapacityEmitOptions options,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        Validate(options);

        if (profile.Syslog is null || profile.Syslog.Samples.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{profile.Id}' profilinin syslog yüzeyi yok. Basılacak örnek tanımlanmamış.");
        }

        var lines = SyslogEmitter.ReadSamples(profile, repositoryRoot);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException($"'{profile.Id}' örnek dosyalarında hiç satır yok.");
        }

        var time = timeProvider ?? TimeProvider.System;
        var encoding = SyslogEmitter.ResolveEncoding(profile.Encoding);
        var pacer = new TokenBucketPacer(options.Pace, time);
        var udp = string.Equals(profile.Syslog.Transport, "udp", StringComparison.OrdinalIgnoreCase);
        var port = options.Port ?? (udp ? 5141 : 5140);

        var tcp = new List<TcpClient>();
        var udpClients = new List<UdpClient>();

        try
        {
            if (udp)
            {
                for (var i = 0; i < options.Connections; i++)
                {
                    var client = new UdpClient();
                    client.Connect(host, port);
                    udpClients.Add(client);
                }
            }
            else
            {
                for (var i = 0; i < options.Connections; i++)
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(host, port, cancellationToken);
                    tcp.Add(client);
                }
            }

            var digests = new List<string>();
            var pending = new List<byte[]>(options.BatchSize);
            long bytes = 0;
            var connection = 0;

            while (!pacer.Finished
                && (options.MaxLines is null || digests.Count < options.MaxLines.Value))
            {
                var delay = pacer.TryAcquire();

                if (delay > TimeSpan.Zero)
                {
                    if (pending.Count > 0)
                    {
                        bytes += await FlushAsync(
                            pending, tcp, udpClients, connection++, cancellationToken);
                    }

                    await Task.Delay(delay, time, cancellationToken);
                    continue;
                }

                var sequence = digests.Count;
                var wire = SyslogEmitter.WireLine(lines[sequence % lines.Count], time.GetUtcNow());

                if (options.PayloadMode == CapacityPayloadMode.Tagged)
                {
                    wire = Tag(wire, options.RunId, sequence, time.GetUtcNow());
                }

                var body = encoding.GetBytes(wire);
                digests.Add(CapacityRunManifest.Digest(body));

                var framed = new byte[body.Length + NewLine.Length];
                body.CopyTo(framed, 0);
                NewLine.CopyTo(framed, body.Length);
                pending.Add(framed);

                // UDP'de bir kayıt = bir datagram. TCP toplu yazmayı gerçekten
                // kullanır; bir batch tek WriteAsync çağrısıdır.
                var flushAt = udp ? 1 : options.BatchSize;
                if (pending.Count >= flushAt)
                {
                    bytes += await FlushAsync(
                        pending, tcp, udpClients, connection++, cancellationToken);
                }
            }

            if (pending.Count > 0)
            {
                bytes += await FlushAsync(pending, tcp, udpClients, connection, cancellationToken);
            }

            foreach (var client in tcp)
            {
                await client.GetStream().FlushAsync(cancellationToken);
            }

            // Hüküm FIN bekleme bütçesinden ÖNCE alınır; o yarım saniye ağ
            // sadakati içindir, üretecin EPS'si değildir.
            var attainment = GeneratorAttainment.From(pacer);

            foreach (var client in tcp)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            if (tcp.Count > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), time, cancellationToken);
            }

            var manifest = new CapacityRunManifest(
                options.RunId,
                options.Pace.Name,
                attainment,
                digests,
                options.GeneratorOnSameHost);

            return new CapacityEmitResult(
                new EmitResult(digests.Count, bytes, attainment.Elapsed),
                manifest);
        }
        finally
        {
            foreach (var client in tcp)
            {
                client.Dispose();
            }

            foreach (var client in udpClients)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Tagged replay sözleşmesi; raw mod bu metoda hiç uğramaz.</summary>
    public static string Tag(
        string wireLine,
        string runId,
        long sequence,
        DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(wireLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        var sendNanoseconds = sentAt.ToUnixTimeMilliseconds() * 1_000_000L;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{wireLine} bizigo_run_id=\"{runId}\" bizigo_seq={sequence} bizigo_send_ns={sendNanoseconds}");
    }

    private static async Task<long> FlushAsync(
        List<byte[]> pending,
        IReadOnlyList<TcpClient> tcp,
        IReadOnlyList<UdpClient> udp,
        int connection,
        CancellationToken cancellationToken)
    {
        var index = connection % Math.Max(tcp.Count, udp.Count);
        var size = pending.Sum(static payload => payload.Length);

        if (udp.Count > 0)
        {
            foreach (var payload in pending)
            {
                await udp[index].SendAsync(payload, cancellationToken);
            }
        }
        else
        {
            var batch = new byte[size];
            var offset = 0;

            foreach (var payload in pending)
            {
                payload.CopyTo(batch, offset);
                offset += payload.Length;
            }

            await tcp[index].GetStream().WriteAsync(batch, cancellationToken);
        }

        pending.Clear();
        return size;
    }

    private static void Validate(CapacityEmitOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RunId)
            || options.RunId.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "RunId yalnızca ASCII harf, rakam, nokta, tire ve alt çizgi taşıyabilir.",
                nameof(options));
        }

        if (options.MaxLines is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxLines sıfırdan büyük olmalı.");
        }

        if (options.Connections is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Connections 1..256 aralığında olmalı.");
        }

        if (options.BatchSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize 1..10000 aralığında olmalı.");
        }

        if (options.Port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port 1..65535 aralığında olmalı.");
        }
    }
}

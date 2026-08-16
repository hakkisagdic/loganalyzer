using System.Threading.Channels;
using Bizigo.Contracts;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Kapı ile işçiler arasındaki sınırlı kanal.
///
/// <para>
/// <see cref="BoundedChannelFullMode.Wait"/> bilinçli: <c>DropWrite</c> olsaydı
/// yük altında veri <b>sessizce</b> düşerdi ve bunu fark etmenin bir yolu
/// olmazdı. Beklemek yavaşlatır, düşürmek yalan söyler.
/// </para>
/// </summary>
public sealed class IngestChannel
{
    private readonly Channel<IReadOnlyList<RawRecord>> _channel;

    public IngestChannel(IOptions<IngestOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = Channel.CreateBounded<IReadOnlyList<RawRecord>>(
            new BoundedChannelOptions(options.Value.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public ChannelWriter<IReadOnlyList<RawRecord>> Writer => _channel.Writer;

    public ChannelReader<IReadOnlyList<RawRecord>> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}

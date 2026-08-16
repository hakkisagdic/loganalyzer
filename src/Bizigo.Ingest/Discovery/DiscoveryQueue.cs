using System.Threading.Channels;

namespace Bizigo.Ingest.Discovery;

/// <param name="SourceClass">Miner anahtarı — kaynak sınıfı başına ayrı miner (K14).</param>
/// <param name="Signature">Yerel maskeleme sonucu; önbellek anahtarı.</param>
/// <param name="Text">Ham gövde; sidecar kendi maskelemesini uyguluyor.</param>
public sealed record DiscoveryItem(string SourceClass, string Signature, string Text);

/// <summary>
/// Keşif kuyruğu: <b>sınırlı kapasite, dolunca düşür</b> (F1 §9).
///
/// <para>
/// Kanal <see cref="BoundedChannelFullMode.Wait"/> ile kuruluyor ama yazma
/// daima <c>TryWrite</c> ile yapılıyor — dolu kanalda <c>TryWrite</c> beklemez,
/// <c>false</c> döner. <c>DropWrite</c> kullanılmıyor çünkü o sessizce
/// düşürüyor ve <b>düşen sayılamıyor</b>; sayılamayan bir düşüş, olmayan bir
/// düşüş gibi görünür.
/// </para>
/// </summary>
public sealed class DiscoveryQueue
{
    private readonly Channel<DiscoveryItem> _channel;
    private readonly DiscoveryStats _stats;

    public DiscoveryQueue(SidecarOptions options, DiscoveryStats stats)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Sidecar kuyruğu sınırsız olamaz: sınırsız kuyruk, ingest'i bloklamak yerine belleği tüketir.");
        }

        _stats = stats;
        _channel = Channel.CreateBounded<DiscoveryItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<DiscoveryItem> Reader => _channel.Reader;

    /// <summary>Kuyruk doluysa <c>false</c> döner ve <b>beklemez</b>.</summary>
    public bool TryEnqueue(DiscoveryItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            _stats.Enqueue();
            return true;
        }

        _stats.DropQueueFull();
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}

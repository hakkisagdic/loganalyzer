using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Kısmi batch'leri süre dolunca yazar.
///
/// <para>
/// Bu servis olmadan, düşük trafikli bir kurulumda son birkaç yüz satır bir
/// sonraki batch dolana kadar bellekte bekler ve sorgularda <b>görünmez</b>.
/// "Log geldi ama aramada çıkmıyor" şikâyetinin en sık sebebi budur.
/// </para>
/// </summary>
public sealed class EventSinkFlushService(
    ClickHouseEventSink sink,
    IOptions<EventSinkOptions> options,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly EventSinkOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await sink.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Kapanışta elde kalanı yaz: aksi halde her yeniden başlatma küçük bir
        // replay borcu bırakırdı.
        await sink.FlushAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

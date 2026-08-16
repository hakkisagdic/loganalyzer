using Bizigo.ControlPlane;
using Bizigo.Parsing;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Parser kataloğunu ve envanter anlık görüntüsünü güncel tutar.
///
/// <para>
/// Açılışta <b>bir kez senkron</b> yükleniyor: boru hattı parser'sız ayağa
/// kalkarsa ilk saniyelerin trafiği sebepsiz yere <c>failed</c> olur ve bu,
/// ham arşiv sayesinde kaybolmasa bile gereksiz replay işi çıkarır.
/// </para>
///
/// <para>
/// Sonrası periyodik. Yeniden yükleme atomik olduğu için koşan boru hattı
/// etkilenmiyor (<see cref="ParserCatalog"/>).
/// </para>
/// </summary>
public sealed class CatalogRefreshService(
    ParserCatalog catalog,
    ParserCompiler compiler,
    SourceDirectory sources,
    IOptions<ParsingOptions> options,
    ILogger<CatalogRefreshService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly ParsingOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.InventoryRefreshInterval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sources.RefreshAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Envanter okunamazsa eski anlık görüntü kullanılmaya devam eder;
            // Postgres kesintisi ingest'i durdurmamalı.
            logger.LogError(ex, "Envanter tazelenemedi; önceki anlık görüntü kullanılıyor.");
        }

        try
        {
            var report = catalog.LoadFromDirectory(_options.ParserDirectory, compiler);

            foreach (var error in report.Errors)
            {
                logger.LogError("Parser yüklenemedi: {Error}", error);
            }

            if (report.Loaded > 0)
            {
                logger.LogInformation("Parser kataloğu: {Loaded} parser yüklü.", report.Loaded);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Parser kataloğu yüklenemedi; önceki katalog kullanılıyor.");
        }
    }
}

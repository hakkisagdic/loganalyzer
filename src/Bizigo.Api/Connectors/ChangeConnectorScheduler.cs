using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Connectors;

public sealed class ChangeConnectorOptions
{
    public const string SectionName = "Changes:Connectors";

    public bool Enabled { get; set; } = true;

    /// <summary>Zamanlayıcının tur aralığı — connector'ın kendi aralığı değil.</summary>
    public TimeSpan TurnInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bir turda en fazla kaç connector koşacak.</summary>
    public int MaxPerTurn { get; set; } = 10;

    /// <summary>
    /// Webhook teslimat kayıtlarının ve connector çalışma geçmişinin saklama
    /// süresi.
    ///
    /// <para>
    /// <b>Tek politika, iki tablo</b> (T24'ün açık bıraktığı karar): iki ayrı
    /// saklama süresi tasarlamak, altı ay sonra iki farklı temizlik işi ve iki
    /// farklı sürpriz demekti. İkisi de aynı soruya hizmet ediyor — "bu kaynak
    /// ne zaman ne yaptı" — ve aynı anda eskiyorlar.
    /// </para>
    ///
    /// <para>
    /// 90 gün: idempotans penceresi için fazlasıyla yeterli (sağlayıcılar
    /// saatler içinde yeniden dener) ve "geçen çeyrek bu connector kaç kez
    /// düştü" sorusunu hâlâ cevaplıyor.
    /// </para>
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Temizlik turu aralığı. Saklama süresine göre seyrek olması yeterli.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Aynı anda kaç cihaza bağlanılabileceği (T26 kabul kriteri: "çekim
    /// maliyeti sınırlı").
    ///
    /// <para>
    /// Yüzlerce cihaza aynı anda SSH açmak iki tarafı da yorar: bizde soket ve
    /// iş parçacığı, cihazda yönetim CPU'su. İzlediğimiz cihazı yormak,
    /// izlemenin kendisini bir arıza sebebine çevirir.
    /// </para>
    /// </summary>
    public int MaxDeviceConcurrency { get; set; } = 8;
}

/// <summary>
/// Vadesi gelmiş connector'ları koşturan arka plan işi (T25 kabul kriteri:
/// "pasif connector çalışmıyor; etkinleştirildiğinde zamanlayıcı devralıyor").
///
/// <para>
/// <b>Tur doğrudan çağrılabiliyor</b> (<see cref="RunTurnAsync"/>) ve testler
/// onu çağırıyor — F1'in en pahalı dersi buydu: arka plan görevini başlatıp
/// etkiyi duvar saatiyle yoklayan test, yüklü makinede sağlıklı kodu düşürüyor
/// ve paketi altı buçuk dakikaya çıkarıyordu. Beklemek bir sinyalle
/// değiştirilemiyorsa denklemden çıkarılmalı.
/// </para>
///
/// <para>
/// Yalnızca <b>zamanlanabilir</b> tipler buraya düşüyor. Webhook push, elle
/// giriş insan eliyle; ikisinin de <c>NextRunAt</c>'i null ve sorgunun dışında
/// kalıyorlar.
/// </para>
/// </summary>
public sealed class ChangeConnectorScheduler(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ChangeConnectorService service,
    IEnumerable<IChangeConnectorRunner> runners,
    ChangeConnectorOptions options,
    TimeProvider clock,
    ILogger<ChangeConnectorScheduler> log) : BackgroundService
{
    private readonly Dictionary<ChangeConnectorType, IChangeConnectorRunner> _runners =
        runners.ToDictionary(r => r.ConnectorType);

    /// <summary>Tur sayacı — "zamanlayıcı gerçekten dönüyor mu" sorusunun cevabı.</summary>
    public long Turns { get; private set; }

    public long Executed { get; private set; }

    public long Failed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            log.LogInformation("Connector zamanlayıcısı kapalı.");
            return;
        }

        using var timer = new PeriodicTimer(options.TurnInterval, clock);

        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunTurnAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Tek turun hatası döngüyü durdurmuyor: duran bir zamanlayıcının
                // tek belirtisi, verinin sessizce birikmemesi olurdu.
                log.LogError(ex, "Connector zamanlayıcı turu hata verdi.");
            }
        }
    }

    public async Task<int> RunTurnAsync(CancellationToken cancellationToken = default)
    {
        Turns++;

        var now = clock.GetUtcNow();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var due = await db.ChangeConnectors
            .Where(c => c.Enabled && c.NextRunAt != null && c.NextRunAt <= now)
            .OrderBy(c => c.NextRunAt)
            .Take(Math.Clamp(options.MaxPerTurn, 1, 100))
            .ToListAsync(cancellationToken);

        foreach (var connector in due)
        {
            await ExecuteOneAsync(db, connector, cancellationToken);
        }

        return due.Count;
    }

    private async Task ExecuteOneAsync(
        ControlPlaneDbContext db,
        ChangeConnectorEntity connector,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        var context = service.BuildContext(connector);

        ConnectorRunResult result;

        if (!_runners.TryGetValue(connector.ConnectorType, out var runner))
        {
            // Kaydetme kapısı bunu zaten engelliyor; buraya düşmesi ancak
            // runner'ı kaldırılmış bir dağıtımda mümkün. Sessizce atlamak, bir
            // connector'ın hiç koşmadığını görünmez kılardı.
            result = new ConnectorRunResult(
                false, 0, $"{connector.ConnectorType} tipi için toplayıcı kayıtlı değil.");
        }
        else
        {
            try
            {
                result = await runner.RunAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                result = new ConnectorRunResult(false, 0, ex.Message);
            }
        }

        // Runner ne döndürdüyse döndürsün, dışarı çıkan metin redaksiyondan
        // GEÇİYOR. Bu, runner yazarının dikkatine bırakılamayacak kadar sık
        // kırılan bir yer.
        var error = ChangeConnectorService.Clean(result.Error, context.Credential, connector);

        var state = result.Ok ? ConnectorRunState.Succeeded : ConnectorRunState.Failed;
        var finishedAt = clock.GetUtcNow();

        db.ChangeConnectorRuns.Add(new ChangeConnectorRunEntity
        {
            ConnectorId = connector.Id,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            State = state,
            ChangesWritten = result.ChangesWritten,
            Error = Truncate(error),
        });

        connector.LastRunAt = finishedAt;
        connector.LastRunState = state;
        connector.LastError = Truncate(error);

        // Bir sonraki vade ŞİMDİ'den itibaren, planlanan vadeden değil: uzun
        // süren bir koşumdan sonra geçmişte kalmış vadeler birikip connector'ı
        // arka arkaya koşturmaya başlardı.
        connector.NextRunAt = connector.IntervalSeconds is > 0
            ? finishedAt.AddSeconds(connector.IntervalSeconds.Value)
            : null;

        await db.SaveChangesAsync(cancellationToken);

        Executed++;

        if (!result.Ok)
        {
            Failed++;
            log.LogWarning("Connector koşumu başarısız: {Slug} — {Error}", connector.Slug, error);
        }
    }

    /// <summary>Kolon 1024; kırpma sessiz olmasın diye sonuna işaret koyuluyor.</summary>
    private static string Truncate(string text) =>
        text.Length <= 1024 ? text : text[..1021] + "...";
}

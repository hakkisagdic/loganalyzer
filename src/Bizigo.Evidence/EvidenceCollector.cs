using System.Diagnostics;
using Bizigo.Contracts;
using Microsoft.Extensions.Logging;

namespace Bizigo.Evidence;

/// <summary>
/// Kanıt toplayıcı — sağlayıcıları koşturan ve <b>beş türün tamamını</b>
/// raporlayan taraf.
///
/// <para>
/// <b>Hiçbir sağlayıcıya özel kod içermiyor.</b> Kayıtlı sağlayıcıları
/// <c>IEnumerable&lt;IEvidenceProvider&gt;</c> olarak alıyor,
/// <see cref="EvidenceKind"/> enum'unu geziyor ve karşılığı olmayan türü
/// <see cref="EvidenceStatus.NotRegistered"/> diye raporluyor. F5'te metrik,
/// trace ve topoloji sağlayıcıları geldiğinde burada değişecek tek şey yok —
/// kabul kriteri bu ve bugün sınanıyor.
/// </para>
///
/// <para>
/// <b>Neden T34'te:</b> "yeni sağlayıcı eklendiğinde motor değişmiyor" iddiası
/// ancak sağlayıcıdan habersiz bir tüketici varsa sınanabilir. Bu sınıf o
/// tüketicinin en incesi — korelasyon mantığı (T35) ve paket kalıcılığı (T36)
/// içermiyor.
/// </para>
/// </summary>
public sealed class EvidenceCollector(
    IEnumerable<IEvidenceProvider> providers,
    ILogger<EvidenceCollector> logger)
{
    private readonly IEvidenceProvider[] _providers = [.. providers];

    public IReadOnlyList<IEvidenceProvider> Providers => _providers;

    /// <summary>
    /// Hiç sağlayıcısı olmayan türler — bugün <c>Metric</c>, <c>Trace</c>,
    /// <c>Topology</c> (F5).
    /// </summary>
    public IReadOnlyList<EvidenceKind> UnregisteredKinds =>
    [
        .. Enum.GetValues<EvidenceKind>()
            .Where(kind => !_providers.Any(p => p.Kind == kind))
    ];

    /// <summary>
    /// Bütün sağlayıcıları koşturur ve <b>beş türü de</b> kapsayan bir sonuç
    /// döndürür.
    ///
    /// <para>
    /// Sağlayıcılar <b>paralel</b> koşuyor: biri ClickHouse'u 20 saniye
    /// bekletirse diğerlerinin sıraya girmesi için bir sebep yok, ve toplam süre
    /// tavanı kullanıcının beklediği süre.
    /// </para>
    /// </summary>
    public async Task<EvidenceReport> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(budget);

        window.Validate();

        var watch = Stopwatch.StartNew();

        var slices = await Task.WhenAll(
            _providers.Select(provider => RunAsync(provider, window, scope, budget, cancellationToken)));

        // Kayıtlı sağlayıcısı olmayan tür sessizce yok sayılmıyor: rapor "bu
        // kanıt türüne bakılmadı" diyebilmek zorunda. Sessiz atlama, okuyanın
        // eksik kanıta tam kanıt muamelesi yapmasının en kolay yolu.
        var missing = UnregisteredKinds.Select(kind => new EvidenceSlice
        {
            ProviderId = $"({kind.ToString().ToLowerInvariant()})",
            Kind = kind,
            Status = EvidenceStatus.NotRegistered,
            Detail = "Bu kanıt türü için sağlayıcı yok — F5.",
        });

        return new EvidenceReport
        {
            Window = window,
            Slices = [.. slices, .. missing],
            Duration = watch.Elapsed,
        };
    }

    private async Task<EvidenceSlice> RunAsync(
        IEvidenceProvider provider,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        if (!provider.IsAvailable)
        {
            return EvidenceSlice.NotAvailable(
                provider.Id, provider.Kind, "Sağlayıcı kayıtlı ama şu an kullanılamıyor.");
        }

        var watch = Stopwatch.StartNew();

        // Süre tavanı sağlayıcının insafına bırakılmıyor: tavanı burada
        // uygulamak, yeni bir sağlayıcının onu uygulamayı unutmasını kapatıyor.
        //
        // Sınırı da yazalım: bu bir **iptal sinyali**, zorla durdurma değil.
        // Token'ı hiç okumayan bir sağlayıcı yine de bütçeyi aşabilir; sözleşme
        // token'ı aşağı geçirmeyi şart koşuyor. Karşılığı, sağlayıcıların
        // `IScopedQuery`den geçmesi — sorgu tarafı token'a saygılı.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget.MaxDuration);

        try
        {
            var slice = await provider.GatherAsync(window, scope, budget, timeout.Token);

            return slice with { Duration = watch.Elapsed };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Bütçe aşımı bir arıza değil ama sessiz de değil: kanıt eksik.
            logger.LogWarning(
                "Kanıt sağlayıcısı {Provider} {Seconds:0.#} sn bütçesini aştı.",
                provider.Id,
                budget.MaxDuration.TotalSeconds);

            return new EvidenceSlice
            {
                ProviderId = provider.Id,
                Kind = provider.Kind,
                Status = EvidenceStatus.Failed,
                Detail = $"Süre tavanı aşıldı ({budget.MaxDuration.TotalSeconds:0.#} sn).",
                Truncated = true,
                Duration = watch.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            // Çağıranın iptali **yutulmuyor**. İlk yazımda aşağıdaki genel
            // `catch` onu da yakalıyordu ve koşu, kullanıcı vazgeçmiş olmasına
            // rağmen "sağlayıcı patladı" diyen tam bir rapor üretiyordu —
            // iptal edilmiş bir RCA'nın sonuç döndürmesi, hem yanlış hem de
            // sebebi hiçbir yerde görünmeyen bir davranış.
            throw;
        }
        catch (Exception ex)
        {
            // Tek sağlayıcının arızası paketi düşürmüyor. Ama `Failed` durumu
            // rapora kadar gidiyor — "kanıt yok" ile "kanıta bakamadık" farkı
            // bu sınıfın tamamı.
            logger.LogError(ex, "Kanıt sağlayıcısı {Provider} hata verdi.", provider.Id);

            return new EvidenceSlice
            {
                ProviderId = provider.Id,
                Kind = provider.Kind,
                Status = EvidenceStatus.Failed,
                Detail = ex.Message,
                Duration = watch.Elapsed,
            };
        }
    }
}

/// <summary>
/// Bir toplama koşusunun tamamı. T36 bunu <c>evidence_bundle</c> olarak
/// saklayacak; T34 yalnızca şeklini tanımlıyor.
/// </summary>
public sealed record EvidenceReport
{
    public required RcaWindow Window { get; init; }
    public required IReadOnlyList<EvidenceSlice> Slices { get; init; }
    public TimeSpan Duration { get; init; }

    public IEnumerable<EvidenceItem> Items => Slices.SelectMany(s => s.Items);

    /// <summary>Kapsam dışı toplam — rapordaki tek satırın kaynağı (RCA §3.2).</summary>
    public long OutOfScopeCount => Slices.Sum(s => s.OutOfScopeCount);

    /// <summary>
    /// Rapor <b>eksik kanıtla</b> mı kuruluyor. Doğruysa okuyan bunu görmeli:
    /// bir sağlayıcı patlamış, bütçeye takılmış ya da kırpılmış olabilir.
    /// </summary>
    public bool IsPartial =>
        Slices.Any(s => s.Status is EvidenceStatus.Failed or EvidenceStatus.Unavailable || s.Truncated);

    /// <summary>
    /// Bakılamayan türler — raporun "kapalı sağlayıcılar" bölümü. Boş liste
    /// "her şeye bakıldı" demek; dolu liste okuyanın körü körüne güvenmesini
    /// zorlaştıran şeyin ta kendisi.
    /// </summary>
    public IReadOnlyList<EvidenceSlice> NotConsulted =>
        [.. Slices.Where(s => !s.IsEvidence)];
}

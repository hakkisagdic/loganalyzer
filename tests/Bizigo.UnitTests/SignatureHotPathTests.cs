using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Discovery;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Text;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// K35'in taşıyıcı iddiası, sıcak yolun kendisinde sınanıyor:
/// <b><c>signature_hash</c> her olayda dolu</b> — ayrıştırma durumundan,
/// örneklemeden ve sidecar'ın açık olup olmamasından bağımsız.
///
/// <para>
/// Bu üçü ayrı ayrı sınanmak zorunda çünkü <c>template_id</c> tam olarak
/// üçünde de boş kalıyor ve F3'ün iki korelasyonu bu yüzden kırıktı. Testin
/// birim seviyesinde değil <see cref="ParsingSink"/> üzerinden koşmasının sebebi
/// de bu: iddia "hash fonksiyonu çalışıyor" değil, "boru hattı onu her olayda
/// çağırıyor".
/// </para>
/// </summary>
public sealed class SignatureHotPathTests : IDisposable
{
    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
    private static readonly DateTimeOffset Received = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryControlPlaneFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Katalogdaki gerçek bir satır (ayrıştırması başarılı olur) ve tanınmayacak
    /// bir satır (<c>failed</c>) — ikisi de imzalanmak zorunda.
    /// </summary>
    private const string ParsesOk =
        "Oct 10 2018 12:34:56 localhost CiscoASA[999]: %ASA-6-302013: Built outbound TCP "
        + "connection 11757 for outside:192.168.205.104/80 to inside:172.31.98.44/1772";

    private const string ParsesFailed = "bu satıra uyan hiçbir parser yok 10.0.0.1 port 443";

    [Fact]
    public async Task Her_olayda_imza_dolu_ornekleme_kapaliyken_de()
    {
        // SampleRate = 0: eski kod yolunda başarılı olayların HİÇBİRİ
        // maskelenmiyordu. Kolonun bunlarda da dolması K35'in tamamı.
        var (sink, events) = Build(new SidecarOptions { SampleRate = 0, QueueCapacity = 64 });

        await sink.HandleAsync(Batch(ParsesOk, ParsesFailed), TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count);

        foreach (var parsed in events)
        {
            Assert.NotEqual(SignatureHash.None, parsed.SignatureHash);
            Assert.Equal(Masks.Compute(parsed.Decoded).Hash, parsed.SignatureHash);
        }
    }

    /// <summary>
    /// Ayrıştırması başarılı olan olayda da dolu — ve <c>template_id</c>'nin boş
    /// kalması bunu etkilemiyor. İki alanın bağımsızlığının doğrudan kanıtı.
    /// </summary>
    [Fact]
    public async Task Ayristirma_durumu_imzayi_etkilemiyor()
    {
        var (sink, events) = Build(new SidecarOptions { SampleRate = 0, QueueCapacity = 64 });

        await sink.HandleAsync(Batch(ParsesOk, ParsesFailed), TestContext.Current.CancellationToken);

        var statuses = events.Select(e => e.Parsed.Status).ToArray();
        Assert.Contains(ParseStatus.Failed, statuses);
        Assert.Contains(statuses, s => s != ParseStatus.Failed);

        Assert.All(events, parsed => Assert.NotEqual(SignatureHash.None, parsed.SignatureHash));
        Assert.All(events, parsed => Assert.Equal(string.Empty, parsed.TemplateId));
    }

    /// <summary>
    /// <b>Sidecar kapalıyken de dolu.</b> K35'in gerekçesinin tamamı bu satır:
    /// RCA'nın en güçlü iki sinyali artık sidecar'a bağlı değil.
    /// </summary>
    [Fact]
    public async Task Sidecar_kapaliyken_de_imza_dolu()
    {
        var (sink, events) = Build(new SidecarOptions { Enabled = false }, new NullTemplateAnnotator());

        await sink.HandleAsync(Batch(ParsesOk, ParsesFailed), TestContext.Current.CancellationToken);

        Assert.All(events, parsed => Assert.NotEqual(SignatureHash.None, parsed.SignatureHash));
    }

    /// <summary>
    /// Aynı gövde iki farklı kaynaktan gelince aynı hash — kaynak kimliği
    /// imzaya karışmıyor. Sıcak yolun kendisinde sınanıyor, çünkü karışma
    /// ihtimali <see cref="SignatureHash.Of"/>'ta değil, çağıran taraftaydı.
    /// </summary>
    [Fact]
    public async Task Iki_farkli_kaynaktan_ayni_govde_ayni_hash()
    {
        var (sink, events) = Build(new SidecarOptions { SampleRate = 0, QueueCapacity = 64 });

        await sink.HandleAsync(
            [Record(ParsesOk, "10.1.1.1"), Record(ParsesOk, "10.9.9.9")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count);
        Assert.Equal(events[0].SignatureHash, events[1].SignatureHash);
        Assert.NotEqual(SignatureHash.None, events[0].SignatureHash);
    }

    /// <summary>
    /// Uzunluk sınırını aşan satır imzasız geçiyor <b>ve sayılıyor</b> — olay
    /// düşmüyor, yalnızca korelasyonlarda görünmüyor.
    /// </summary>
    [Fact]
    public async Task Sinirini_asan_satir_imzasiz_ve_sayiliyor()
    {
        var masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
        var (sink, events) = Build(new SidecarOptions { SampleRate = 0, QueueCapacity = 64 }, masks: masks);
        var before = masks.SkippedTooLong;

        await sink.HandleAsync(
            [Record(new string('x', MaskCatalog.MaxInputLength + 1), "10.1.1.1")],
            TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal(SignatureHash.None, events[0].SignatureHash);
        Assert.Equal(before + 1, masks.SkippedTooLong);
    }

    /// <summary>
    /// <b>Bekçinin kırmızı yanabildiği.</b> Sıcak yol imzayı hesaplamayı
    /// bıraksaydı yukarıdaki testlerin hepsi düşerdi — bu test onu doğrudan
    /// gösteriyor: imzasız kurulmuş bir olay hash'i sıfır taşıyor ve
    /// normalizasyon onu olduğu gibi geçiriyor, yani sessizce bir yerden
    /// dolmuyor.
    /// </summary>
    [Fact]
    public void Imzasiz_kurulan_olay_normalizasyonda_sifir_kaliyor()
    {
        var parsed = new ParsedEvent(
            new RawRecord
            {
                EventId = Guid.CreateVersion7(Received),
                ReceivedAt = Received,
                SourceKey = "10.1.1.1",
                OwnerGroup = OwnerGroups.Unassigned,
                SourceId = "s",
                Body = Encoding.UTF8.GetBytes(ParsesOk),
            },
            ParsesOk,
            "utf-8",
            new ResolvedSource("s", OwnerGroups.Unassigned, "firewall", "auto", string.Empty, IsKnown: false),
            new ParseResult
            {
                ParserId = string.Empty,
                ParserVersion = string.Empty,
                Status = ParseStatus.Failed,
                Fields = new Dictionary<string, object?>(StringComparer.Ordinal),
            },
            DispatchTier.Unmatched);

        Assert.Equal(SignatureHash.None, new EventNormalizer().Normalize(parsed).SignatureHash);
    }

    /// <summary>Normalizasyon imzayı olduğu gibi taşıyor — kolona giden tek yol.</summary>
    [Fact]
    public async Task Normalizasyon_imzayi_tasiyor()
    {
        var (sink, events) = Build(new SidecarOptions { SampleRate = 0, QueueCapacity = 64 });

        await sink.HandleAsync(Batch(ParsesOk), TestContext.Current.CancellationToken);

        var normalized = new EventNormalizer().Normalize(events[0]);

        Assert.Equal(events[0].SignatureHash, normalized.SignatureHash);
        Assert.Equal(Masks.Compute(ParsesOk).Hash, normalized.SignatureHash);
    }

    private static IReadOnlyList<DecodedRecord> Batch(params string[] bodies) =>
        [.. bodies.Select(body => Record(body, "10.1.1.1"))];

    private static DecodedRecord Record(string body, string sourceKey) => new(
        new RawRecord
        {
            EventId = Guid.CreateVersion7(Received),
            ReceivedAt = Received,
            SourceKey = sourceKey,
            Body = Encoding.UTF8.GetBytes(body),
        },
        new DecodedBody("utf-8", body, WasDeclaredHonored: true));

    /// <summary>
    /// Gerçek dispatcher ve gerçek katalog. Envanter boş bırakılıyor: kaynaklar
    /// <c>_unassigned</c>'a düşüyor ve dağıtım ön filtreden geçiyor — imza
    /// açısından fark etmiyor, ama sahte bir dispatcher kurmak testin sınadığı
    /// şeyi (boru hattının imzayı çağırması) zayıflatırdı.
    /// </summary>
    private (ParsingSink Sink, List<ParsedEvent> Events) Build(
        SidecarOptions options,
        ITemplateAnnotator? annotator = null,
        MaskCatalog? masks = null)
    {
        var tables = MappingTableCatalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));
        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(
            RepositoryLayout.CatalogParserDirectory,
            new ParserCompiler(new GrokCompiler(RepositoryLayout.DefaultLibrary), tables));
        Assert.Empty(load.Errors);

        var stats = new DiscoveryStats();
        annotator ??= new DiscoveryAnnotator(
            options, new TemplateCache(1024), new DiscoveryQueue(options, stats), stats);

        var collector = new CollectingSink();

        return (
            new ParsingSink(
                new EventComposer(
                    new Dispatcher(catalog, new DispatchStats()),
                    annotator,
                    masks ?? Masks,
                    NullLogger<EventComposer>.Instance),
                new SourceDirectory(_factory),
                new DispatchStats(),
                collector),
            collector.Events);
    }

    private sealed class CollectingSink : IParsedEventSink
    {
        public List<ParsedEvent> Events { get; } = [];

        public ValueTask HandleAsync(IReadOnlyList<ParsedEvent> batch, CancellationToken cancellationToken)
        {
            Events.AddRange(batch);
            return ValueTask.CompletedTask;
        }
    }
}

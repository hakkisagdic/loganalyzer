using System.Globalization;
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
using Bizigo.Parsing.Testing;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.Cli.Seeding;

/// <param name="Text">Satırın kendisi, dosyadaki hâliyle.</param>
/// <param name="ParserDirectory">
/// <c>catalog/parsers/&lt;bu&gt;/samples/…</c>. Kaynak kimliği bundan türüyor,
/// dosya adından değil: bir vendor'ın iki örnek dosyası aynı cihazın iki
/// konusudur.
/// </param>
/// <param name="File">Örnek dosyanın yolu — rapor ve hata mesajı için.</param>
/// <param name="Line">Dosyadaki satır numarası (1 tabanlı).</param>
public sealed record GoldenSampleLine(string Text, string ParserDirectory, string File, int Line);

/// <param name="Rows">ClickHouse'a yazılan satır.</param>
/// <param name="DistinctSignatures">Ayrı <c>signature_hash</c> sayısı (0 hariç).</param>
/// <param name="ByVendor">Vendor → satır. Sigma ölçümü paydayı buradan kuruyor.</param>
/// <param name="ByTimeSource">
/// <c>time_source</c> dağılımı. Üretimdeki dağılımın aynısı olmalı; hepsi
/// <c>received</c> çıkarsa yeniden yazma hiç tutmamış demektir.
/// </param>
/// <param name="ByParseStatus"><c>ok</c>/<c>partial</c>/<c>failed</c> dağılımı.</param>
/// <param name="RowsInLastWindow">Son 45 dakikadaki satır.</param>
/// <param name="SignaturesInLastWindow">
/// Son 45 dakikadaki ayrı imza — baseline ölçümünün oranının <b>paydası bu</b>.
/// Sıfırsa ölçüm "pencerede hiç imzalı olay yok" der ve durur; tek haneliyse
/// oran kaba adımlarla değişir ve eğrinin dirseği okunamaz.
/// </param>
/// <param name="From">Yazılan en eski olay zamanı.</param>
/// <param name="To">Yazılan en yeni olay zamanı.</param>
public sealed record SeedReport(
    long Rows,
    int DistinctSignatures,
    IReadOnlyDictionary<string, long> ByVendor,
    IReadOnlyDictionary<string, long> ByTimeSource,
    IReadOnlyDictionary<string, long> ByParseStatus,
    long RowsInLastWindow,
    int SignaturesInLastWindow,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>
/// Altın örnekleri <b>ürünün kendi boru hattından</b> geçirip ClickHouse'a yazar
/// (T39).
///
/// <para>
/// <b>Neden doğrudan <c>INSERT</c> değil:</b> ölçümlerin sorduğu şeylerin hepsi
/// — <c>signature_hash</c>, <c>template_id</c>, OCSF/OTel görünümlerinin
/// beslendiği <c>attrs</c>, <c>time_source</c>, <c>parse_status</c> — boru
/// hattının çıktısı. Elle yazılan bir satır bunların hepsinde üretimdekinden
/// ayrışabilir ve ayrıştığı hiçbir yerde görünmez; ölçüm o zaman ölçmek
/// istediğimiz şeyi değil, fixture'ı ölçer.
/// </para>
///
/// <para>
/// <b>Kullanılan yol:</b> <see cref="EncodingDetector"/> →
/// <see cref="EventComposer"/> (dispatch + imza + şablon) →
/// <see cref="EventNormalizer"/> → <see cref="EventWriter"/>. Yani ingest'in
/// HTTP ucu, WAL'ı ve kanalı dışında kalan her adım gerçek.
/// </para>
///
/// <para>
/// <b><see cref="ClickHouseEventSink"/> bilerek atlandı.</b> O sınıf yazma
/// hatasını <b>yutuyor</b> ve sayaca yazıyor — üretimde doğru davranış, çünkü
/// veri WAL'da duruyor ve replay ile kapatılabiliyor. Yükleyicide WAL yok:
/// yutulan bir hata, eksik yüklenmiş ve eksikliği görünmeyen bir veri kümesi
/// demek olurdu. Normalizasyon ve yazım aynı tiplerle yapılıyor, yalnızca hata
/// yolu gürültülü.
/// </para>
///
/// <para>
/// <b>Kaynak envanterden çözülmüyor.</b> Yükleyici kaynağını zaten biliyor ve
/// Postgres'e bağlanması gereksiz bir bağımlılık olurdu. <c>ParserId</c> boş
/// bırakılıyor: dispatcher'ın 1. kademesi (envanter bağı) yerine 2. kademe
/// (literal ön filtre) koşuyor — <see cref="SampleCoverage"/> ile aynı tercih ve
/// aynı gerekçe: buradaki soru "kaynağı tanımasak da katalog bu satırı tanır
/// mı".
/// </para>
/// </summary>
public sealed class GoldenSampleSeeder
{
    /// <summary>
    /// Doğrulama toleransı. Bütün biçimler tam saniye taşıdığı için sıfır da
    /// olabilirdi; bir saniye, gelecekte kesirli bir biçim eklenirse yükleyicinin
    /// tamamını durdurmasın diye duruyor.
    /// </summary>
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Raporlanan "son pencere" — <c>BaselineWindowMeasurement.WindowLength</c>
    /// ile aynı. Ölçüm o pencerede imza bulamazsa hiç başlamıyor, o yüzden
    /// yükleyici sayıyı yazmadan bitmemeli.
    /// </summary>
    private static readonly TimeSpan LastWindow = TimeSpan.FromMinutes(45);

    private readonly EventComposer _composer;
    private readonly EncodingDetector _detector;
    private readonly EventNormalizer _normalizer;
    private readonly MaskCatalog _masks;

    public GoldenSampleSeeder(Dispatcher dispatcher, MaskCatalog masks)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(masks);

        _masks = masks;
        _composer = new EventComposer(
            dispatcher,
            new NullTemplateAnnotator(),
            masks,
            NullLogger<EventComposer>.Instance);
        _detector = new EncodingDetector();
        _normalizer = new EventNormalizer();
    }

    /// <summary>
    /// <c>catalog/parsers/&lt;id&gt;/samples/*.log</c> altındaki satırlar.
    /// Boş satır ve <c>#</c> yorumu atlanıyor — <see cref="SampleCoverage"/> ile
    /// aynı kural, çünkü aynı dosyalar.
    /// </summary>
    public static IReadOnlyList<GoldenSampleLine> ReadSamples(string catalogDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);

        var lines = new List<GoldenSampleLine>();

        if (!Directory.Exists(catalogDirectory))
        {
            return lines;
        }

        var files = Directory
            .EnumerateDirectories(catalogDirectory, SampleCoverage.SamplesDirectoryName, SearchOption.AllDirectories)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories))
            .OrderBy(static path => path, StringComparer.Ordinal);

        foreach (var file in files)
        {
            // `<catalog>/<parser dizini>/samples/<dosya>` — kaynak kimliği
            // parser dizininden geliyor.
            var parserDirectory = ParserDirectoryOf(catalogDirectory, file);
            var number = 0;

            foreach (var raw in File.ReadLines(file))
            {
                number++;

                if (raw.Length == 0 || raw[0] == '#')
                {
                    continue;
                }

                lines.Add(new GoldenSampleLine(raw, parserDirectory, file, number));
            }
        }

        return lines;
    }

    /// <summary>Satır başına maskelenmiş imza — yayılım planının girdisi.</summary>
    public IReadOnlyList<ulong> Signatures(IReadOnlyList<GoldenSampleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return [.. lines.Select(line => _masks.Compute(line.Text).Hash)];
    }

    /// <summary>Satır başına vendor anahtarı — sıranın vendor'lara dağıtılması için.</summary>
    public static IReadOnlyList<string> Vendors(IReadOnlyList<GoldenSampleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return [.. lines.Select(static line => line.ParserDirectory)];
    }

    /// <summary>
    /// <b>Ölçülen bir ürün özelliği, yükleyicinin kusuru değil:</b> maskeleme
    /// sözlüğünde ay <b>adı</b> için bir maske yok. <c>NUMBER</c> saati ve günü
    /// yutuyor ama <c>May</c>, <c>Oct</c> gibi kısaltmalar imzada aynen kalıyor —
    /// yani syslog biçimli vendor'larda (Cisco ASA, MikroTik, nginx) aynı şablon
    /// her ay <b>yeni bir <c>signature_hash</c></b> alıyor.
    ///
    /// <para>
    /// Bu, F3'ün "ilk-görülen imza" sinyalini doğrudan ilgilendiriyor: ayın
    /// birinde o vendor'ların bütün şablonları "yeni" görünüyor. Yükleyici
    /// açısından sonucu, 30 günlük yayılımın kaçınılmaz olarak bir ay sınırı
    /// içermesi ve ayrı imza sayısının satır sayısından fazla çıkması. Sayı
    /// raporda basılıyor ki eğriyi okuyan kişi bunu bilmeden okumasın.
    /// </para>
    ///
    /// <para>Ölçülen: 5 günlük yayılımda 81/81, 30 günlükte 92, 90 günlükte 102 ayrı imza.</para>
    /// </summary>
    /// <returns>Yayılım boyunca imzası sabit kalmayan satır sayısı.</returns>
    public int TimeSensitiveLines(IReadOnlyList<GoldenSampleLine> lines, DateTimeOffset anchor, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var probes = new[] { anchor, anchor - (span / 2), anchor - span };
        var count = 0;

        foreach (var line in lines)
        {
            var hashes = probes
                .Select(at => _masks.Compute(SampleTimeRewriter.Rewrite(line.Text, at).Text).Hash)
                .Distinct()
                .Count();

            if (hashes > 1)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Planı olaya çevirir. <b>Yazmıyor</b> — yazan taraf toplu hâlde
    /// <see cref="EventWriter"/>'ı çağırıyor, böylece ClickHouse'suz sınanabilir.
    /// </summary>
    public LogEvent Compose(
        GoldenSampleLine sample,
        DateTimeOffset at,
        string ownerGroup,
        Guid eventId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerGroup);

        var rewritten = SampleTimeRewriter.Rewrite(sample.Text, at);
        var bytes = Encoding.UTF8.GetBytes(rewritten.Text);

        var raw = new RawRecord
        {
            EventId = eventId,
            // Cihaz damgası: yeniden yazılamayan satırlarda olay zamanı buradan
            // geliyor (`time_source = observed`) ve o da üretimdeki bir hâl.
            ObservedAt = at,
            ReceivedAt = at,
            SourceKey = SourceKeyOf(sample.ParserDirectory),
            TransportProto = "syslog-tcp",
            TransportPeer = SourceKeyOf(sample.ParserDirectory),
            EncodingDeclared = "utf-8",
            Body = bytes,
        };

        var decoded = _detector.Decode(bytes, raw.EncodingDeclared, sourceFallback: null);
        var source = SourceFor(sample.ParserDirectory, ownerGroup);
        var parsed = _composer.Compose(new DecodedRecord(raw, decoded), source);
        var normalized = _normalizer.Normalize(parsed);

        Verify(sample, rewritten, at, normalized);
        return normalized;
    }

    /// <summary>
    /// <b>Bekçi.</b> Olayın zamanı ekilen ana eşit değilse yükleyici durur.
    ///
    /// <para>
    /// Bu kontrolün var olma sebebi: damga biçimi bilgisi
    /// <see cref="SampleTimeRewriter"/> ile parser YAML'ında <b>iki kez</b>
    /// yazılı. Ayrışırlarsa satır ya orijinal (2015–2024) tarihine ya da yanlış
    /// saat dilimine düşer — ikisi de hata üretmez, yalnızca baseline ölçümünü
    /// sessizce yanlış yapar. Sessiz yanlış davranış bu depoda en pahalı hata
    /// sınıfı; burada gürültülü hâle getiriliyor.
    /// </para>
    /// </summary>
    private static void Verify(
        GoldenSampleLine sample,
        RewrittenLine rewritten,
        DateTimeOffset expected,
        LogEvent normalized)
    {
        var drift = normalized.Timestamp - expected;

        if (drift.Duration() <= TimestampTolerance)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             Zaman damgası yeniden yazımı tutmadı — yükleme durduruldu.
               dosya      : {sample.File}:{sample.Line}
               beklenen   : {expected:O}
               olan       : {normalized.Timestamp:O}  (time_source={normalized.TimeSource})
               parser     : {(normalized.ParserId.Length == 0 ? "<eşleşmedi>" : normalized.ParserId)}
               damga yaz. : {(rewritten.Rewritten ? "evet" : "hayır — satırda tanınan damga yok")}
               satır      : {rewritten.Text}

             Muhtemel sebep: parser YAML'ının `date` adımı ile SampleTimeRewriter
             ayrıştı (biçim, `default_timezone` ya da alan adı değişti).
             """));
    }

    /// <summary>
    /// Kaynak kaydı. <c>IsKnown: true</c> ama <c>ParserId: null</c> — envanterde
    /// duran ama parser'a bağlanmamış kaynak, gerçek ve olağan bir durum.
    /// </summary>
    private static ResolvedSource SourceFor(string parserDirectory, string ownerGroup) =>
        new(
            SourceId: "golden-" + parserDirectory,
            OwnerGroup: ownerGroup,
            SourceClass: "golden",
            Encoding: "utf-8",
            ParserId: null,
            IsKnown: true);

    private static string SourceKeyOf(string parserDirectory) => "golden-" + parserDirectory;

    private static string ParserDirectoryOf(string catalogDirectory, string file)
    {
        var relative = Path.GetRelativePath(catalogDirectory, file);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first;
    }

    /// <summary>
    /// Planı koşturur ve <paramref name="write"/>'ı toplu hâlde çağırır.
    ///
    /// <para>
    /// Yazım geri çağrı olarak alınıyor: ClickHouse'a yazmak entegrasyon işi,
    /// olay üretmek değil. Birim testi aynı yolu koşturup satırları belleğe
    /// toplayabiliyor.
    /// </para>
    /// </summary>
    public async Task<SeedReport> RunAsync(
        IReadOnlyList<GoldenSampleLine> samples,
        IReadOnlyList<PlannedOccurrence> plan,
        string ownerGroup,
        int batchRows,
        Func<IReadOnlyList<LogEvent>, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);

        var signatures = new HashSet<ulong>();
        var byVendor = new Dictionary<string, long>(StringComparer.Ordinal);
        var byTimeSource = new Dictionary<string, long>(StringComparer.Ordinal);
        var byStatus = new Dictionary<string, long>(StringComparer.Ordinal);

        var windowSignatures = new HashSet<ulong>();
        var buffer = new List<LogEvent>(batchRows);
        long rows = 0;
        long inLastWindow = 0;

        var from = plan.Count == 0 ? DateTimeOffset.MinValue : plan[0].At;
        var to = plan.Count == 0 ? DateTimeOffset.MinValue : plan[^1].At;
        var windowStart = to - LastWindow;

        // `EventId` deterministik: aynı tohum + aynı plan aynı kimlikleri verir,
        // yani iki koşumun aynı veriyi mi ürettiği elle karşılaştırılabiliyor.
        var ordinal = 0;

        foreach (var occurrence in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = samples[occurrence.LineIndex];
            var normalized = Compose(sample, occurrence.At, ownerGroup, DeterministicId(ordinal++));

            if (normalized.SignatureHash != 0)
            {
                signatures.Add(normalized.SignatureHash);
            }

            Bump(byVendor, normalized.Vendor.Length == 0 ? "<yok>" : normalized.Vendor);
            Bump(byTimeSource, normalized.TimeSource);
            Bump(byStatus, normalized.ParseStatus.ToString());

            if (normalized.Timestamp >= windowStart)
            {
                inLastWindow++;

                if (normalized.SignatureHash != 0)
                {
                    windowSignatures.Add(normalized.SignatureHash);
                }
            }

            buffer.Add(normalized);
            rows++;

            if (buffer.Count >= batchRows)
            {
                await write(buffer, cancellationToken);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await write(buffer, cancellationToken);
        }

        return new SeedReport(
            rows, signatures.Count, byVendor, byTimeSource, byStatus,
            inLastWindow, windowSignatures.Count, from, to);
    }

    private static void Bump(Dictionary<string, long> counters, string key) =>
        counters[key] = counters.GetValueOrDefault(key) + 1;

    /// <summary>
    /// Sıra numarasından türetilen sabit UUID. <c>Guid.NewGuid()</c> kullanılsaydı
    /// iki koşumun ürettiği veri kimlik düzeyinde karşılaştırılamazdı.
    /// </summary>
    private static Guid DeterministicId(int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        "bizigo-golden"u8[..8].CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes[8..], (long)ordinal);
        return new Guid(bytes);
    }
}

using Bizigo.Commands.Fields;
using Bizigo.Commands.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Commands;

/// <param name="Catalog">Parser kataloğu (altın örnekler burada).</param>
/// <param name="MaskFile">Maskeleme sözlüğü.</param>
/// <param name="Migrations">ClickHouse göç dizini — görünümün kolon listesi oradan okunuyor.</param>
/// <param name="ConnectionString">Boşsa yalnızca katalog yarısı koşuyor.</param>
/// <param name="OwnerGroup">ClickHouse yarısında sayılan kapsam grubu.</param>
/// <param name="Anchor">Örnekleri hangi ana taşıyarak ölçeceği.</param>
public sealed record FieldCoverageRequest(
    string Catalog,
    string MaskFile,
    string Migrations,
    string? ConnectionString,
    string OwnerGroup,
    DateTimeOffset Anchor);

/// <param name="ColumnCount">Görünümden okunan kolon sayısı.</param>
/// <param name="SampleCount">Ölçüme giren örnek satır sayısı.</param>
/// <param name="Report">Katalog yarısı — <i>"ne üretilebiliyor"</i>.</param>
/// <param name="Stored">
/// ClickHouse yarısı — <i>"ne yazılmış"</i>. <b><see langword="null"/> "boş" DEĞİL</b>:
/// bağlantı verilmediği için <b>hiç sorulmadı</b>. Boş liste ise soruldu ve
/// grupta satır yok. İkisi tek değere inseydi <i>"bakmadım"</i> ile
/// <i>"baktım, yok"</i> aynı çıktıyı verirdi — bu deponun dört kez ödediği
/// ayrım.
/// </param>
public sealed record FieldsCoverageOutcome(
    int ColumnCount,
    int SampleCount,
    FieldCoverageReport Report,
    IReadOnlyList<VendorFieldCoverage>? Stored);

/// <param name="Columns">
/// Görünümden okunan kolonlar. <b>Sayı değil kolonların kendisi</b> taşınıyor:
/// CLI'nin <c>--rules</c> birleştirmesi onlara ihtiyaç duyuyor ve yalnızca sayı
/// taşınsaydı görünümü <b>ikinci kez</b> okumak zorunda kalırdı — aynı dosyanın
/// iki okuması, arada değişirse iki farklı gerçek.
/// </param>
/// <param name="Spaces">Vendor başına kolon değer uzayları.</param>
public sealed record FieldsValuesOutcome(
    IReadOnlyList<OcsfViewColumn> Columns,
    IReadOnlyList<VendorValueSpace> Spaces)
{
    public int ColumnCount => Columns.Count;
}

/// <summary>
/// Alan kapsamı komutlarının çekirdeği (T39 ölçümleri).
///
/// <para>
/// Hesap <c>Bizigo.Commands.Fields</c>'te ve <b>zaten saftı</b> — M02 onu
/// <c>Bizigo.Cli</c>'den buraya taşıdı, yeniden yazmadı. Buradaki iş bekçiler,
/// kurulum ve <b>sebep adlandırma</b>.
/// </para>
/// </summary>
public static class FieldsCommands
{
    /// <summary>
    /// Katalog yarısı ClickHouse istemiyor: örnek satırları gerçek boru
    /// hattından geçirip <c>LogEvent</c>'e bakıyor. ClickHouse yarısı yazma ve
    /// görünüm yolundan sonra ne kaldığını sayıyor. İkisinin farkı tek başına
    /// görünmeyen bir hata sınıfını yakalıyor — <b>alan doluyor ama kolon boş
    /// görünüyor</b> — ve o kayıp hata vermez, yalnızca o alana vuran her Sigma
    /// kuralını sessizce sonuçsuz bırakır.
    /// </summary>
    public static async Task<CommandOutcome<FieldsCoverageOutcome>> CoverageAsync(
        FieldCoverageRequest request,
        ParserToolbox toolbox,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolbox);

        // BEKÇİ: yazıcının yazdığı her kolonu tanımıyorsak ölçüm eksik bir
        // tabloyu tam gösterir.
        var unknown = EventFieldKinds.Unknown();

        if (unknown.Count > 0)
        {
            return CommandOutcome<FieldsCoverageOutcome>.Failed(
                CommandFailureKind.InvalidArgument,
                "`events` tablosuna eklenmiş ama EventFieldKinds'ta tanımlanmamış kolon(lar): " +
                string.Join(", ", unknown) +
                ". Alan kapsamı ölçümü onları hiç sormaz; önce oraya ekleyin.");
        }

        var samples = GoldenSampleSeeder.ReadSamples(request.Catalog);

        if (samples.Count == 0)
        {
            return CommandOutcome<FieldsCoverageOutcome>.Failed(
                CommandFailureKind.NotFound,
                $"{request.Catalog} altında hiç `samples/*.log` yok.");
        }

        var columns = OcsfViewSchema.Read(request.Migrations);

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(request.Catalog, toolbox.Compiler);

        if (load.Errors.Count > 0)
        {
            return CommandOutcome<FieldsCoverageOutcome>.Failed(
                CommandFailureKind.InvalidArgument,
                "Katalog yüklenemedi: " + string.Join("; ", load.Errors));
        }

        var seeder = new GoldenSampleSeeder(
            new Dispatcher(catalog, new DispatchStats()),
            MaskCatalog.LoadFromFile(request.MaskFile));

        // Her örnek satır BİR kez: soru "katalog ne taşıyor", "hangi satır kaç
        // kez yazıldı" değil. Zipf ağırlıkları buraya karışsaydı nadir bir
        // satırın doldurduğu alan oranda kaybolurdu.
        var events = new List<LogEvent>(samples.Count);

        foreach (var sample in samples)
        {
            events.Add(seeder.Compose(sample, request.Anchor, request.OwnerGroup, Guid.NewGuid()));
        }

        var report = FieldCoverage.Measure(events, columns);

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            // `null` — "boş" DEĞİL. Bağlantı verilmedi, yani soru hiç
            // sorulmadı; sunum bunu ayrı bir cümleyle söylemek zorunda.
            return CommandOutcome<FieldsCoverageOutcome>.Success(
                new FieldsCoverageOutcome(columns.Count, samples.Count, report, Stored: null));
        }

        using var context = new ClickHouseContext(new ClickHouseOptions
        {
            ConnectionString = request.ConnectionString,
        });

        var stored = await new FieldCoverageReader(context).ReadAsync(
            request.OwnerGroup,
            [.. columns.Select(column => (column.Source, column.Alias))],
            cancellationToken).ConfigureAwait(false);

        return CommandOutcome<FieldsCoverageOutcome>.Success(
            new FieldsCoverageOutcome(columns.Count, samples.Count, report, stored));
    }

    /// <summary>
    /// Bir kolonun taşıdığı ayrık değerler: <b>kapalı</b> küme mi, cihazın ne
    /// yazarsa yazsın <b>açık</b> mı, yoksa hiç <b>yok</b> mu.
    /// </summary>
    public static CommandOutcome<FieldsValuesOutcome> Values(
        string catalogDirectory,
        string mappingsDirectory,
        string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        var columns = OcsfViewSchema.Read(migrationsDirectory);
        var tables = MappingTableCatalog.LoadFromDirectory(mappingsDirectory);
        var spaces = ColumnValueSpaces.Build(catalogDirectory, tables, columns);

        if (spaces.Count == 0)
        {
            return CommandOutcome<FieldsValuesOutcome>.Failed(
                CommandFailureKind.NotFound,
                $"{catalogDirectory} altında `metadata.vendor` taşıyan parser yok.");
        }

        return CommandOutcome<FieldsValuesOutcome>.Success(
            new FieldsValuesOutcome(columns, spaces));
    }
}

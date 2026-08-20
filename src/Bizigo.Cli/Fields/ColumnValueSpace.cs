using System.Globalization;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Schema;

namespace Bizigo.Cli.Fields;

/// <summary>Bir kolonun taşıyabileceği değerler hakkında ne bilindiği.</summary>
public enum ValueSpaceKind
{
    /// <summary>
    /// Bu vendor'ın hiçbir parser'ı kolonu doldurmuyor. Kolona vuran her kural,
    /// veri ne olursa olsun eşleşmez.
    /// </summary>
    Absent,

    /// <summary>
    /// Değer uzayı <b>kapalı</b>: kolon bir eşleme tablosundan ya da sabitten
    /// besleniyor, dolayısıyla hangi değerlerin durabileceği <b>önceden</b>
    /// biliniyor.
    /// </summary>
    Closed,

    /// <summary>
    /// Değer uzayı <b>açık</b>: kolon cihazın yazdığını taşıyor
    /// (<c>"{{ srcip }}"</c>). Buradan bir şey söylenemez ve söylememek doğru.
    /// </summary>
    Open,
}

/// <param name="Alias"><c>events_ocsf</c>'teki ad — Sigma kurallarının vurduğu ad.</param>
/// <param name="Column"><c>events</c>'teki kaynak kolon.</param>
/// <param name="Kind">Uzay hakkında ne biliniyor.</param>
/// <param name="Values"><see cref="ValueSpaceKind.Closed"/> ise erişilebilir değerler.</param>
/// <param name="Sources">Uzayı kuran parser'lar ve tablolar — teşhis için.</param>
/// <param name="MissingIn">
/// Bu vendor'ın kolonu <b>doldurmayan</b> parser'ları.
///
/// <para>
/// Vendor düzeyinde birleşim, parser düzeyindeki farkı gizliyor ve o fark
/// gerçek bir vaka: <c>activity_name</c> MikroTik'te "açık" görünüyor çünkü
/// <c>routeros.system</c> dolduruyor — ama <c>routeros.firewall</c> onu bilerek
/// boş bırakıyor ve <c>logsource.category: firewall</c> taşıyan bir kural
/// yalnızca o parser'ın satırlarına vuruyor. Liste olmasaydı ölçüm "açık uzay,
/// söylenemez" der ve asıl cevabı gizlerdi.
/// </para>
/// </param>
public sealed record ColumnValueSpace(
    string Alias,
    string Column,
    ValueSpaceKind Kind,
    IReadOnlyList<string> Values,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> MissingIn);

/// <param name="Vendor"><c>metadata.vendor</c> — <c>device_vendor_name</c> kolonunun değeri.</param>
/// <param name="Products">
/// Bu vendor'ın <c>metadata.product</c> değerleri. Sigma kuralının
/// <c>logsource.product</c>'ı buraya bağlanıyor.
/// </param>
/// <param name="Columns">Görünüm takma adı → değer uzayı.</param>
/// <param name="UnreachableCoreFields">
/// Parser'ın doldurduğu ama normalizasyonun hiçbir kolona taşımadığı
/// <c>core</c> alanları. Boş olması bekleniyor; dolu olması bir kalem.
/// </param>
public sealed record VendorValueSpace(
    string Vendor,
    IReadOnlyList<string> Products,
    IReadOnlyDictionary<string, ColumnValueSpace> Columns,
    IReadOnlyList<string> UnreachableCoreFields);

/// <summary>
/// <b>Bir kolonun taşıyabileceği değerlerin, veriye bakmadan çıkarılması</b>
/// (T39).
///
/// <para>
/// Sorunun kaynağı ölçülmüş bir vaka: <c>nginx_5xx_burst</c> kuralı
/// <c>status|startswith: '5'</c> arıyor. Örneklemde 5xx yanıt olmaması ikincil;
/// asıl mesele <c>status</c> kolonunun <c>outcome</c>'dan gelmesi ve
/// <c>http_status_outcome.yaml</c>'ın HTTP kodunu <c>success</c>/<c>failure</c>'a
/// <b>çevirmesi</b>. O kolonda hiçbir zaman bir sayı durmuyor — yani kural,
/// örneklem düzelse bile eşleşemez.
/// </para>
///
/// <para>
/// Bu "başka ada inmiş"ten (kutu 2) bir adım öte: bilgi indi ama <b>başka bir
/// sözlüğe çevrildi</b>. Kutu 2'nin <c>[biçim: …]</c> işareti büyük/küçük harf
/// farkını yakalıyordu; bu, anlamsal çevirinin karşılığı.
/// </para>
///
/// <para>
/// <b>Neden veriye bakmıyor:</b> veri bir örneklem, şema bir taahhüt.
/// Örneklemde bir değerin bulunmaması "bugün yok" demek; şemanın o değeri
/// üretemiyor olması "hiçbir zaman olmayacak" demek. İkisi aynı tabloda aynı
/// görünüyor ve verdikleri iş emri zıt.
/// </para>
/// </summary>
public static class ColumnValueSpaces
{
    /// <summary>
    /// <c>map.core</c> alan adı → <c>events</c> kolonu.
    ///
    /// <para>
    /// Bu eşleme <c>EventNormalizer</c>'ın gövdesinde <b>kod olarak</b> duruyor
    /// (<c>Text(core, "host")</c> …) ve orada bir tabloya çevrilebilecek hâlde
    /// değil. Burası ikinci yazımı, ve ayrışması sessiz olmasın diye iki bekçisi
    /// var: sağdaki her ad <see cref="Bizigo.Storage.ClickHouse.EventFieldKinds"/>'ta
    /// tanımlı olmak zorunda, ve <see cref="ParserYamlLoader.CoreFields"/>'daki
    /// her ad burada ya eşlenmiş ya da <b>bilinçli olarak eşlenmemiş</b> sayılıp
    /// raporlanıyor.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> CoreToColumn = new(StringComparer.Ordinal)
    {
        ["host"] = "host",
        ["vendor"] = "vendor",
        ["product"] = "product",
        ["src_ip"] = "src_ip",
        ["dst_ip"] = "dst_ip",
        ["src_port"] = "src_port",
        ["dst_port"] = "dst_port",
        ["proto"] = "proto",
        ["action"] = "action",
        ["outcome"] = "outcome",
        ["user_name"] = "user_name",
        ["severity_num"] = "severity_num",

        // `ts` ve `body` kolona gidiyor ama değer uzayı sorusu anlamsız:
        // biri zaman, öbürü ham gövde. Eşlenmiş sayılıyorlar ki
        // "ulaşamıyor" listesine düşmesinler.
        ["ts"] = "ts",
        ["body"] = "body",
    };

    /// <summary><c>map.ocsf</c> alan adı → <c>events</c> kolonu (K8: yalnızca ikisi kolona yazılıyor).</summary>
    private static readonly Dictionary<string, string> OcsfToColumn = new(StringComparer.Ordinal)
    {
        ["class_uid"] = "ocsf_class_uid",
        ["activity_id"] = "ocsf_activity_id",
    };

    /// <summary>
    /// <paramref name="catalogDirectory"/> altındaki parser'ları okuyup vendor
    /// başına değer uzayını çıkarır.
    /// </summary>
    public static IReadOnlyList<VendorValueSpace> Build(
        string catalogDirectory,
        MappingTableCatalog tables,
        IReadOnlyList<OcsfViewColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(columns);

        var aliasOf = columns.ToDictionary(
            static column => column.Source,
            static column => column.Alias,
            StringComparer.Ordinal);

        var byVendor = new Dictionary<string, VendorAccumulator>(StringComparer.Ordinal);

        foreach (var file in Directory
                     .EnumerateFiles(catalogDirectory, "*.yaml", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var loaded = ParserYamlLoader.LoadFile(file);

            if (!loaded.Ok)
            {
                continue;
            }

            var definition = loaded.Value;
            var vendor = definition.Metadata.Vendor;

            if (vendor.Length == 0)
            {
                continue;
            }

            if (!byVendor.TryGetValue(vendor, out var accumulator))
            {
                accumulator = new VendorAccumulator(vendor);
                byVendor[vendor] = accumulator;
            }

            accumulator.Add(definition, tables, aliasOf);
        }

        return
        [
            .. byVendor.Values
                .Select(static accumulator => accumulator.Build())
                .OrderBy(static space => space.Vendor, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Bir değerin, verilen operatörle bu uzaydan üretilip üretilemeyeceği.
    ///
    /// <para>
    /// <b>Bilinmeyen operatör "erişilemez" demiyor.</b> Modellenmemiş bir
    /// operatörü erişilemez saymak, ölçümü kendi eksikliğini ürünün kusuru gibi
    /// göstermeye iter — bu turda iki kez ödenen dersin aynısı. Emin
    /// olunmayan her durumda cevap <c>true</c>.
    /// </para>
    ///
    /// <para>
    /// Karşılaştırma <b>büyük/küçük harf duyarsız</b>: Sigma'nın varsayılanı bu.
    /// <c>InvariantCultureIgnoreCase</c> değil <c>OrdinalIgnoreCase</c> —
    /// <c>tr-TR</c>'de <c>I</c> harfi sessizce kayıyor.
    /// </para>
    /// </summary>
    /// <summary>
    /// Operatör modellenmiş mi.
    ///
    /// <para>
    /// <b>"Metin ekseni yanılıyor" iddiası bunu gerektiriyor.</b> Modellenmemiş
    /// bir operatörde <see cref="CanSatisfy"/> güvenli tarafa düşüp <c>true</c>
    /// dönüyor; o <c>true</c>'yu "değer kolonda VAR" diye okumak, aracın
    /// bilmediğini bildiği gibi göstermek olurdu.
    /// </para>
    /// </summary>
    public static bool IsModelled(string @operator) =>
        @operator is "" or "equals" or "startswith" or "endswith" or "contains";

    public static bool CanSatisfy(ColumnValueSpace space, string @operator, string value)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(value);

        if (space.Kind == ValueSpaceKind.Absent)
        {
            return false;
        }

        if (space.Kind == ValueSpaceKind.Open)
        {
            return true;
        }

        return @operator switch
        {
            "" or "equals" => space.Values.Contains(value, StringComparer.OrdinalIgnoreCase),
            "startswith" => space.Values.Any(item => item.StartsWith(value, StringComparison.OrdinalIgnoreCase)),
            "endswith" => space.Values.Any(item => item.EndsWith(value, StringComparison.OrdinalIgnoreCase)),
            "contains" => space.Values.Any(item => item.Contains(value, StringComparison.OrdinalIgnoreCase)),

            // Modellenmemiş: `re`, `gte`, `lt`, `cidr`, `base64offset` …
            _ => true,
        };
    }

    private sealed class VendorAccumulator(string vendor)
    {
        private readonly Dictionary<string, ColumnAccumulator> _columns = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _products = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _unreachable = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _parsers = new(StringComparer.Ordinal);

        public void Add(
            ParserDefinition definition,
            MappingTableCatalog tables,
            IReadOnlyDictionary<string, string> aliasOf)
        {
            _parsers.Add(definition.Metadata.Id);

            if (definition.Metadata.Product.Length > 0)
            {
                _products.Add(definition.Metadata.Product);
            }

            Merge(definition, definition.Map.Core, CoreToColumn, tables, aliasOf, trackUnreachable: true);
            Merge(definition, definition.Map.Ocsf, OcsfToColumn, tables, aliasOf, trackUnreachable: false);
        }

        private void Merge(
            ParserDefinition definition,
            IReadOnlyDictionary<string, MapValue> map,
            IReadOnlyDictionary<string, string> toColumn,
            MappingTableCatalog tables,
            IReadOnlyDictionary<string, string> aliasOf,
            bool trackUnreachable)
        {
            foreach (var (field, value) in map)
            {
                if (!toColumn.TryGetValue(field, out var column))
                {
                    if (trackUnreachable)
                    {
                        // Parser dolduruyor, normalizasyon hiçbir kolona
                        // taşımıyor — bilgi yazma anında düşüyor.
                        _unreachable.Add($"{definition.Metadata.Id}: core.{field}");
                    }

                    continue;
                }

                if (!aliasOf.TryGetValue(column, out var alias))
                {
                    // Kolon var ama görünümde yok: Sigma o kolona hiç vuramaz.
                    continue;
                }

                if (!_columns.TryGetValue(alias, out var accumulator))
                {
                    accumulator = new ColumnAccumulator(alias, column);
                    _columns[alias] = accumulator;
                }

                accumulator.Add(definition.Metadata.Id, value, tables);
            }
        }

        public VendorValueSpace Build() => new(
            vendor,
            [.. _products],
            _columns.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value.Build(_parsers),
                StringComparer.Ordinal),
            [.. _unreachable]);
    }

    private sealed class ColumnAccumulator(string alias, string column)
    {
        private readonly SortedSet<string> _values = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _sources = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _mapped = new(StringComparer.Ordinal);
        private bool _open;

        public void Add(string parserId, MapValue value, MappingTableCatalog tables)
        {
            _mapped.Add(parserId);

            switch (value)
            {
                case LiteralMapValue literal:
                    _values.Add(Text(literal.Value));
                    _sources.Add($"{parserId}: sabit");
                    break;

                case LookupMapValue lookup:
                    // Bilinmeyen tablo derleme zamanında zaten hata; buraya
                    // gelirse uzayı AÇIK saymak tek güvenli davranış.
                    if (!tables.Contains(lookup.Table))
                    {
                        _open = true;
                        _sources.Add($"{parserId}: tablo yok ({lookup.Table})");
                        break;
                    }

                    foreach (var output in tables.Outputs(lookup.Table))
                    {
                        _values.Add(output);
                    }

                    if (lookup.Default is not null)
                    {
                        _values.Add(Text(lookup.Default));
                    }

                    _sources.Add($"{parserId}: {lookup.Table}");
                    break;

                default:
                    // Şablon: cihaz ne yazarsa. Uzay açık ve öyle kalmalı —
                    // tek bir açık parser bütün vendor'ı açık yapıyor.
                    _open = true;
                    _sources.Add($"{parserId}: şablon");
                    break;
            }
        }

        public ColumnValueSpace Build(IReadOnlyCollection<string> allParsers) => new(
            alias,
            column,
            _open ? ValueSpaceKind.Open : ValueSpaceKind.Closed,
            _open ? [] : [.. _values],
            [.. _sources],
            [.. allParsers.Where(parser => !_mapped.Contains(parser))]);

        private static string Text(object value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

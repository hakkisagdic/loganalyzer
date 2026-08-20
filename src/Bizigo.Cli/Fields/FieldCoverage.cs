using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Cli.Fields;

/// <param name="Text">Satırda hiçbir alana girmemiş metin parçası.</param>
/// <param name="Lines">Kaç örnek satırda görüldüğü.</param>
public sealed record UncapturedFragment(string Text, int Lines);

/// <param name="Key"><c>attrs</c> (görünümde <c>unmapped</c>) anahtarı.</param>
/// <param name="Lines">Kaç satırda bu şekilde indiği.</param>
/// <param name="Sample">Örnek bir değer — hangi bilgi olduğunu göstermek için.</param>
/// <param name="FromLine">
/// Değer ham satırda birebir geçiyor mu. <c>false</c> ise bu bir defter kaydı
/// (<c>bizigo.dispatch_tier</c> gibi), dosyadan gelen bir bilgi değil — kutu 2
/// yalnızca <c>true</c> olanlarla ilgileniyor.
/// </param>
/// <param name="Note">
/// Boş değilse: aynı değer bir OCSF kolonunda <b>başka biçimde</b> duruyor
/// (ör. <c>UDP</c> → <c>udp</c>). Yani bilgi kayıp değil, dönüştürülmüş.
/// </param>
public sealed record RelocatedField(string Key, int Lines, string Sample, bool FromLine, string Note);

/// <param name="Vendor"><c>vendor</c> değeri — ClickHouse'taki <c>device_vendor_name</c>.</param>
/// <param name="Lines">Bu vendor'ın örnek satır sayısı.</param>
/// <param name="Populated">Görünüm takma adı → dolduğu satır sayısı.</param>
/// <param name="AttributeKeys"><c>attrs</c> anahtarı → o anahtarı taşıyan satır sayısı.</param>
/// <param name="Relocated">Kutu 2: OCSF kolonuna değil <c>unmapped</c>'e inmiş bilgi.</param>
/// <param name="Uncaptured">Kutu 1: hiçbir alana girmemiş metin.</param>
/// <param name="UncapturedDropped">
/// Rapora sığmadığı için basılmayan parça sayısı. Sessiz kırpma "hepsi bu" diye
/// okunur.
/// </param>
public sealed record VendorFieldReport(
    string Vendor,
    int Lines,
    IReadOnlyDictionary<string, int> Populated,
    IReadOnlyDictionary<string, int> AttributeKeys,
    IReadOnlyList<RelocatedField> Relocated,
    IReadOnlyList<UncapturedFragment> Uncaptured,
    int UncapturedDropped);

/// <summary>Vendor raporları + kolon listesi; kutu 3 vendor'lar arası karşılaştırma gerektiriyor.</summary>
public sealed record FieldCoverageReport(
    IReadOnlyList<VendorFieldReport> Vendors,
    IReadOnlyList<string> Aliases)
{
    /// <summary>
    /// <b>Kutu 3a — küresel boş.</b> Hiçbir vendor'da dolmayan OCSF alanları:
    /// ya eşleme hiç yazılmamış ya da örneklem bu bilgiyi hiç taşımıyor.
    /// </summary>
    public IReadOnlyList<string> EmptyEverywhere() =>
        [.. Aliases.Where(alias => Vendors.All(vendor => vendor.Populated.GetValueOrDefault(alias) == 0))];

    /// <summary>
    /// <b>Kutu 3b — vendor'a özel boş.</b> Başka bir vendor'da dolan ama burada
    /// hiç dolmayan alanlar.
    ///
    /// <para>
    /// Bu ayrımın olmaması bir turda gerçek bir soruna yol açtı: küresel bir
    /// "bu alan eksik" listesi <c>activity_name</c>'i ifade edemiyor, çünkü alan
    /// FortiGate'te dolu ve RouterOS'ta boş. Aynı listede duran iki farklı
    /// durum, ikisi için de yanlış iş yaptırıyor.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> EmptyFor(VendorFieldReport vendor)
    {
        ArgumentNullException.ThrowIfNull(vendor);

        return
        [
            .. Aliases.Where(alias =>
                vendor.Populated.GetValueOrDefault(alias) == 0
                && Vendors.Any(other => other.Populated.GetValueOrDefault(alias) > 0)),
        ];
    }
}

/// <summary>
/// <b>Örnek dosyada duran bilginin <c>events_ocsf</c>'e ne kadarının indiğinin
/// ölçümü</b> (T39).
///
/// <para>
/// Soruyu soran şey Kapı 3: bir dizi Sigma kuralı hiçbir satır bulmuyor ve
/// bunun birden çok, birbirine hiç benzemeyen sebebi olabilir. Tabloda hepsi
/// aynı görünüyor — boş kolon. Ayırt edilmezse yanlış iş yapılır: olmayan bir
/// veriyi eşlemeye çalışmak, ya da var olan bir alanı örneklem eksikliği sanıp
/// geçmek.
/// </para>
///
/// <h3>Üç kutu</h3>
///
/// <list type="number">
/// <item><b>Dosyada var, hiçbir alana inmemiş.</b> Satırın hiçbir alan değeriyle
/// örtüşmeyen parçaları. Parser onu hiç görmemiş demektir.</item>
/// <item><b>İnmiş ama başka ada/biçime.</b> Değer <c>unmapped</c>'te duruyor,
/// OCSF kolonunda değil — RouterOS'un zincir adının <c>fw_chain</c>'e gitmesi
/// gibi. Bu bir <b>kayıp değil yer değiştirme</b> ve Sigma açısından farkı
/// yok: kural OCSF adına vuruyorsa yine bulamıyor.</item>
/// <item><b>Kolon var ama boş.</b> Ve bu <b>vendor'a özel</b> olabiliyor:
/// <c>activity_name</c> FortiGate'te dolu, RouterOS'ta hep boş. Küresel bir
/// liste bunu ifade edemiyor.</item>
/// </list>
///
/// <para>
/// <b>Araç eşanlamlı tablosu taşımıyor.</b> Hangi <c>unmapped</c> anahtarının
/// hangi OCSF kolonuna karşılık geldiğini iddia etmiyor: o tabloyu yazmak,
/// ölçümün cevaplaması istenen soruyu ölçümün girdisine taşımak olurdu. Üç kutu
/// yan yana basılıyor, eşleştirmeyi okuyan yapıyor.
/// </para>
///
/// <para>
/// <b>Kutu 1'in sınırı açık:</b> "yakalanmamış" bilgi demek değil. Ayraç, sabit
/// sözcük ve söz dizimi de yakalanmamış görünür ve parser'ın onları almaması
/// doğru davranış. Liste taranarak <i>veriye benzeyen</i> parçalar aranmalı;
/// araç bu ayrımı yapmıyor, çünkü yapabilmesi için neyin veri olduğunu bilmesi
/// gerekirdi — ki sorunun kendisi bu.
/// </para>
/// </summary>
public static class FieldCoverage
{
    /// <summary>
    /// Bir parçanın rapora girmesi için gereken en az uzunluk. Kısa parçalar
    /// neredeyse tamamen ayraç ve tek harfli kalıntı; eşiği düşürmek listeyi
    /// okunamaz yapıyor.
    /// </summary>
    private const int MinimumFragment = 4;

    /// <summary>Yakalanmış sayılmak için bir değerin en az uzunluğu.</summary>
    private const int MinimumValue = 2;

    /// <summary>Vendor başına basılan en fazla parça; kırpılan sayı raporlanıyor.</summary>
    private const int MaxFragments = 25;

    /// <summary>
    /// Kapsama sayılmayan kolonlar: gövdenin kendisi ve boru hattının ürettiği
    /// kimlikler. Gövde her şeyi kapsardı; kimlikler satırda zaten yok.
    /// </summary>
    private static readonly string[] NotEvidenceOfCapture =
    [
        "body", "raw_ref", "event_id", "signature_hash",
        "ts", "ingested_at", "owner_group", "source_id",
    ];

    public static FieldCoverageReport Measure(
        IReadOnlyList<LogEvent> events,
        IReadOnlyList<OcsfViewColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(columns);

        var measurable = columns
            .Where(column => EventFieldKinds.Of(column.Source) != EventFieldKind.Always)
            .ToList();

        var reports = new List<VendorFieldReport>();

        foreach (var group in events
                     .GroupBy(static e => e.Vendor.Length == 0 ? "<yok>" : e.Vendor, StringComparer.Ordinal)
                     .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            reports.Add(MeasureVendor(group.Key, [.. group], measurable));
        }

        return new FieldCoverageReport(reports, [.. measurable.Select(static column => column.Alias)]);
    }

    private static VendorFieldReport MeasureVendor(
        string vendor,
        IReadOnlyList<LogEvent> events,
        IReadOnlyList<OcsfViewColumn> columns)
    {
        var populated = new Dictionary<string, int>(StringComparer.Ordinal);
        var attributes = new Dictionary<string, int>(StringComparer.Ordinal);
        var fragments = new Dictionary<string, int>(StringComparer.Ordinal);
        var relocated = new Dictionary<string, RelocatedField>(StringComparer.Ordinal);

        foreach (var logEvent in events)
        {
            var values = ColumnValues(logEvent);

            // Bu satırda OCSF kolonlarına inen değerler — kutu 2'nin ölçüsü.
            var onColumns = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var column in columns)
            {
                var value = values[column.Source];

                if (!EventFieldKinds.IsPopulated(column.Source, value))
                {
                    continue;
                }

                populated[column.Alias] = populated.GetValueOrDefault(column.Alias) + 1;

                if (column.Source != "attrs")
                {
                    onColumns[column.Alias] = value;
                }
            }

            foreach (var (key, value) in logEvent.Attrs)
            {
                if (value.Length == 0)
                {
                    continue;
                }

                attributes[key] = attributes.GetValueOrDefault(key) + 1;
                Relocate(relocated, key, value, onColumns, logEvent.Body);
            }

            foreach (var fragment in Uncaptured(logEvent, values))
            {
                fragments[fragment] = fragments.GetValueOrDefault(fragment) + 1;
            }
        }

        var ordered = fragments
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        return new VendorFieldReport(
            vendor,
            events.Count,
            populated,
            attributes,
            [
                .. relocated.Values
                    .OrderByDescending(static entry => entry.Lines)
                    .ThenBy(static entry => entry.Key, StringComparer.Ordinal),
            ],
            [.. ordered.Take(MaxFragments).Select(pair => new UncapturedFragment(pair.Key, pair.Value))],
            Math.Max(0, ordered.Count - MaxFragments));
    }

    /// <summary>
    /// Bir <c>attrs</c> değeri OCSF kolonlarının hiçbirinde <b>birebir</b>
    /// yoksa "yer değiştirmiş" sayılıyor.
    ///
    /// <para>
    /// Büyük/küçük harf farkı ayrıca işaretleniyor: <c>proto_token=UDP</c> ile
    /// <c>connection_info_protocol_name=udp</c> aynı bilgi, ve o durumda cevap
    /// "kayıp" değil "dönüştürülmüş". İkisi karıştırılırsa eşleme yazmaya
    /// gerek olmadığı hâlde yazılır.
    /// </para>
    /// </summary>
    private static void Relocate(
        Dictionary<string, RelocatedField> relocated,
        string key,
        string value,
        IReadOnlyDictionary<string, string> onColumns,
        string body)
    {
        if (onColumns.Values.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        var variant = onColumns
            .FirstOrDefault(column => string.Equals(column.Value, value, StringComparison.OrdinalIgnoreCase));

        var note = variant.Key is null ? string.Empty : $"biçim: {variant.Key}";
        var fromLine = value.Length >= MinimumValue && body.Contains(value, StringComparison.Ordinal);

        relocated[key] = relocated.TryGetValue(key, out var existing)
            ? existing with { Lines = existing.Lines + 1 }
            : new RelocatedField(key, 1, value, fromLine, note);
    }

    /// <summary>
    /// <c>events</c> kolonu → metin değeri. <c>EventWriter</c>'ın yazdığı
    /// alanların aynısı; <see cref="EventFieldKinds.Unknown"/> bekçisi ikisinin
    /// ayrışmasını yakalıyor.
    /// </summary>
    private static Dictionary<string, string> ColumnValues(LogEvent e) => new(StringComparer.Ordinal)
    {
        ["ts"] = e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        ["ingested_at"] = e.IngestedAt.ToString("O", CultureInfo.InvariantCulture),
        ["time_source"] = e.TimeSource,
        ["event_id"] = e.EventId.ToString(),
        ["owner_group"] = e.OwnerGroup,
        ["source_id"] = e.SourceId,
        ["host"] = e.Host,
        ["vendor"] = e.Vendor,
        ["product"] = e.Product,
        ["parser_id"] = e.ParserId,
        ["parser_version"] = e.ParserVersion,
        ["parse_status"] = e.ParseStatus.ToString(),
        ["parse_generation"] = e.ParseGeneration.ToString(CultureInfo.InvariantCulture),
        ["encoding_detected"] = e.EncodingDetected,
        ["template_id"] = e.TemplateId,
        ["signature_hash"] = e.SignatureHash.ToString(CultureInfo.InvariantCulture),
        ["severity_num"] = e.SeverityNum.ToString(CultureInfo.InvariantCulture),
        ["ocsf_class_uid"] = e.OcsfClassUid.ToString(CultureInfo.InvariantCulture),
        ["ocsf_activity_id"] = e.OcsfActivityId.ToString(CultureInfo.InvariantCulture),
        ["src_ip"] = Address(e.SrcIp),
        ["dst_ip"] = Address(e.DstIp),
        ["src_port"] = e.SrcPort.ToString(CultureInfo.InvariantCulture),
        ["dst_port"] = e.DstPort.ToString(CultureInfo.InvariantCulture),
        ["proto"] = e.Proto,
        ["action"] = e.Action,
        ["outcome"] = e.Outcome,
        ["user_name"] = e.UserName,
        ["attrs"] = e.Attrs.Count > 0 ? "dolu" : string.Empty,
        ["body"] = e.Body,
        ["raw_ref"] = e.RawRef,
    };

    private static string Address(System.Net.IPAddress address) =>
        address.Equals(System.Net.IPAddress.IPv6Any) ? "::" : address.ToString();

    /// <summary>
    /// Gövdenin hiçbir alan değeriyle örtüşmeyen parçaları.
    ///
    /// <para>
    /// Alan <b>adına</b> değil <b>değerine</b> bakılıyor ve değer gövdede
    /// aranıyor. Bunun bir yan etkisi var ve bilgi taşıyor: parser bir değeri
    /// dönüştürdüyse (eşleme tablosundan <c>UDP</c> → <c>udp</c>) ham hâli
    /// gövdede kapsanmamış görünür. Bu bir kusur değil — ham hâl gerçekten alan
    /// olarak sorgulanamıyor ve <c>ILIKE</c> ile ham metne vuran bir Sigma
    /// kuralı tam da oradan yanlış sonuç üretiyor.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Uncaptured(LogEvent e, IReadOnlyDictionary<string, string> values)
    {
        var body = e.Body;

        if (body.Length == 0)
        {
            yield break;
        }

        var candidates = values
            .Where(pair => !NotEvidenceOfCapture.Contains(pair.Key, StringComparer.Ordinal))
            .Select(static pair => pair.Value)
            .Concat(e.Attrs.Values)
            .Where(static value => value.Length >= MinimumValue)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var covered = new bool[body.Length];

        foreach (var value in candidates.Where(value => !IsBlob(value, candidates)))
        {
            Mark(covered, body, value);
        }

        // Alan ADLARI da kapsama sayılıyor. `key=value` ve JSON biçimlerinde
        // anahtar satırın içinde duruyor ve alanın kimliğinin parçası:
        // `srcip=` kapsanmamış sayılsaydı FortiGate'in listesi baştan sona
        // ayraçla dolar, gerçek bulguyu görünmez yapardı. Serbest metinli
        // vendor'larda (ASA, RouterOS) anahtarlar satırda geçmediği için bu
        // adım hiçbir şeyi değiştirmiyor.
        foreach (var key in e.Attrs.Keys)
        {
            Mark(covered, body, key);
        }

        var start = -1;

        for (var i = 0; i <= body.Length; i++)
        {
            var isCovered = i == body.Length || covered[i];

            if (!isCovered && start < 0)
            {
                start = i;
            }
            else if (isCovered && start >= 0)
            {
                var fragment = body[start..i].Trim();
                start = -1;

                if (fragment.Length >= MinimumFragment)
                {
                    yield return fragment;
                }
            }
        }
    }

    /// <summary>
    /// Bir değer <b>blob</b> mu: içinde başka bir yakalanmış değer geçiyor mu.
    ///
    /// <para>
    /// Bu kuralın olmaması ölçümü <b>sessizce yalan söyletiyordu</b> ve ilk
    /// koşumda yakalandı: dört vendor'ın dördünde de "yakalanmamış metin: (yok)"
    /// çıkıyordu, yani parser her şeyi almış gibi görünüyordu. Sebep,
    /// <c>attrs['message']</c>'ın <b>satırın tamamı</b> olması — gövdeyi kendisi
    /// kapsıyor ve geriye hiçbir aralık bırakmıyordu. Cisco'nun
    /// <c>event_message</c>'ı, nginx'in <c>request</c>'i, FortiGate'in
    /// <c>msg</c>'si aynı sınıftan.
    /// </para>
    ///
    /// <para>
    /// Eşik yerine <b>yapı</b>ya bakılıyor: içinde daha küçük bir yakalanmış
    /// değer barındıran bir alan, o değeri ayrıca çözen bir alanın <i>üst</i>
    /// hâlidir ve Sigma açısından yalnızca <c>contains</c> ile
    /// adreslenebilir — yani "alan olarak inmiş" saymak yanıltıcı olur. Bedeli
    /// kabul edildi: iki alanın değeri tesadüfen iç içe geçerse (ör.
    /// <c>1234443</c> içinde <c>443</c>) üsttekinin aralığı kapsanmamış görünür.
    /// Fazla raporlamak, eksik raporlamaktan iyi.
    /// </para>
    /// </summary>
    private static bool IsBlob(string value, IReadOnlyList<string> candidates)
    {
        foreach (var other in candidates)
        {
            if (other.Length < value.Length && value.Contains(other, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Mark(bool[] covered, string body, string value)
    {
        if (value.Length < MinimumValue)
        {
            return;
        }

        var from = 0;

        while (from <= body.Length - value.Length)
        {
            var index = body.IndexOf(value, from, StringComparison.Ordinal);

            if (index < 0)
            {
                return;
            }

            for (var i = index; i < index + value.Length; i++)
            {
                covered[i] = true;
            }

            from = index + 1;
        }
    }
}

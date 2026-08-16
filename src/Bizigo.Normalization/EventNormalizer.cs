using System.Globalization;
using System.Net;
using Bizigo.Contracts;
using Bizigo.Storage.Raw;

namespace Bizigo.Normalization;

/// <summary>
/// <see cref="ParsedEvent"/> → <see cref="LogEvent"/> (F1 §5, K8).
///
/// <para>
/// <b>Yalnızca <c>core</c> yazılıyor.</b> OCSF ve OTel alanları saklanmıyor,
/// <c>core</c> + <c>attrs</c> üzerinden ClickHouse görünümleriyle türetiliyor.
/// İkisini de materyalize etmek depolamayı ~2 katına, mapping bakımını iki
/// katına çıkarırdı. Saklanan tek istisna <c>ocsf_class_uid</c> ve
/// <c>ocsf_activity_id</c>: filtrelemede ucuz ve sık kullanılıyorlar.
/// </para>
///
/// <para>
/// Parser'ın ürettiği diğer OCSF/OTel değerleri <c>attrs</c>'a <c>ocsf.</c> ve
/// <c>otel.</c> önekleriyle giriyor; görünümler oradan okuyor. Böylece yeni bir
/// alan eklemek şema göçü değil, YAML değişikliği oluyor.
/// </para>
/// </summary>
public sealed class EventNormalizer(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public LogEvent Normalize(ParsedEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var core = source.Parsed.Core;
        var attrs = BuildAttributes(source);

        return new LogEvent
        {
            EventId = source.Raw.EventId,
            Timestamp = ResolveTimestamp(source),
            IngestedAt = _time.GetUtcNow(),

            OwnerGroup = source.Source.OwnerGroup,
            SourceId = source.Source.SourceId,
            Host = Text(core, "host") is { Length: > 0 } host ? host : source.Raw.SourceKey,
            Vendor = Text(core, "vendor"),
            Product = Text(core, "product"),

            ParserId = source.Parsed.ParserId,
            ParserVersion = source.Parsed.ParserVersion,
            ParseStatus = source.Parsed.Status,
            ParseGeneration = 1,
            EncodingDetected = source.EncodingName,
            TemplateId = string.Empty,

            SeverityNum = Byte(core, "severity_num"),
            OcsfClassUid = UInt32(source.Parsed.Ocsf, "class_uid"),
            OcsfActivityId = UInt16(source.Parsed.Ocsf, "activity_id"),

            SrcIp = Ip(core, "src_ip"),
            DstIp = Ip(core, "dst_ip"),
            SrcPort = UInt16(core, "src_port"),
            DstPort = UInt16(core, "dst_port"),
            Proto = Text(core, "proto"),
            Action = Text(core, "action"),
            Outcome = Text(core, "outcome"),
            UserName = Text(core, "user_name"),

            Attrs = attrs,
            Body = source.Decoded,
            RawRef = RawRefFor(source),
        };
    }

    /// <summary>
    /// <c>raw_ref</c>: olayın ham hâlini içeren arşiv <b>ön eki</b>
    /// (<c>raw/{owner_group}/{yyyy}/{MM}/{dd}/{HH}/{source_class}/</c>).
    ///
    /// <para>
    /// <b>Neden bayt konumu değil:</b> ingest boru hattı ile arşiv yükleyici
    /// bilinçli olarak bağımsız çalışıyor (F1 §2.3). Olay satırı yazıldığında
    /// nesne henüz oluşmamış oluyor, dolayısıyla <c>offset</c> bilinemiyor.
    /// Alternatifler tartıldı:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>Ayrı bir <c>raw_index</c> tablosu — O(1) okuma verirdi ama hem
    /// yükleyicinin hem replay'in bakması gereken <b>ikinci bir gerçek kaynağı</b>
    /// doğururdu; sessizce sürüklenmesi manifest'in önlemek için var olduğu hata
    /// sınıfının aynısı.</item>
    /// <item>Yüklemeyi olay yazımından önce yapmak — iki yolu birbirine bağlar ve
    /// ClickHouse yazımını S3 gecikmesine tabi kılardı.</item>
    /// </list>
    ///
    /// <para>
    /// Seçilen yol: ön ek yazma anında <b>hesaplanabiliyor</b> (grup, saat ve
    /// kaynak sınıfı biliniyor), manifest sorgusunun anahtarıyla birebir örtüşüyor
    /// ve tek gerçek kaynak arşivin kendisi olarak kalıyor. Bedeli, tek bir kaydı
    /// okumak için nesnenin açılıp <c>event_id</c>'nin taranması — replay zaten
    /// nesnenin tamamını okuduğu için ona ek maliyet getirmiyor.
    /// </para>
    /// </summary>
    private static string RawRefFor(ParsedEvent source)
    {
        var timestamp = (source.Raw.ObservedAt ?? source.Raw.ReceivedAt).UtcDateTime;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{RawObjectKey.Prefix}/{source.Source.OwnerGroup.Trim('/')}/" +
            $"{timestamp:yyyy}/{timestamp:MM}/{timestamp:dd}/{timestamp:HH}/{source.Source.SourceClass}/");
    }

    /// <summary>
    /// Olay zamanı: parser'ın çözdüğü damga → cihazın/collector'ın damgası →
    /// ingest anı. Hiçbiri yoksa satır zamansız kalmaz; yanlış zamandansa
    /// "yaklaşık doğru" zaman daha kullanışlı ve <c>ts</c> bölümleme anahtarı.
    /// </summary>
    private static DateTimeOffset ResolveTimestamp(ParsedEvent source) =>
        source.Parsed.Timestamp ?? source.Raw.ObservedAt ?? source.Raw.ReceivedAt;

    private static Dictionary<string, string> BuildAttributes(ParsedEvent source)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in source.Parsed.Fields)
        {
            var text = Stringify(value);
            if (text.Length > 0)
            {
                attrs[key] = text;
            }
        }

        // Görünümlerin okuduğu türetme girdileri. Önek olmadan parser'ın kendi
        // alanlarıyla çakışabilirdi.
        Merge(attrs, source.Parsed.Ocsf, "ocsf.");
        Merge(attrs, source.Parsed.Otel, "otel.");

        if (!source.Source.IsKnown)
        {
            // Envanterde olmayan kaynak sorguda görünür olmalı; `_unassigned`
            // grubu tek başına "neden" sorusunu cevaplamıyor.
            attrs["bizigo.unassigned_source_key"] = source.Raw.SourceKey;
        }

        attrs["bizigo.dispatch_tier"] = source.Tier.ToString();

        return attrs;
    }

    private static void Merge(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, object?> values,
        string prefix)
    {
        foreach (var (key, value) in values)
        {
            var text = Stringify(value);
            if (text.Length > 0)
            {
                target[prefix + key] = text;
            }
        }
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Text(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Stringify(value) : string.Empty;

    private static byte Byte(IReadOnlyDictionary<string, object?> values, string key) =>
        (byte)Math.Clamp(Int64(values, key), 0, byte.MaxValue);

    private static ushort UInt16(IReadOnlyDictionary<string, object?> values, string key) =>
        (ushort)Math.Clamp(Int64(values, key), 0, ushort.MaxValue);

    private static uint UInt32(IReadOnlyDictionary<string, object?> values, string key) =>
        (uint)Math.Clamp(Int64(values, key), 0, uint.MaxValue);

    private static long Int64(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            double d => (long)d,
            // Parser çıktısı sıklıkla string: grok her yakalamayı metin verir ve
            // `convert` adımı kullanılmamış olabilir.
            string text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// IPv4 → <c>::ffff:a.b.c.d</c>. Çözülemeyen değer <c>::</c> oluyor: sorgu
    /// tarafında "adres yok" ile "adres bozuk" ayrımı yapılmıyor, ikisi de
    /// filtrelenebilir tek bir değere düşüyor.
    /// </summary>
    private static IPAddress Ip(IReadOnlyDictionary<string, object?> values, string key)
    {
        var text = Text(values, key);

        if (text.Length == 0 || !IPAddress.TryParse(text, out var address))
        {
            return IPAddress.IPv6Any;
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? address.MapToIPv6()
            : address;
    }
}

using System.Net;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

/// <summary>
/// T07 (F1 §5, K8). Yazılan tek gerçek <c>core</c>; OCSF/OTel türetiliyor.
/// </summary>
public sealed class EventNormalizerTests
{
    private static readonly DateTimeOffset Received = new(2026, 8, 16, 12, 30, 0, TimeSpan.Zero);
    private readonly EventNormalizer _normalizer = new();

    private static ResolvedSource Source(
        string ownerGroup = "network/core",
        string sourceClass = "firewall",
        bool known = true) =>
        new("fg-ankara-01", ownerGroup, sourceClass, "auto", "fortinet.traffic", known);

    private static ParsedEvent Event(
        IReadOnlyDictionary<string, object?>? core = null,
        IReadOnlyDictionary<string, object?>? ocsf = null,
        IReadOnlyDictionary<string, object?>? otel = null,
        IReadOnlyDictionary<string, object?>? fields = null,
        DateTimeOffset? parsedTimestamp = null,
        ResolvedSource? source = null,
        ParseStatus status = ParseStatus.Ok,
        DateTimeOffset? observedAt = null,
        IReadOnlyList<string>? tags = null)
    {
        var raw = new RawRecord
        {
            EventId = Guid.CreateVersion7(Received),
            ReceivedAt = Received,
            ObservedAt = observedAt,
            SourceKey = "10.1.2.3",
            Body = Encoding.UTF8.GetBytes("ham satır"),
        };

        var parsed = new ParseResult
        {
            ParserId = "fortinet.traffic",
            ParserVersion = "1.2.0",
            Status = status,
            Fields = fields ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            Core = core ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            Ocsf = ocsf ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            Otel = otel ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            Timestamp = parsedTimestamp,
            Tags = tags ?? [],
        };

        return new ParsedEvent(
            raw,
            "çözülmüş gövde",
            "windows-1254",
            source ?? Source(),
            parsed,
            DispatchTier.InventoryBound);
    }

    [Fact]
    public void Core_alanlari_dogru_tiplere_ceviriliyor()
    {
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["host"] = "fw-01",
            ["src_port"] = "41022",
            ["dst_port"] = 443,
            ["proto"] = "tcp",
            ["action"] = "accept",
            ["outcome"] = "success",
            ["user_name"] = "ahmet",
            ["severity_num"] = "5",
        }));

        Assert.Equal("fw-01", result.Host);
        Assert.Equal(41022, result.SrcPort);
        Assert.Equal(443, result.DstPort);
        Assert.Equal("tcp", result.Proto);
        Assert.Equal((byte)5, result.SeverityNum);
    }

    [Fact]
    public void Grokun_metin_ciktisi_sayiya_ceviriliyor()
    {
        // grok her yakalamayı string verir; `convert` adımı kullanılmamış olabilir.
        // Burada sessizce sıfıra düşmek portları kaybetmek demek olurdu.
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["src_port"] = "65535",
        }));

        Assert.Equal(65535, result.SrcPort);
    }

    [Fact]
    public void IPv4_adresi_v6_esleniyor()
    {
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["src_ip"] = "10.1.2.3",
        }));

        // Tek kolonda tutuluyor; iki ayrı kolon her sorguya bir OR eklerdi.
        Assert.Equal(IPAddress.Parse("10.1.2.3").MapToIPv6(), result.SrcIp);
    }

    [Fact]
    public void IPv6_adresi_oldugu_gibi_kaliyor()
    {
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dst_ip"] = "2001:db8::1",
        }));

        Assert.Equal(IPAddress.Parse("2001:db8::1"), result.DstIp);
    }

    [Fact]
    public void Cozulemeyen_adres_bos_degere_dusuyor()
    {
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["src_ip"] = "bu bir IP değil",
        }));

        Assert.Equal(IPAddress.IPv6Any, result.SrcIp);
    }

    [Fact]
    public void Sadece_class_uid_ve_activity_id_kolona_yaziliyor()
    {
        var result = _normalizer.Normalize(Event(ocsf: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["class_uid"] = 4001,
            ["activity_id"] = 6,
            ["disposition_id"] = 2,
        }));

        Assert.Equal(4001u, result.OcsfClassUid);
        Assert.Equal((ushort)6, result.OcsfActivityId);

        // Kalan OCSF alanları kolona değil `attrs`a; görünüm oradan türetiyor.
        Assert.Equal("2", result.Attrs["ocsf.disposition_id"]);
    }

    [Fact]
    public void Otel_alanlari_onekle_attrs_a_giriyor()
    {
        var result = _normalizer.Normalize(Event(otel: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["network.transport"] = "tcp",
        }));

        Assert.Equal("tcp", result.Attrs["otel.network.transport"]);
    }

    [Fact]
    public void Parser_alanlari_attrs_a_giriyor()
    {
        var result = _normalizer.Normalize(Event(fields: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["devid"] = "FG100E",
            ["bos"] = null,
        }));

        Assert.Equal("FG100E", result.Attrs["devid"]);
        Assert.False(result.Attrs.ContainsKey("bos"));
    }

    /// <summary>
    /// Üç kademeli zaman çözümü ve <b>her kademenin kendini bildirmesi</b>.
    ///
    /// <para>
    /// Değerin kendisi kadar kaynağı da önemli: gözlem zamanına düşmüş bir
    /// olayın gerçek zamanı dakikalarca önce olabilir ve RCA'nın korelasyon
    /// penceresi bunu bilmeden kayar. Kolon olmadan aşağı akış "bu 14:03'te
    /// oldu" ile "bunu 14:03'te gördük" arasını ayıramıyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Zaman_kademesi_ve_kaynagi_birlikte_cozuluyor()
    {
        var parsedTime = new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);
        var observedTime = new DateTimeOffset(2026, 8, 15, 3, 5, 0, TimeSpan.Zero);

        var parsed = _normalizer.Normalize(Event(parsedTimestamp: parsedTime, observedAt: observedTime));
        Assert.Equal(parsedTime, parsed.Timestamp);
        Assert.Equal(TimeSources.Parsed, parsed.TimeSource);

        // Parser çözemedi ama collector gördü.
        var observed = _normalizer.Normalize(Event(observedAt: observedTime));
        Assert.Equal(observedTime, observed.Timestamp);
        Assert.Equal(TimeSources.Observed, observed.TimeSource);

        // Hiçbiri yok: satır zamansız kalmıyor çünkü `ts` bölümleme anahtarı.
        var received = _normalizer.Normalize(Event());
        Assert.Equal(Received, received.Timestamp);
        Assert.Equal(TimeSources.Received, received.TimeSource);
    }

    /// <summary>
    /// Parser'ın etiketleri olayla birlikte iniyor. Yazılmazlarsa parser'ın satır
    /// hakkında <b>söylediği</b> şey kayboluyor — <c>cisco.asa</c> tarih taşımayan
    /// satırı <c>_asa_no_timestamp</c> ile işaretliyor ve o bilgi aşağı akışta
    /// hiç yoktu.
    /// </summary>
    [Fact]
    public void Parser_etiketleri_attrs_a_giriyor()
    {
        var result = _normalizer.Normalize(Event(tags: ["_asa_no_timestamp", "ipv6"]));

        Assert.Equal("_asa_no_timestamp,ipv6", result.Attrs["bizigo.tags"]);

        // Etiketsiz olay boş bir anahtar taşımıyor: her satıra bedel bindirmez.
        Assert.False(_normalizer.Normalize(Event()).Attrs.ContainsKey("bizigo.tags"));
    }

    /// <summary>
    /// Parser'ın <b>şikâyetleri</b> de olayla birlikte iniyor (T16).
    ///
    /// <para>
    /// <c>parse_status=partial</c> tek başına "bir şey eksik" diyor ama hangi
    /// adımın neden takıldığını söylemiyor; o bilgi <c>ParseContext</c> içinde
    /// vardı ve ClickHouse'a hiç ulaşmıyordu. Sebebi olmayan bir <c>partial</c>,
    /// olay detayında cevaplanamayan bir soru bırakıyor — F1'in "sessiz bozulma"
    /// sınıfından.
    /// </para>
    /// </summary>
    [Fact]
    public void Cozumleme_sorunlari_attrs_a_giriyor()
    {
        var withIssues = Event(status: ParseStatus.Partial) with { };
        var parsed = withIssues.Parsed with
        {
            Issues = [new ParseIssue("date", "alan 'log_timestamp' yok")],
        };

        var result = _normalizer.Normalize(withIssues with { Parsed = parsed });

        Assert.Equal("date: alan 'log_timestamp' yok", result.Attrs["bizigo.parse_issues"]);

        // Sorunsuz olay boş bir anahtar taşımıyor — bu satırların çoğunluğu.
        Assert.False(_normalizer.Normalize(Event()).Attrs.ContainsKey("bizigo.parse_issues"));
    }

    /// <summary>
    /// Birden fazla sorun tek anahtarda birleşiyor: <c>mapKeys</c> bloom filtresi
    /// anahtar kümesi üzerinde ve adım başına anahtar açmak o indeksi seyreltirdi
    /// (etiketlerdeki gerekçenin aynısı).
    /// </summary>
    [Fact]
    public void Birden_fazla_sorun_tek_anahtarda_birlesiyor()
    {
        var source = Event(status: ParseStatus.Partial);
        var parsed = source.Parsed with
        {
            Issues =
            [
                new ParseIssue("grok", "desen uymadı"),
                new ParseIssue("date", "biçim çözülemedi"),
            ],
        };

        var result = _normalizer.Normalize(source with { Parsed = parsed });

        Assert.Equal(
            "grok: desen uymadı | date: biçim çözülemedi",
            result.Attrs["bizigo.parse_issues"]);
    }

    [Fact]
    public void Raw_ref_arsiv_on_ekini_veriyor()
    {
        var result = _normalizer.Normalize(Event());

        // Ön ek yazma anında hesaplanabiliyor ve manifest sorgusunun anahtarıyla
        // birebir örtüşüyor — ikinci bir gerçek kaynak doğmuyor.
        Assert.Equal("raw/network/core/2026/08/16/12/firewall/", result.RawRef);
    }

    [Fact]
    public void Raw_ref_bastaki_egik_cizgiyi_atiyor()
    {
        // Keycloak grup adlarını "/network/core" biçiminde basıyor.
        var result = _normalizer.Normalize(Event(source: Source(ownerGroup: "/network/core")));

        Assert.StartsWith("raw/network/core/", result.RawRef, StringComparison.Ordinal);
    }

    [Fact]
    public void Envanterde_olmayan_kaynak_attrs_ta_isaretleniyor()
    {
        var result = _normalizer.Normalize(Event(
            source: Source(ownerGroup: OwnerGroups.Unassigned, known: false)));

        // `_unassigned` tek başına "neden" sorusunu cevaplamıyor.
        Assert.Equal("10.1.2.3", result.Attrs["bizigo.unassigned_source_key"]);
    }

    [Fact]
    public void Dispatch_kademesi_attrs_ta_gorunuyor()
    {
        var result = _normalizer.Normalize(Event());

        Assert.Equal("InventoryBound", result.Attrs["bizigo.dispatch_tier"]);
    }

    [Fact]
    public void Kodlama_ve_govde_tasiniyor()
    {
        var result = _normalizer.Normalize(Event());

        Assert.Equal("windows-1254", result.EncodingDetected);
        Assert.Equal("çözülmüş gövde", result.Body);
    }

    [Fact]
    public void Basarisiz_ayristirma_da_olay_uretiyor()
    {
        var result = _normalizer.Normalize(Event(status: ParseStatus.Failed));

        // Satır kaybolmuyor: `failed` olarak yazılıyor ve replay ile düzeltilebiliyor.
        Assert.Equal(ParseStatus.Failed, result.ParseStatus);
        Assert.Equal("çözülmüş gövde", result.Body);
    }

    [Fact]
    public void Host_yoksa_kaynak_anahtarina_dusuyor()
    {
        var result = _normalizer.Normalize(Event());

        Assert.Equal("10.1.2.3", result.Host);
    }

    [Fact]
    public void Kapsam_grubu_kaynaktan_geliyor()
    {
        // Olayın kendi alanları grubu DEĞİŞTİREMEZ.
        var result = _normalizer.Normalize(Event(core: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner_group"] = "baska/grup",
        }));

        Assert.Equal("network/core", result.OwnerGroup);
    }
}

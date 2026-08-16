using System.Net;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;

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
        ParseStatus status = ParseStatus.Ok)
    {
        var raw = new RawRecord
        {
            EventId = Guid.CreateVersion7(Received),
            ReceivedAt = Received,
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

    [Fact]
    public void Zaman_oncelik_sirasi_parser_sonra_ingest()
    {
        var parsedTime = new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal(parsedTime, _normalizer.Normalize(Event(parsedTimestamp: parsedTime)).Timestamp);

        // Parser damga çözemediyse satır zamansız kalmıyor: `ts` bölümleme anahtarı.
        Assert.Equal(Received, _normalizer.Normalize(Event()).Timestamp);
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

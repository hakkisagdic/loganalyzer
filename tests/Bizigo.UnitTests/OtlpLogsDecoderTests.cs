using System.Text;
using System.Text.Json;
using Bizigo.Ingest.Otlp;

namespace Bizigo.UnitTests;

/// <summary>
/// F1 §2.1 + §2.4. En kritik test <see cref="Bytes_govde_bayt_bayt_korunuyor"/>:
/// collector'da <c>encoding: nop</c> ile korunan ham baytların bu sınırda da
/// bozulmadan geçmesi, ham arşiv + replay zincirinin ilk halkası.
/// </summary>
public sealed class OtlpLogsDecoderTests
{
    private readonly OtlpLogsDecoder _decoder = new();
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// OTLP/JSON gövdesi elle kuruluyor: üretilen protobuf sınıfları
    /// <c>internal</c> (bilerek), yani test onları göremez. Bu aslında iyi bir
    /// kısıt — çözücüyü <b>dışarıdan</b>, gerçek bir collector gibi sınıyoruz.
    /// </summary>
    private static byte[] JsonRequest(object logRecord, object? resourceAttributes = null)
    {
        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new { attributes = resourceAttributes ?? Array.Empty<object>() },
                    scopeLogs = new[]
                    {
                        new { logRecords = new[] { logRecord } },
                    },
                },
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static object Attribute(string key, object value) =>
        new { key, value };

    [Fact]
    public void Bytes_govde_bayt_bayt_korunuyor()
    {
        // windows-1254 "işlem başarısız" — UTF-8 olarak GEÇERSİZ.
        var raw = Encoding.GetEncoding("windows-1254").GetBytes("işlem başarısız");

        var json = JsonRequest(new
        {
            body = new { bytesValue = Convert.ToBase64String(raw) },
        });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        var record = Assert.Single(records);
        Assert.Equal(raw, record.Body.ToArray());
    }

    [Fact]
    public void String_govde_utf8_bayta_ceviriliyor()
    {
        var json = JsonRequest(new
        {
            body = new { stringValue = "bağlantı düştü" },
        });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Equal("bağlantı düştü", Encoding.UTF8.GetString(records[0].Body.Span));
    }

    [Fact]
    public void Kaynak_anahtari_oncelik_sirasina_gore_secilliyor()
    {
        var json = JsonRequest(
            new
            {
                body = new { stringValue = "x" },
                attributes = new[]
                {
                    Attribute("net.peer.ip", new { stringValue = "10.1.2.3" }),
                    Attribute("bizigo.source_key", new { stringValue = "fg-ankara-01" }),
                },
            });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        // Elle atanan anahtar, tahmin edilen IP'yi yener.
        Assert.Equal("fg-ankara-01", records[0].SourceKey);
    }

    [Fact]
    public void Kaynak_anahtari_yoksa_bos_kaliyor_kayit_reddedilmiyor()
    {
        var json = JsonRequest(new { body = new { stringValue = "x" } });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        // Reddetmek veri kaybı demek; `_unassigned` grubuna düşmesi T06'nın işi.
        Assert.Equal(string.Empty, Assert.Single(records).SourceKey);
    }

    [Fact]
    public void Tasima_ozniteligi_okunuyor()
    {
        var json = JsonRequest(new
        {
            body = new { stringValue = "x" },
            attributes = new[] { Attribute("bizigo.transport", new { stringValue = "syslog-udp" }) },
        });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Equal("syslog-udp", records[0].TransportProto);
    }

    [Fact]
    public void Tasima_belirtilmemisse_otlp_http_varsayiliyor()
    {
        var json = JsonRequest(new { body = new { stringValue = "x" } });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Equal("otlp-http", records[0].TransportProto);
    }

    [Fact]
    public void Kaynak_oznitelikleri_resource_onekiyle_geliyor()
    {
        var json = JsonRequest(
            new { body = new { stringValue = "x" } },
            resourceAttributes: new[] { Attribute("service.name", new { stringValue = "fw" }) });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Equal("fw", records[0].Attributes["resource.service.name"]);
    }

    [Fact]
    public void Bilinmeyen_alan_istegi_reddetmiyor()
    {
        // Collector sürümü bizden önde olabilir. Katı çözümleme burada veri kaybı olurdu.
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[
              {"body":{"stringValue":"x"},"buGelecektekiAlan":"deger"}]}]}]}
            """u8.ToArray();

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Single(records);
    }

    [Fact]
    public void Bos_export_bos_liste_veriyor()
    {
        var records = _decoder.Decode(
            """{"resourceLogs":[]}"""u8.ToArray(),
            OtlpLogsDecoder.JsonContentType,
            Now);

        Assert.Empty(records);
    }

    [Fact]
    public void Desteklenmeyen_content_type_reddediliyor()
    {
        Assert.Throws<OtlpDecodeException>(
            () => _decoder.Decode(new byte[] { 1, 2, 3 }, "text/plain", Now));
    }

    [Fact]
    public void Bozuk_json_anlamli_hata_veriyor()
    {
        Assert.Throws<OtlpDecodeException>(
            () => _decoder.Decode("{bozuk"u8.ToArray(), OtlpLogsDecoder.JsonContentType, Now));
    }

    [Fact]
    public void Bozuk_protobuf_anlamli_hata_veriyor()
    {
        Assert.Throws<OtlpDecodeException>(
            () => _decoder.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, OtlpLogsDecoder.ProtobufContentType, Now));
    }

    [Fact]
    public void Content_type_parametreli_gelse_de_taniniyor()
    {
        var json = JsonRequest(new { body = new { stringValue = "x" } });

        var records = _decoder.Decode(json, "application/json; charset=utf-8", Now);

        Assert.Single(records);
    }

    [Fact]
    public void Zaman_damgasi_nanosaniyeden_cozuluyor()
    {
        var unixNano = 1_755_000_000_000_000_000UL;

        var json = JsonRequest(new
        {
            body = new { stringValue = "x" },
            timeUnixNano = unixNano.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        var records = _decoder.Decode(json, OtlpLogsDecoder.JsonContentType, Now);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_755_000_000_000),
            records[0].ObservedAt);
    }
}

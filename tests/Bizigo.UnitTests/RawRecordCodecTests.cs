using System.Text;
using Bizigo.Contracts;

namespace Bizigo.UnitTests;

/// <summary>
/// WAL yükü ile ham arşiv satırı <b>aynı</b> format (F1 §7.1). Tur-gidiş burada
/// kırılırsa replay geçmişi okuyamaz — bu yüzden en zor baytlarla sınanıyor.
/// </summary>
public sealed class RawRecordCodecTests
{
    private static RawRecord Sample(ReadOnlyMemory<byte> body) => new()
    {
        EventId = Guid.Parse("01936d2a-0000-7000-8000-000000000001"),
        ReceivedAt = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
        SourceKey = "fg-ankara-01",
        TransportProto = "syslog-tcp",
        TransportPeer = "10.1.2.3:41022",
        EncodingDeclared = "windows-1254",
        Body = body,
    };

    [Fact]
    public void Tur_gidis_alanlari_koruyor()
    {
        var original = Sample(Encoding.UTF8.GetBytes("bağlantı düştü"));

        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(original));

        Assert.Equal(original.EventId, restored.EventId);
        Assert.Equal(original.ReceivedAt, restored.ReceivedAt);
        Assert.Equal(original.SourceKey, restored.SourceKey);
        Assert.Equal(original.TransportProto, restored.TransportProto);
        Assert.Equal(original.TransportPeer, restored.TransportPeer);
        Assert.Equal(original.EncodingDeclared, restored.EncodingDeclared);
        Assert.Equal(original.Body.ToArray(), restored.Body.ToArray());
    }

    [Fact]
    public void Gecersiz_utf8_baytlari_kayipsiz_tasiniyor()
    {
        // JSON string olarak taşınamayacak baytlar: NUL, tek başına 0xC3, 0xFF.
        byte[] body = [0x00, 0xC3, 0x28, 0xFF, 0x0A];

        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(Sample(body)));

        Assert.Equal(body, restored.Body.ToArray());
    }

    [Fact]
    public void Owner_group_bos_yazilip_bos_okunuyor()
    {
        // WAL aşamasında çözülmemiş olması normal; alanın VARLIĞI formatın parçası.
        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(Sample("x"u8.ToArray())));

        Assert.Equal(string.Empty, restored.OwnerGroup);
        Assert.Equal(string.Empty, restored.SourceId);
    }

    [Fact]
    public void Cozulmus_alanlar_yazilabiliyor()
    {
        var record = Sample("x"u8.ToArray()) with
        {
            OwnerGroup = "network/core",
            SourceId = "fg-ankara-01",
        };

        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(record));

        Assert.Equal("network/core", restored.OwnerGroup);
        Assert.Equal("fg-ankara-01", restored.SourceId);
    }

    [Fact]
    public void Oznitelikler_korunuyor()
    {
        var record = Sample("x"u8.ToArray()) with
        {
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resource.service.name"] = "fw",
                ["net.peer.ip"] = "10.1.2.3",
            },
        };

        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(record));

        Assert.Equal(2, restored.Attributes.Count);
        Assert.Equal("fw", restored.Attributes["resource.service.name"]);
    }

    [Fact]
    public void Satir_tek_satir_uretiyor()
    {
        // NDJSON şartı: gövdede satır sonu olsa bile çıktı tek satır kalmalı.
        var line = RawRecordCodec.ToLine(Sample("birinci\nikinci"u8.ToArray()));

        Assert.DoesNotContain((byte)'\n', line);
    }

    [Fact]
    public void Bos_govde_tasinabiliyor()
    {
        var restored = RawRecordCodec.Read(RawRecordCodec.ToLine(Sample(ReadOnlyMemory<byte>.Empty)));

        Assert.Empty(restored.Body.ToArray());
    }
}

using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;
using Bizigo.Storage.Raw;

namespace Bizigo.UnitTests;

public sealed class RawObjectBuilderTests
{
    private static RawRecord Record(string body, string sourceKey = "fg-01") => new()
    {
        EventId = Guid.CreateVersion7(),
        ReceivedAt = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
        SourceKey = sourceKey,
        Body = Encoding.UTF8.GetBytes(body),
    };

    [Fact]
    public void Kayitlar_sikistirilip_geri_acilabiliyor()
    {
        var builder = new RawObjectBuilder();
        var ts = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 100; i++)
        {
            var record = Record($"satır-{i}");
            builder.Add(record.EventId, ts, RawRecordCodec.ToLine(record));
        }

        var built = builder.Build(3);
        var plain = Encoding.UTF8.GetString(RawObjectBuilder.Decompress(built.Compressed));

        Assert.Equal(100, built.EventCount);
        Assert.Equal(100, plain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Sha256_sikistirilmis_baytlarin_uzerinden_aliniyor()
    {
        var builder = new RawObjectBuilder();
        var record = Record("veri");
        builder.Add(record.EventId, record.ReceivedAt, RawRecordCodec.ToLine(record));

        var built = builder.Build(3);

        // Depodan geri okunan şey sıkıştırılmış hâli; doğrulama onun üzerinden
        // yapılmalı ki sıkıştırıcı sürümü değişince sahte uyuşmazlık çıkmasın.
        var expected = Convert.ToHexString(SHA256.HashData(built.Compressed)).ToLowerInvariant();
        Assert.Equal(expected, built.Sha256);
    }

    [Fact]
    public void Raw_ref_konumu_dogru_satiri_gosteriyor()
    {
        var builder = new RawObjectBuilder();
        var ts = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 20).Select(i => Record($"kayıt-{i}")).ToArray();

        foreach (var record in records)
        {
            builder.Add(record.EventId, ts, RawRecordCodec.ToLine(record));
        }

        var built = builder.Build(3);

        // Ortadaki bir kaydı konumundan çekmek, replay'in ve /events/{id}/raw'ın
        // dayandığı tek mekanizma.
        var target = built.Refs[7];
        var line = RawObjectBuilder.ExtractLine(built.Compressed, target.Offset, target.Length);
        var restored = RawRecordCodec.Read(line.Span);

        Assert.Equal(records[7].EventId, restored.EventId);
        Assert.Equal("kayıt-7", Encoding.UTF8.GetString(restored.Body.Span));
    }

    [Fact]
    public void Zaman_araligi_en_kucuk_ve_en_buyuk_damgayi_aliyor()
    {
        var builder = new RawObjectBuilder();
        var baseTs = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        builder.Add(Guid.CreateVersion7(), baseTs.AddMinutes(30), "a"u8);
        builder.Add(Guid.CreateVersion7(), baseTs, "b"u8);
        builder.Add(Guid.CreateVersion7(), baseTs.AddMinutes(59), "c"u8);

        var built = builder.Build(3);

        // Manifest aralık sorgusu buna dayanıyor: yanlışsa replay nesneyi atlar.
        Assert.Equal(baseTs, built.TsFrom);
        Assert.Equal(baseTs.AddMinutes(59), built.TsTo);
    }

    [Fact]
    public void Nesne_disini_gosteren_raw_ref_reddediliyor()
    {
        var builder = new RawObjectBuilder();
        builder.Add(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "kısa"u8);
        var built = builder.Build(3);

        // Manifest ile nesne ayrışmışsa sessizce yanlış bayt döndürmek yerine hata.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RawObjectBuilder.ExtractLine(built.Compressed, 9_000, 10));
    }

    [Fact]
    public void Bos_nesne_yazilmiyor()
    {
        var builder = new RawObjectBuilder();

        Assert.True(builder.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => builder.Build(3));
    }

    [Fact]
    public void Gecersiz_utf8_baytlari_sikistirmadan_gecerken_korunuyor()
    {
        byte[] body = [0x00, 0xC3, 0x28, 0xFF];
        var record = new RawRecord
        {
            EventId = Guid.CreateVersion7(),
            ReceivedAt = DateTimeOffset.UtcNow,
            SourceKey = "fg-01",
            Body = body,
        };

        var builder = new RawObjectBuilder();
        builder.Add(record.EventId, record.ReceivedAt, RawRecordCodec.ToLine(record));
        var built = builder.Build(3);

        var line = RawObjectBuilder.ExtractLine(built.Compressed, built.Refs[0].Offset, built.Refs[0].Length);
        Assert.Equal(body, RawRecordCodec.Read(line.Span).Body.ToArray());
    }
}

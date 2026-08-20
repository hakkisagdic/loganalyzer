using System.Net;
using Bizigo.Contracts;
using Bizigo.Replay;

namespace Bizigo.UnitTests;

/// <summary>
/// Fark raporunun testi (T11 kabul kriteri).
///
/// <para>
/// <c>--dry-run</c>'ın tek işi "ne değişecek" sorusunu doğru cevaplamak. Yanlış
/// cevaplarsa özellik <b>zararlı</b> olur: kullanıcı raporu görüp güvenerek
/// uygular ve beklemediği bir değişiklik alır. Bu yüzden karşılaştırma mantığı
/// veritabanından ayrı, saf biçimde sınanıyor.
/// </para>
/// </summary>
public sealed class ReplayDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    private static LogEvent Event(
        Guid id,
        ParseStatus status = ParseStatus.Ok,
        string action = "accept",
        ushort srcPort = 41022,
        IReadOnlyDictionary<string, string>? attrs = null,
        uint generation = 1) => new()
        {
            EventId = id,
            Timestamp = Now,
            IngestedAt = Now,
            OwnerGroup = "net-core",
            SourceId = "fg-01",
            Host = "fw-01",
            ParseStatus = status,
            ParserId = "fortinet.traffic",
            ParserVersion = "1.0.0",
            ParseGeneration = generation,
            Action = action,
            SrcPort = srcPort,
            SrcIp = IPAddress.IPv6Any,
            DstIp = IPAddress.IPv6Any,
            Attrs = attrs ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Body = "satır",
        };

    private static (Dictionary<Guid, LogEvent> Rebuilt, Dictionary<Guid, LogEvent> Existing) Pair(
        LogEvent before,
        LogEvent after) =>
        (new Dictionary<Guid, LogEvent> { [after.EventId] = after },
         new Dictionary<Guid, LogEvent> { [before.EventId] = before });

    [Fact]
    public void Ayni_olay_degismemis_sayiliyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(Event(id), Event(id));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.Changed);
    }

    [Fact]
    public void Ingested_at_ve_parse_generation_fark_sayilmiyor()
    {
        // İkisi de her replay'de değişir. Fark sayılsaydı rapor "her satır
        // değişti" derdi ve gerçek farklar görünmez olurdu.
        var id = Guid.CreateVersion7();
        var before = Event(id, generation: 1) with { IngestedAt = Now };
        var after = Event(id, generation: 99) with { IngestedAt = Now.AddDays(30) };

        var result = ReplayDiff.Compare(
            new Dictionary<Guid, LogEvent> { [id] = after },
            new Dictionary<Guid, LogEvent> { [id] = before },
            10);

        Assert.Equal(1, result.Unchanged);
    }

    /// <summary>
    /// İmza değişmesi raporlanan bir fark (T29 kabul kriteri).
    ///
    /// <para>
    /// Maskeleme sözlüğü güncellendiyse ya da kodlama tespiti düzeldiyse aynı ham
    /// satır başka bir imzaya düşüyor — ve o satırlar RCA'nın gözünde "ilk kez
    /// görülen" oluyor. Rapor sessiz kalsaydı replay sonrası tek seferlik bir
    /// ilk-görülen dalgası doğar ve sebebi hiçbir yerde yazılı olmazdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Imza_degismesi_fark_olarak_raporlaniyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id) with { SignatureHash = 111 },
            Event(id) with { SignatureHash = 222 });

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.ChangesByField["signature_hash"]);
    }

    /// <summary>
    /// Aynı imza fark <b>değil</b> — yukarıdaki bekçinin kırmızı yanmasının
    /// kazara olmadığının kanıtı. Replay'lerin ezici çoğunluğunda imza
    /// değişmiyor ve her satırı "değişti" saymak raporu kullanılamaz yapardı.
    /// </summary>
    [Fact]
    public void Ayni_imza_fark_sayilmiyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id) with { SignatureHash = 111 },
            Event(id) with { SignatureHash = 111 });

        Assert.Equal(1, ReplayDiff.Compare(rebuilt, existing, 10).Unchanged);
    }

    [Fact]
    public void Failed_to_ok_ayrica_sayiliyor()
    {
        // Replay'in asıl vaadi bu sayı.
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id, ParseStatus.Failed, action: string.Empty),
            Event(id, ParseStatus.Ok));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.FailedToOk);
        Assert.Equal(0, result.OkToFailed);
    }

    [Fact]
    public void Ok_to_failed_gerileme_olarak_gorunuyor()
    {
        // Sıfırdan büyükse yeni parser bir gerileme getirmiş demektir.
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id, ParseStatus.Ok),
            Event(id, ParseStatus.Failed, action: string.Empty));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.OkToFailed);
    }

    [Fact]
    public void Alan_bazinda_sayim_hangi_alanin_etkilendigini_gosteriyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id, action: "accept", srcPort: 1000),
            Event(id, action: "deny", srcPort: 2000));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.ChangesByField["action"]);
        Assert.Equal(1, result.ChangesByField["src_port"]);
    }

    [Fact]
    public void Attrs_alan_alan_karsilastiriliyor()
    {
        // Tek bir "attrs değişti" satırı hangi alanın etkilendiğini gizlerdi.
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id, attrs: new Dictionary<string, string>(StringComparer.Ordinal) { ["devid"] = "A" }),
            Event(id, attrs: new Dictionary<string, string>(StringComparer.Ordinal) { ["devid"] = "B" }));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.ChangesByField["attrs.devid"]);
    }

    [Fact]
    public void Yeni_eklenen_attrs_alani_fark_sayiliyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(
            Event(id),
            Event(id, attrs: new Dictionary<string, string>(StringComparer.Ordinal) { ["yeni"] = "değer" }));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.ChangesByField["attrs.yeni"]);
    }

    [Fact]
    public void Arsivde_olup_tabloda_olmayan_satir_yeni_sayiliyor()
    {
        // İlk işlemede kaybedilmiş satırlar; replay bunları geri getiriyor.
        var id = Guid.CreateVersion7();

        var result = ReplayDiff.Compare(
            new Dictionary<Guid, LogEvent> { [id] = Event(id) },
            [],
            10);

        Assert.Equal(1, result.NewRows);
        Assert.Equal(0, result.Changed);
    }

    [Fact]
    public void Ornek_sayisi_sinirlaniyor()
    {
        var rebuilt = new Dictionary<Guid, LogEvent>();
        var existing = new Dictionary<Guid, LogEvent>();

        for (var i = 0; i < 50; i++)
        {
            var id = Guid.CreateVersion7();
            existing[id] = Event(id, action: "accept");
            rebuilt[id] = Event(id, action: "deny");
        }

        var result = ReplayDiff.Compare(rebuilt, existing, 5);

        // Sayım tam, örnekler sınırlı: 50 farkın tamamını raporlamak raporu
        // okunamaz kılardı.
        Assert.Equal(50, result.Changed);
        Assert.Equal(5, result.Samples.Count);
    }

    [Fact]
    public void Ornek_fark_eski_ve_yeni_degeri_tasiyor()
    {
        var id = Guid.CreateVersion7();
        var (rebuilt, existing) = Pair(Event(id, action: "accept"), Event(id, action: "deny"));

        var result = ReplayDiff.Compare(rebuilt, existing, 10);

        var sample = Assert.Single(result.Samples);
        var change = sample.Changes.Single(c => c.Field == "action");

        // Sayı tek başına "doğru mu" sorusunu cevaplamıyor; değerler lazım.
        Assert.Equal("accept", change.Before);
        Assert.Equal("deny", change.After);
    }
}

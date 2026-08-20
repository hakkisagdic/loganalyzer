using Bizigo.Cli.Fields;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

/// <summary>
/// Alan kapsamı ölçümünün bekçileri (T39).
///
/// <para>
/// Ölçüm bir <b>ayrım</b> yapmak için var: bir Sigma kuralı boş dönüyorsa
/// sebebi eşleme eksikliği mi, bilginin başka ada inmesi mi, yoksa örneklemde o
/// desenin hiç olmaması mı. Üç sebebin tablodaki görüntüsü aynı ve yanlış
/// ayırmanın bedeli, olmayan bir veriyi eşlemeye çalışmak ya da var olan bir
/// alanı örneklem eksikliği sanıp geçmek.
/// </para>
///
/// <para>
/// Aracın kendisinin sessizce yanlış sayması, tam da engellemek için var olduğu
/// hatayı üretmek olurdu — ve <b>bir kez üretti</b>: ilk koşumda kutu 1 dört
/// vendor'da da boş çıktı, çünkü <c>attrs['message']</c> satırın tamamıydı ve
/// gövdeyi kendisi kapsıyordu. "Parser her şeyi yakalamış" görüntüsü
/// veriyordu. Aşağıdaki ilk test o günün kaydı.
/// </para>
/// </summary>
public sealed class FieldCoverageTests
{
    private static readonly IReadOnlyList<OcsfViewColumn> Columns =
    [
        new("action", "activity_name"),
        new("proto", "connection_info_protocol_name"),
        new("user_name", "actor_user_name"),
        new("src_port", "src_endpoint_port"),
        new("attrs", "unmapped"),
    ];

    /// <summary>
    /// <b>Gövdenin kopyası kapsama sayılmaz.</b>
    ///
    /// <para>
    /// Parser'lar ham satırı <c>message</c> alanında saklıyor ve o değer
    /// gövdenin birebir kendisi. Kapsama sayılırsa hiçbir aralık boşta kalmaz
    /// ve ölçüm "yakalanmamış metin yok" der — ölçmesi istenen şeyin tam
    /// tersini.
    /// </para>
    /// </summary>
    [Fact]
    public void Govdenin_kopyasi_yakalanmis_sayilmiyor()
    {
        const string Body = "kabul edildi kullanici=ayse KAYIP_PARCA_BURADA";

        var report = FieldCoverage.Measure(
            [
                Event(Body, action: "kabul", attrs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // Boru hattının gerçekte yaptığı şey: ham satır bir alan.
                    ["message"] = Body,
                    ["kullanici"] = "ayse",
                }),
            ],
            Columns);

        var vendor = Assert.Single(report.Vendors);

        Assert.Contains(
            vendor.Uncaptured,
            fragment => fragment.Text.Contains("KAYIP_PARCA_BURADA", StringComparison.Ordinal));
    }

    /// <summary>
    /// Blob kuralı yapıya bakıyor, uzunluğa değil: içinde başka bir yakalanmış
    /// değer geçen alan üst hâldir ve Sigma yalnızca <c>contains</c> ile
    /// adresleyebilir.
    /// </summary>
    [Fact]
    public void Ic_ice_gecen_alan_ust_hal_sayiliyor()
    {
        const string Body = "olay: Administrator ayse giris yapti KAYIP";

        var report = FieldCoverage.Measure(
            [
                Event(Body, action: string.Empty, attrs: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // `msg` içinde `user` geçiyor → blob.
                    ["msg"] = "Administrator ayse giris yapti KAYIP",
                    ["user"] = "ayse",
                }),
            ],
            Columns);

        var vendor = Assert.Single(report.Vendors);

        Assert.Contains(
            vendor.Uncaptured,
            fragment => fragment.Text.Contains("KAYIP", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Kutu 2.</b> Satırdan gelen ama OCSF kolonuna inmeyen bilgi
    /// işaretleniyor; boru hattının kendi defter kaydı ise
    /// <c>FromLine=false</c> ile ayrılıyor. İkisi tek listede olsaydı
    /// <c>bizigo.dispatch_tier</c> ile <c>fw_chain</c> aynı ağırlıkta görünürdü.
    /// </summary>
    [Fact]
    public void Yer_degistirmis_bilgi_defter_kaydindan_ayriliyor()
    {
        var report = FieldCoverage.Measure(
            [
                Event("forward: in:ether1", action: string.Empty,
                    attrs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fw_chain"] = "forward",
                        ["bizigo.dispatch_tier"] = "Prefilter",
                    }),
            ],
            Columns);

        var vendor = Assert.Single(report.Vendors);

        var chain = Assert.Single(vendor.Relocated, entry => entry.Key == "fw_chain");
        Assert.True(chain.FromLine);

        var tier = Assert.Single(vendor.Relocated, entry => entry.Key == "bizigo.dispatch_tier");
        Assert.False(tier.FromLine);
    }

    /// <summary>
    /// Yalnızca <b>biçimi</b> değişmiş değer "kayıp" sayılmamalı:
    /// <c>proto_token=UDP</c> ile <c>connection_info_protocol_name=udp</c> aynı
    /// bilgi. Ayrım yapılmazsa gerek olmadığı hâlde eşleme yazılır.
    /// </summary>
    [Fact]
    public void Yalnizca_bicimi_degismis_deger_isaretleniyor()
    {
        var report = FieldCoverage.Measure(
            [
                Event("proto UDP", action: string.Empty, proto: "udp",
                    attrs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["proto_token"] = "UDP",
                    }),
            ],
            Columns);

        var vendor = Assert.Single(report.Vendors);
        var entry = Assert.Single(vendor.Relocated, item => item.Key == "proto_token");

        Assert.Contains("connection_info_protocol_name", entry.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kutu 3b — bu ölçümün asıl ayrımı.</b> Bir alan bir vendor'da dolu,
    /// başkasında hep boş olabiliyor (<c>activity_name</c> FortiGate'te dolu,
    /// RouterOS'ta değil). Küresel bir "eksik alan" listesi bunu ifade edemiyor
    /// ve iki farklı durum için de yanlış iş yaptırıyor.
    /// </summary>
    [Fact]
    public void Vendora_ozel_bos_alan_kuresel_bostan_ayriliyor()
    {
        var report = FieldCoverage.Measure(
            [
                Event("a", action: "deny", vendor: "Fortinet"),
                Event("b", action: string.Empty, vendor: "MikroTik"),
            ],
            Columns);

        var fortinet = report.Vendors.Single(vendor => vendor.Vendor == "Fortinet");
        var mikrotik = report.Vendors.Single(vendor => vendor.Vendor == "MikroTik");

        Assert.Empty(report.EmptyFor(fortinet));
        Assert.Contains("activity_name", report.EmptyFor(mikrotik));

        // Küresel boş DEĞİL: bir vendor onu dolduruyor.
        Assert.DoesNotContain("activity_name", report.EmptyEverywhere());

        // Hiç kimsenin doldurmadığı alan küresel listede.
        Assert.Contains("actor_user_name", report.EmptyEverywhere());
    }

    /// <summary>
    /// <b>Bekçinin bekçisi.</b> <c>events</c>'e kolon eklenip
    /// <see cref="EventFieldKinds"/> unutulursa ölçüm o kolonu <b>hiç sormaz</b>
    /// ve eksik tablo tam görünür — <c>Produces</c> kapısının elle yazılmış
    /// listesiyle birebir aynı hata sınıfı.
    /// </summary>
    [Fact]
    public void Yazicinin_her_kolonu_taniniyor()
    {
        Assert.Empty(EventFieldKinds.Unknown());
    }

    /// <summary>
    /// Görünümün kolon listesi <b>göç dosyasından</b> okunuyor. Elle yazılsaydı
    /// görünüme eklenen bir kolon hiç sorulmazdı.
    /// </summary>
    [Fact]
    public void Gorunum_kolonlari_goc_dosyasindan_okunuyor()
    {
        var columns = OcsfViewSchema.Read(Path.Combine(RepositoryLayout.Root, "db", "clickhouse"));

        Assert.Contains(columns, column => column is { Source: "action", Alias: "activity_name" });
        Assert.Contains(columns, column => column is { Source: "attrs", Alias: "unmapped" });
        Assert.Contains(columns, column => column is { Source: "body", Alias: "raw_data" });

        // Takma adsız kolon da tanınıyor.
        Assert.Contains(columns, column => column is { Source: "owner_group", Alias: "owner_group" });

        // Ve her kaynak kolonun doluluk ölçüsü tanımlı — biri eksikse ölçüm
        // o kolonda patlar, sessizce atlamaz.
        foreach (var column in columns)
        {
            EventFieldKinds.Of(column.Source);
        }
    }

    /// <summary>
    /// İkinci bir tanım eklenirse hangisinin geçerli olduğu dosya adına
    /// gizlenmiş olurdu; araç duruyor.
    /// </summary>
    [Fact]
    public void Ikinci_gorunum_tanimi_reddediliyor()
    {
        var directory = Directory.CreateTempSubdirectory("bizigo-ocsf-view");

        try
        {
            const string Definition = "CREATE VIEW events_ocsf AS SELECT ts AS time FROM events;";
            File.WriteAllText(Path.Combine(directory.FullName, "0001_a.sql"), Definition);
            File.WriteAllText(Path.Combine(directory.FullName, "0002_b.sql"), Definition);

            var error = Assert.Throws<InvalidOperationException>(
                () => OcsfViewSchema.Read(directory.FullName));

            Assert.Contains("birden fazla", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static LogEvent Event(
        string body,
        string action,
        string vendor = "Acme",
        string proto = "",
        IReadOnlyDictionary<string, string>? attrs = null) => new()
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UnixEpoch,
            OwnerGroup = "golden",
            SourceId = "golden-acme",
            Vendor = vendor,
            Action = action,
            Proto = proto,
            Body = body,
            Attrs = attrs ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
}

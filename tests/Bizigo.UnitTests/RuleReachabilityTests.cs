using Bizigo.Cli.Fields;

namespace Bizigo.UnitTests;

/// <summary>
/// Kural × değer uzayı birleştirmesinin bekçileri (T39).
///
/// <para>
/// Birleştirme iki bağımsız ekseni yan yana koyuyor: <c>explain_misses.py</c>
/// "dizge örneklerde var mı" diye soruyor, bu taraf "olsa bile o kolonda
/// durabilir mi". Aşağıdaki dört durum, ikisinin ayrı ayrı cevaplayamadığı
/// sorular.
/// </para>
/// </summary>
public sealed class RuleReachabilityTests
{
    private const string RawText = "raw_data";

    private static readonly IReadOnlyDictionary<string, string> FieldMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "status",
            ["action"] = "activity_name",
            ["srcip"] = "src_endpoint_ip",
            ["message"] = RawText,
        };

    private static VendorValueSpace Vendor(params (string Alias, ColumnValueSpace Space)[] columns) =>
        new(
            "NGINX",
            ["nginx"],
            columns.ToDictionary(static column => column.Alias, static column => column.Space, StringComparer.Ordinal),
            []);

    private static RuleEntry Rule(string field, string @operator, string value, string verdict = "present") =>
        new("kural.yml", "nginx", verdict, [new RuleLiteral(field, @operator, value, verdict)]);

    /// <summary>
    /// <b>Asıl kapı.</b> Kapalı bir uzayın üretemeyeceği değer, veri ne olursa
    /// olsun eşleşmez — ve bu, örneklem boşluğundan bambaşka bir iş emri.
    /// </summary>
    [Fact]
    public void Kapali_uzayin_disindaki_deger_ERISILEMEZ()
    {
        var space = Vendor(("status",
            new ColumnValueSpace("status", "outcome", ValueSpaceKind.Closed, ["failure", "success"], ["t"], [])));

        var result = Assert.Single(RuleReachability.Join([Rule("status", "startswith", "5")], [space], FieldMap, RawText));

        Assert.Equal(ReachVerdict.Unreachable, result.Verdict);
        Assert.Contains("kapalı bir değer uzayı", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Vendor birleşiminde kaybolan parser boşluğu.</b> Ölçülmüş vaka:
    /// <c>activity_name</c> MikroTik'te "açık" görünüyor çünkü
    /// <c>routeros.system</c> dolduruyor — ama <c>routeros.firewall</c> onu
    /// bilerek boş bırakıyor ve <c>routeros_drop_input</c> tam ona vuruyor.
    /// </summary>
    [Fact]
    public void Bazi_parserlarin_doldurmadigi_kolon_PARSER_BOSLUGU()
    {
        var space = Vendor(("activity_name",
            new ColumnValueSpace("activity_name", "action", ValueSpaceKind.Open, [], ["t"], ["nginx.access.json"])));

        var result = Assert.Single(RuleReachability.Join([Rule("action", "", "drop")], [space], FieldMap, RawText));

        Assert.Equal(ReachVerdict.ParserGap, result.Verdict);
        Assert.Contains("nginx.access.json", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ters yön — ve bu turda gerçekten çıktı.</b> Metin ekseni "dizge
    /// örneklerde YOK" diyor (<c>fortigate_user_auth_fail</c>, FortiGate satırda
    /// <c>status="failed"</c> yazıyor) ama <c>auth_outcome.yaml</c>
    /// <c>failed → failure</c> çeviriyor: kolonda gerçekten <c>failure</c>
    /// duruyor ve kural doğru. Tek eksenle bakan bir ölçüm bunu "kuralın
    /// kusuru" diye raporlardı.
    /// </summary>
    [Fact]
    public void Eslemenin_cevirdigi_deger_metin_ekseninin_yanildigini_gosteriyor()
    {
        var space = Vendor(("status",
            new ColumnValueSpace("status", "outcome", ValueSpaceKind.Closed, ["failure", "success"], ["t"], [])));

        var result = Assert.Single(RuleReachability.Join(
            [Rule("status", "", "failure", verdict: "absent")], [space], FieldMap, RawText));

        Assert.Equal(ReachVerdict.Reachable, result.Verdict);
        Assert.True(result.TextAxisWrong);
        Assert.Contains("eşleme tablosu", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Alan adı çevirisi <b>uygulanıyor</b>: kural Sigma taxonomy'siyle
    /// (<c>srcip</c>) yazılmış, kolon <c>src_endpoint_ip</c>. Çeviri olmadan
    /// her dizge "kolona bağlı değil" görünür ve tablo "hiçbir sorun yok" diye
    /// okunurdu.
    /// </summary>
    [Fact]
    public void Sigma_taxonomy_adi_kolona_ceviriliyor()
    {
        var space = Vendor(("src_endpoint_ip",
            new ColumnValueSpace("src_endpoint_ip", "src_ip", ValueSpaceKind.Open, [], ["t"], [])));

        var result = Assert.Single(RuleReachability.Join(
            [Rule("srcip", "startswith", "10.")], [space], FieldMap, RawText));

        Assert.Equal(ReachVerdict.Unknown, result.Verdict);
        Assert.DoesNotContain("kolona bağlı değil", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Bu ürün için parser yoksa hüküm verilmiyor.</summary>
    [Fact]
    public void Parseri_olmayan_urun_icin_hukum_verilmiyor()
    {
        var result = Assert.Single(RuleReachability.Join(
            [new RuleEntry("x.yml", "paloalto", "present", [new RuleLiteral("status", "", "success", "present")])],
            [Vendor()],
            FieldMap,
            RawText));

        Assert.Equal(ReachVerdict.Unknown, result.Verdict);
        Assert.Contains("parser yok", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Alan adı çevirisi pipeline dosyasından okunuyor — ikinci bir kopya
    /// yazmak, iki aracın aynı kuralı farklı kolona bağladığı günü hazırlamak
    /// olurdu.
    /// </summary>
    [Fact]
    public void Alan_cevirisi_pipeline_dosyasindan_okunuyor()
    {
        var map = SigmaFieldMap.Read(Path.Combine(
            RepositoryLayout.Root, "prototypes", "t30-sigma", "bizigo_pipeline.py"));

        Assert.Equal("src_endpoint_ip", map["srcip"]);
        Assert.Equal("connection_info_protocol_name", map["proto"]);
    }

    /// <summary>
    /// Sözlük bulunamazsa boş dönmüyor, <b>atıyor</b>: boş bir çeviriyle her
    /// dizge "kolona bağlı değil" görünür ve o tablo "hiçbir şey ölçülemedi"
    /// değil "hiçbir sorun yok" diye okunurdu.
    /// </summary>
    [Fact]
    public void Bulunamayan_sozluk_sessizce_bos_donmuyor()
    {
        var file = Path.GetTempFileName();

        try
        {
            File.WriteAllText(file, "# içinde FIELD_MAP yok\n");
            Assert.Throws<InvalidOperationException>(() => SigmaFieldMap.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Ham gövdeye vuran kural ayrı sınıf: değer uzayı sorusu anlamsız ama
    /// sayısı bir kapsam göstergesi — tam metin taraması indeksten
    /// yararlanmıyor. "Uzay açık, söylenemez" ile aynı kutuda durması, bir
    /// tasarım tercihini bir iş kalemiyle karıştırırdı.
    /// </summary>
    [Fact]
    public void Ham_govdeye_vuran_kural_ayri_siniflaniyor()
    {
        var result = Assert.Single(RuleReachability.Join(
            [Rule("message", "contains", "Teardown")], [Vendor()], FieldMap, RawText));

        Assert.Equal(ReachVerdict.RawText, result.Verdict);
        Assert.Contains("indeksin kullanılamaması", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Görünümde kolonu olmayan ad <c>unmapped['…']</c>'e çevriliyor: alan
    /// olarak adreslenmiş ama Map araması, yine indekssiz. Ham metinden farklı
    /// bir kalem.
    /// </summary>
    [Fact]
    public void Gorunumde_kolonu_olmayan_ad_unmapped_erisimi()
    {
        var result = Assert.Single(RuleReachability.Join(
            [Rule("dns_query_name", "contains", "example")], [Vendor()], FieldMap, RawText));

        Assert.Equal(ReachVerdict.UnmappedAccess, result.Verdict);
        Assert.Contains("unmapped", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>"Metin ekseni yanılıyor" iddiası modellenmiş operatör istiyor.</b>
    /// <c>CanSatisfy</c> modellenmemiş operatörde güvenli tarafa düşüp
    /// <c>true</c> dönüyor; o <c>true</c>'yu "değer kolonda VAR" diye okumak,
    /// aracın bilmediğini bildiği gibi göstermesi olurdu.
    /// </summary>
    [Fact]
    public void Modellenmemis_operatorde_metin_ekseni_yanildi_denmiyor()
    {
        var space = Vendor(("status",
            new ColumnValueSpace("status", "outcome", ValueSpaceKind.Closed, ["failure", "success"], ["t"], [])));

        var result = Assert.Single(RuleReachability.Join(
            [Rule("status", "re", "^fail", verdict: "absent")], [space], FieldMap, RawText));

        Assert.Equal(ReachVerdict.Reachable, result.Verdict);
        Assert.False(result.TextAxisWrong);
    }

    /// <summary>Boş kural listesi "0 erişilemez" diye okunmasın diye reddediliyor.</summary>
    [Fact]
    public void Bos_kural_listesi_reddediliyor()
    {
        var file = Path.GetTempFileName();

        try
        {
            File.WriteAllText(file, "[]");
            Assert.Throws<InvalidOperationException>(() => RuleReachability.ReadRules(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

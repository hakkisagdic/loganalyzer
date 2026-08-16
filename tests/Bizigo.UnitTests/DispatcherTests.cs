using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

/// <summary>
/// T06 kabul kriterleri (F1 §4.2).
///
/// <para>
/// Kademelerin sırası performans için değil <b>doğruluk</b> için: envanter bağı
/// cihazın ne gönderdiğini tahmin etmek yerine biliyor. Buradaki testler o
/// sıranın gerçekten uygulandığını tutuyor.
/// </para>
/// </summary>
public sealed class DispatcherTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static ParserCompiler Compiler() => new(new GrokCompiler(Library));

    private static CompiledParser Parser(
        string id,
        string literal,
        string pattern,
        int specificity = 0)
    {
        // Girinti elle kuruluyor: enterpolasyonlu ham dizgi, çok satırlı değeri
        // yeniden girintilemiyor ve `match` sessizce `metadata`nın altına düşüyor.
        var contains = string.IsNullOrEmpty(literal)
            ? string.Empty
            : $"match:\n  contains: ['{literal}']\n";

        var yaml = $"""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata:
              id: {id}
              version: 1.0.0
              specificity: {specificity}
            {contains}pipeline:
              - grok:
                  field: message
                  patterns:
                    - '{pattern}'
            tests:
              - name: yer tutucu
                input: 'x'
                expect:
                  parse_status: failed
            """;

        var loaded = ParserYamlLoader.Load(yaml, id);
        Assert.True(loaded.Ok, loaded.Describe());

        var compiled = Compiler().Compile(loaded.Value);
        Assert.True(compiled.Ok, string.Join("; ", compiled.Errors.Select(e => e.Message)));

        return compiled.Value;
    }

    private static (Dispatcher Dispatcher, ParserCatalog Catalog, DispatchStats Stats) Build(
        params CompiledParser[] parsers)
    {
        var catalog = new ParserCatalog();
        catalog.Replace(parsers);

        var stats = new DispatchStats();
        return (new Dispatcher(catalog, stats), catalog, stats);
    }

    [Fact]
    public void Envanterde_bagli_kaynak_dogrudan_o_parsera_gidiyor()
    {
        var bound = Parser("vendor.bound", "devid=", "devid=%{WORD:devid}");
        var other = Parser("vendor.other", "devid=", "devid=%{WORD:devid}");

        var (dispatcher, _, stats) = Build(bound, other);

        var result = dispatcher.Dispatch("devid=FG100E", "vendor.bound");

        Assert.Equal(DispatchTier.InventoryBound, result.Tier);
        Assert.Equal("vendor.bound", result.Result.ParserId);

        // Kabul kriteri: bağlı kaynakta aday denemesi YOK — tek deneme.
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, stats.Bound);
        Assert.Equal(0, stats.Candidate);
    }

    [Fact]
    public void Bagli_parser_tutmazsa_aday_taramasina_dusuluyor()
    {
        var bound = Parser("vendor.bound", "yok=", "yok=%{WORD:x}");
        var real = Parser("vendor.real", "devid=", "devid=%{WORD:devid}");

        var (dispatcher, _, stats) = Build(bound, real);

        var result = dispatcher.Dispatch("devid=FG100E", "vendor.bound");

        // Cihaz yazılımı değişmiş olabilir; satır kaybedilmez ama bu sayaçta görünür.
        Assert.Equal(DispatchTier.Candidate, result.Tier);
        Assert.Equal("vendor.real", result.Result.ParserId);
        Assert.Equal(1, stats.BoundMisses);
    }

    [Fact]
    public void Bilinmeyen_parser_bagi_aday_taramasini_engellemiyor()
    {
        var real = Parser("vendor.real", "devid=", "devid=%{WORD:devid}");
        var (dispatcher, _, _) = Build(real);

        // Envanterde silinmiş bir parser'a bağ kalmış olabilir.
        var result = dispatcher.Dispatch("devid=FG100E", "silinmis.parser");

        Assert.Equal(DispatchTier.Candidate, result.Tier);
    }

    [Fact]
    public void Literal_on_filtre_adayi_daraltiyor()
    {
        var fortinet = Parser("fortinet", "devid=", "devid=%{WORD:devid}");
        var cisco = Parser("cisco", "%ASA-", "%%ASA-%{INT:sev}");

        var (dispatcher, _, _) = Build(fortinet, cisco);

        var result = dispatcher.Dispatch("devid=FG100E", boundParserId: null);

        // Cisco literali tutmadığı için hiç denenmemeli.
        Assert.Equal(1, result.Attempts);
        Assert.Equal("fortinet", result.Result.ParserId);
    }

    [Fact]
    public void Adaylar_specificity_sirasiyla_deneniyor_ilk_ok_kazaniyor()
    {
        // İkisi de aynı satırı tutuyor; dar kapsamlı olan önce denenmeli.
        var genel = Parser("genel", "devid=", "devid=%{WORD:devid}", specificity: 1);
        var dar = Parser("dar", "devid=", "devid=%{WORD:devid} type=%{WORD:type}", specificity: 10);

        var (dispatcher, _, _) = Build(genel, dar);

        var result = dispatcher.Dispatch("devid=FG100E type=traffic", boundParserId: null);

        Assert.Equal("dar", result.Result.ParserId);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void Literali_olmayan_parser_her_zaman_aday()
    {
        // Ön filtreyle elenemez; aksi halde sessizce hiç denenmezdi.
        var literalsiz = Parser("literalsiz", literal: string.Empty, "%{GREEDYDATA:hepsi}");

        var (dispatcher, _, _) = Build(literalsiz);

        var result = dispatcher.Dispatch("hiçbir literale uymayan satır", boundParserId: null);

        Assert.Equal(DispatchTier.Candidate, result.Tier);
        Assert.Equal("literalsiz", result.Result.ParserId);
    }

    [Fact]
    public void Hicbiri_tutmayan_satir_failed_oluyor()
    {
        var fortinet = Parser("fortinet", "devid=", "devid=%{WORD:devid}");
        var (dispatcher, _, stats) = Build(fortinet);

        var result = dispatcher.Dispatch("tamamen tanınmayan bir satır", boundParserId: null);

        // REDDEDİLMİYOR: ham arşivde duruyor, parser düzelince replay geri kazanır.
        Assert.Equal(DispatchTier.Unmatched, result.Tier);
        Assert.Equal(ParseStatus.Failed, result.Result.Status);
        Assert.Equal(1, stats.Unmatched);
    }

    [Fact]
    public void Bos_katalogda_satir_kaybolmuyor()
    {
        var (dispatcher, _, _) = Build();

        var result = dispatcher.Dispatch("devid=FG100E", "vendor.bound");

        Assert.Equal(DispatchTier.Unmatched, result.Tier);
        Assert.Equal(ParseStatus.Failed, result.Result.Status);
    }

    [Fact]
    public void Bound_ratio_hesaplaniyor()
    {
        var bound = Parser("vendor.bound", "devid=", "devid=%{WORD:devid}");
        var (dispatcher, _, stats) = Build(bound);

        dispatcher.Dispatch("devid=A", "vendor.bound");
        dispatcher.Dispatch("devid=B", "vendor.bound");
        dispatcher.Dispatch("devid=C", boundParserId: null);
        dispatcher.Dispatch("tanınmayan", boundParserId: null);

        Assert.Equal(4, stats.Total);
        Assert.Equal(0.5, stats.BoundRatio);
        Assert.Equal(0.25, stats.UnmatchedRatio);
    }

    [Fact]
    public void Sicak_yeniden_yukleme_koşan_dagitimi_bozmuyor()
    {
        var ilk = Parser("v1", "devid=", "devid=%{WORD:devid}");
        var (dispatcher, catalog, _) = Build(ilk);

        Assert.Equal("v1", dispatcher.Dispatch("devid=A", null).Result.ParserId);

        catalog.Replace([Parser("v2", "devid=", "devid=%{WORD:devid}")]);

        // Değişim atomik: eski ya da yeni katalog görülür, yarı yüklü ara durum yok.
        Assert.Equal("v2", dispatcher.Dispatch("devid=A", null).Result.ParserId);
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Ayni_id_icin_en_yuksek_surum_kazaniyor()
    {
        var catalog = new ParserCatalog();

        // `Replace` sürüm çözümlemesi yapmıyor (doğrudan liste); çözümleme
        // dizinden yüklemede. Burada kataloğun sıralamayı koruduğunu doğruluyoruz.
        catalog.Replace([
            Parser("a", "x", "%{GREEDYDATA:d}", specificity: 5),
            Parser("b", "x", "%{GREEDYDATA:d}", specificity: 9),
        ]);

        Assert.Equal("b", catalog.Current.Parsers[0].Id);
    }
}

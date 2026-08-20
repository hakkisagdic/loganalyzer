using Bizigo.Api;
using Bizigo.Authoring;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>POST /v1/parsers/try</c>'ın taslak modu (T19).
///
/// <para>
/// Ucun taşıdığı fikir: parser <b>yayınlanmadan önce</b> denenebilmeli. Bunun
/// bedeli, keyfi YAML'ın sunucuda derlenmesi — ve bu ancak derlemenin
/// <b>ad-hoc</b> olmasıyla, yani çalışan kataloğa hiç dokunmamasıyla güvenli.
/// Bu dosyanın asıl testi
/// <see cref="Taslak_denemesi_calisan_katalogu_kirletmiyor"/>.
/// </para>
///
/// <para>
/// HTTP katmanı burada yok: uç yalnızca <c>gate.Inspect</c>,
/// <c>verdict.Compiled.Parse</c> ve <c>dispatcher.Dispatch</c> çağırıyor ve
/// üçü de burada gerçek nesnelerle koşuyor. Uçtan uca yol
/// <c>Bizigo.IntegrationTests/ParserAuthoringTests</c> içinde.
/// </para>
/// </summary>
public sealed class ParserTryTests
{
    private static readonly MappingTableCatalog Tables =
        MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    private static ParserCompiler Compiler() =>
        new(new GrokCompiler(RepositoryLayout.DefaultLibrary), Tables);

    private const string Taslak = """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: test.try.draft
          version: 0.1.0
          vendor: Test
          product: Try
        match:
          transport: [syslog]
          contains: ["TRY-DRAFT"]
        pipeline:
          - grok:
              field: message
              patterns:
                - '^TRY-DRAFT %{WORD:action} %{IPV4:src_ip}$'
        map:
          core:
            action: "{{ action }}"
            src_ip: "{{ src_ip }}"
        tests:
          - name: temel
            input: 'TRY-DRAFT accept 10.0.0.1'
            expect:
              parse_status: ok
              core.action: "accept"
        """;

    /// <summary>
    /// <b>Ucun güvenli olmasının tek sebebi.</b> Taslak denemesi kataloğa
    /// yazsaydı, herhangi bir <c>author</c> tek bir istekle çalışan boru
    /// hattının davranışını değiştirebilirdi — üstelik inceleme ve yayın
    /// kapılarının tamamını atlayarak.
    ///
    /// <para>
    /// Anlık görüntünün <b>aynı nesne</b> kaldığı sınanıyor, yalnızca sayısı
    /// değil: yeniden yüklenip aynı sayıda parser üreten bir katalog da sayı
    /// testini geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Taslak_denemesi_calisan_katalogu_kirletmiyor()
    {
        var compiler = Compiler();
        var catalog = new ParserCatalog();
        catalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "parsers"), compiler);

        var before = catalog.Current;
        Assert.NotEmpty(before.Parsers);

        var verdict = new ParserPublishGate(compiler).Inspect(Taslak);

        Assert.True(verdict.Ok, string.Join(" | ", verdict.Errors));
        Assert.Same(before, catalog.Current);
        Assert.DoesNotContain("test.try.draft", catalog.Current.ByParserId.Keys);
    }

    /// <summary>
    /// Kapı, derlediği parser'ı <b>geri veriyor</b> — önizlemenin örnek satırı
    /// koşturduğu nesne bu. İkinci bir derleme, "kapının onayladığı parser" ile
    /// "önizlemenin koşturduğu parser"ı bir gün ayrıştırabilirdi.
    /// </summary>
    [Fact]
    public void Kapinin_derledigi_parser_ornek_satiri_cozuyor()
    {
        var verdict = new ParserPublishGate(Compiler()).Inspect(Taslak);

        var compiled = Assert.IsType<CompiledParser>(verdict.Compiled);
        var result = compiled.Parse("TRY-DRAFT deny 192.168.1.1");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.False(result.TimedOut);
        Assert.Equal("deny", result.Core["action"]);
        Assert.Equal("192.168.1.1", result.Core["src_ip"]);
    }

    /// <summary>
    /// Testi düşen taslak yayınlanamıyor ama <b>denenebiliyor</b>. İkisini
    /// birleştirmek, testi neden düştüğünü anlamak isteyen kişiyi önce testi
    /// düzeltmeye zorlardı — yani sebebi görmeden.
    /// </summary>
    [Fact]
    public void Testi_dusen_taslak_yine_de_deneniyor()
    {
        var verdict = new ParserPublishGate(Compiler()).Inspect(
            Taslak.Replace("core.action: \"accept\"", "core.action: \"deny\"", StringComparison.Ordinal));

        Assert.False(verdict.Ok);
        Assert.Equal(PublishGateStage.Tests, verdict.Stage);
        Assert.NotNull(verdict.Compiled);
        Assert.Equal(ParseStatus.Ok, verdict.Compiled!.Parse("TRY-DRAFT accept 10.0.0.1").Status);
    }

    /// <summary>
    /// Şema hatası aşamayı <c>schema</c> yapıyor ve <b>satır numarası</b>
    /// taşıyor. Kabul kriteri tam olarak bunu istiyor: "bir yerde bir şey
    /// yanlış" ile "37. satır" arasındaki fark, editörün imleci oraya
    /// götürebilmesi.
    /// </summary>
    [Fact]
    public void Sema_hatasi_satir_numarasiyla_geliyor()
    {
        var verdict = new ParserPublishGate(Compiler()).Inspect("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata:
              id: test.try.broken
              version: 0.1.0
            pipeline:
              - grok:
                  field: message
            """);

        Assert.False(verdict.Ok);
        Assert.Equal(PublishGateStage.Schema, verdict.Stage);

        // Eksik `patterns` sekizinci satırda; hata onu göstermeli. Satırın
        // kendisi sınanıyor, "sıfırdan büyük" değil: hepsini 1'e sabitleyen bir
        // hata da o zayıf koşulu geçerdi.
        var missingPatterns = Assert.Single(
            verdict.SchemaErrors,
            e => e.Message.Contains("patterns", StringComparison.Ordinal));

        Assert.Equal(8, missingPatterns.Line);
        Assert.True(missingPatterns.Column > 0, "Şema hatası sütun taşımıyor.");

        Assert.Empty(verdict.TestResults);
        Assert.Null(verdict.Compiled);
    }

    /// <summary>
    /// Geri izlemeye düşen pattern <c>redos</c> aşamasında duruyor — testler
    /// geçse bile. Sıra bilinçli: geri izleyen bir pattern testleri de
    /// kararsız kılıyor (<c>matchTimeout</c> duvar saatini ölçüyor), yani
    /// "testlerim geçiyor" o durumda bir kanıt değil.
    /// </summary>
    [Fact]
    public void Geri_izleyen_pattern_redos_asamasinda_duruyor()
    {
        var verdict = new ParserPublishGate(Compiler()).Inspect(Taslak.Replace(
            "'^TRY-DRAFT %{WORD:action} %{IPV4:src_ip}$'",
            "'^TRY-DRAFT (?<=T)%{WORD:action} %{IPV4:src_ip}$'",
            StringComparison.Ordinal));

        Assert.False(verdict.Ok);
        Assert.Equal(PublishGateStage.Redos, verdict.Stage);
        Assert.Contains(verdict.RedosFindings, f => f.Code == "GROK003");

        // Bulgu şiddet olarak uyarı ama yayında HATA: ekranın "bu sadece uyarı"
        // demesini engelleyen tek şey bu ayrım.
        var response = ParserRedosFindingResponse.From(
            verdict.RedosFindings.First(f => f.Code == "GROK003"));
        Assert.Equal("warning", response.Severity);
        Assert.True(response.Blocking);
    }

    /// <summary>
    /// Dispatcher kademesi <b>gerekçesiyle</b> geliyor. Ekranın kendi cümlesini
    /// kurması, aynı yorumun iki yerde tutulması demekti.
    /// </summary>
    [Theory]
    [InlineData(DispatchTier.InventoryBound, "inventory_bound")]
    [InlineData(DispatchTier.Candidate, "candidate")]
    [InlineData(DispatchTier.Unmatched, "unmatched")]
    public void Kademe_adi_ve_gerekcesi_tasiniyor(DispatchTier tier, string expected)
    {
        var response = ParserDispatchResponse.From(new DispatchResult(
            ParseResult.Failure("x", "1", "yok"), tier, 3));

        Assert.Equal(expected, response.Tier);
        Assert.NotEmpty(response.Reason);
        Assert.Equal(3, response.Attempts);
    }

    /// <summary>
    /// <c>timed_out</c> gövdede <b>ayrı</b> duruyor. Sıfırdan farklıysa sonuç
    /// "uymadı" değil "ölçülemedi" demek; ikisini karıştırmak sağlıklı bir
    /// parser'ı karantinaya sokar (T08 raporu #10).
    /// </summary>
    [Fact]
    public void Zaman_asimi_durumdan_ayri_tasiniyor()
    {
        var response = ParseOutcomeResponse.From(new ParseResult
        {
            ParserId = "test.timeout",
            ParserVersion = "1.0.0",
            Status = ParseStatus.Failed,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal),
            TimedOut = true,
        });

        Assert.Equal("failed", response.Status);
        Assert.True(response.TimedOut);
    }

    /// <summary>
    /// Değeri olmayan alan gövdeye <b>hiç yazılmıyor</b>. Boş string yazmak
    /// "atanmamış" ile "boş atanmış"ı ayırt edilemez kılardı — T08 raporu #6
    /// tam olarak bu ayrımı geri kazandırdı, gövde onu kaybetmemeli.
    /// </summary>
    [Fact]
    public void Atanmamis_alan_govdeye_inmiyor()
    {
        var response = ParseOutcomeResponse.From(new ParseResult
        {
            ParserId = "test.absent",
            ParserVersion = "1.0.0",
            Status = ParseStatus.Ok,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = null },
        });

        Assert.Equal("1", response.Fields["a"]);
        Assert.DoesNotContain("b", response.Fields.Keys);
    }
}

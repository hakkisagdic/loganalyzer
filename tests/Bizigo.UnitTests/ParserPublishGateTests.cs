using Bizigo.Authoring;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Yayın kapıları (T18).
///
/// <para>
/// Kapıların değeri <b>reddedebilmelerinde</b>. Katalog F1'de sıfır
/// <c>GROK003</c>'e indirildi ve bu dört ayrı daraltma gerektirdi; kapı olmadan
/// ilk katkı o kazanımı sessizce geri alır — geri izlemeye düşen pattern
/// <c>matchTimeout</c> ödüyor, o da duvar saatini ölçüyor, yani yüklü makinede
/// sağlıklı bir satır <c>failed</c> oluyor.
/// </para>
/// </summary>
public sealed class ParserPublishGateTests
{
    private static readonly MappingTableCatalog Tables =
        MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    private static ParserPublishGate Gate() =>
        new(new ParserCompiler(new GrokCompiler(RepositoryLayout.DefaultLibrary), Tables));

    private const string Saglikli = """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: test.gate
          version: 1.0.0
          vendor: Test
          product: Gate
        match:
          transport: [syslog]
          contains: ["GATE-TEST"]
        pipeline:
          - grok:
              field: message
              patterns:
                - '^GATE-TEST %{WORD:action} %{IPV4:src_ip}$'
        map:
          core:
            action: "{{ action }}"
            src_ip: "{{ src_ip }}"
        tests:
          - name: temel
            input: 'GATE-TEST accept 10.0.0.1'
            expect:
              parse_status: ok
              core.action: "accept"
              core.src_ip: "10.0.0.1"
        """;

    [Fact]
    public void Saglikli_parser_geciyor()
    {
        var verdict = Gate().Inspect(Saglikli);

        Assert.True(verdict.Ok, string.Join(" | ", verdict.Errors));
        Assert.Equal("test.gate", verdict.ParserId);
        Assert.Equal("1.0.0", verdict.Version);
        Assert.Equal(1, verdict.PassingTests);
    }

    [Fact]
    public void Sema_hatasi_reddediliyor()
    {
        var verdict = Gate().Inspect("metadata:\n  id: eksik\n");

        Assert.False(verdict.Ok);
        Assert.NotEmpty(verdict.Errors);
    }

    /// <summary>
    /// Gömülü test şema düzeyinde zorunlu; kapı bunu ayrıca doğruluyor çünkü
    /// testsiz parser'ın doğru çalıştığı hiçbir zaman gösterilemez.
    /// </summary>
    [Fact]
    public void Testsiz_parser_reddediliyor()
    {
        var withoutTests = Saglikli[..Saglikli.IndexOf("tests:", StringComparison.Ordinal)] + "tests: []\n";
        var verdict = Gate().Inspect(withoutTests);

        Assert.False(verdict.Ok);
    }

    /// <summary>Beklentisi tutmayan test yayını durduruyor.</summary>
    [Fact]
    public void Dusen_test_yayini_durduruyor()
    {
        // Beklenti değiştiriliyor, girdi değil: parser doğru çalışıyor ama test
        // yanlış şeyi bekliyor — yayının durması gereken hâl.
        var verdict = Gate().Inspect(
            Saglikli.Replace("core.action: \"accept\"", "core.action: \"deny\"", StringComparison.Ordinal));

        Assert.False(verdict.Ok);
        Assert.Contains(verdict.Errors, e => e.Contains("Test düştü", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Asıl bekçi.</b> Geri izlemeye düşen bir pattern (<c>GROK003</c>) uyarı
    /// seviyesinde ama yayında <b>hata</b> sayılıyor. Lookaround taşıyan bir
    /// ifade doğrusal motorda derlenemiyor, dolayısıyla kataloğun sıfır GROK003
    /// değişmezini kırıyor.
    /// </summary>
    [Fact]
    public void Geri_izlemeye_dusen_pattern_yayini_durduruyor()
    {
        var verdict = Gate().Inspect(Saglikli.Replace(
            "'^GATE-TEST %{WORD:action} %{IPV4:src_ip}$'",
            "'^GATE-TEST (?<=X)%{WORD:action} %{IPV4:src_ip}$'",
            StringComparison.Ordinal));

        Assert.False(verdict.Ok);
        Assert.Contains(verdict.Errors, e => e.Contains("GROK003", StringComparison.Ordinal));
    }
}

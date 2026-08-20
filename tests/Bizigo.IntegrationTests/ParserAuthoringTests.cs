using Bizigo.Authoring;
using Bizigo.ControlPlane;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Parser yayın akışı (T18, K33) — gerçek Postgres'e karşı.
///
/// <para>
/// En önemli test <see cref="Yayinlanan_taslak_repodaki_dosyayi_golgeliyor"/>:
/// F2'de katalog kaynağı ikiye çıkıyor ve çakışma kuralı sessiz kalırsa bir
/// <c>git pull</c>'un neden etkisiz kaldığı hiçbir yerde yazmaz.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ParserAuthoringTests(DevStackFixture stack) : IAsyncLifetime
{
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private string _repository = string.Empty;

    // İnterpolasyon yok: grok pattern'i süslü parantez dolu ve kaçış kuralları
    // okunurluğu bitiriyordu. Yer tutucular açık, değişen üç şey belli.
    private const string Sablon = """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: __ID__
          version: __VERSION__
          vendor: Test
          product: Authoring
        match:
          transport: [syslog]
          contains: ["AUTH-TEST"]
        pipeline:
          - grok:
              field: message
              patterns:
                - '^AUTH-TEST %{WORD:action} %{IPV4:src_ip}$'
        map:
          core:
            action: "__ACTION__"
            src_ip: "{{ src_ip }}"
        tests:
          - name: temel
            input: 'AUTH-TEST accept 10.0.0.1'
            expect:
              parse_status: ok
              core.action: "__EXPECT__"
              core.src_ip: "10.0.0.1"
        """;

    /// <param name="expected">
    /// Testin beklediği değer. Varsayılan olarak <paramref name="action"/> ile
    /// aynı; farklı verildiğinde parser doğru çalışır ama gömülü test düşer —
    /// yayın kapısının reddetmesi gereken hâl.
    /// </param>
    private static string Yaml(string id, string version, string action, string? expected = null) => Sablon
        .Replace("__ID__", id, StringComparison.Ordinal)
        .Replace("__VERSION__", version, StringComparison.Ordinal)
        .Replace("__ACTION__", action, StringComparison.Ordinal)
        .Replace("__EXPECT__", expected ?? action, StringComparison.Ordinal);

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Parsers.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // Repodaki dosyaları taklit eden geçici dizin: gerçek katalog
        // kullanılsaydı test onun içeriğine bağımlı olurdu.
        _repository = Path.Combine(Path.GetTempPath(), "bizigo-authoring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repository);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_repository))
        {
            Directory.Delete(_repository, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static ParserCompiler Compiler()
    {
        var root = RepoRoot();
        var library = GrokPatternLibrary.LoadWithOverlay(
            Path.Combine(root, "catalog", "patterns", "legacy"),
            Path.Combine(root, "catalog", "patterns", "bizigo-v1"));

        return new ParserCompiler(
            new GrokCompiler(library),
            MappingTableCatalog.LoadFromDirectory(Path.Combine(root, "catalog", "mappings")));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }

    private (ParserAuthoringService Authoring, PublishedParserLoader Loader, ParserCatalog Catalog) Build()
    {
        var compiler = Compiler();
        var catalog = new ParserCatalog();
        var gate = new ParserPublishGate(compiler);

        var authoring = new ParserAuthoringService(
            _factory, gate, NullLogger<ParserAuthoringService>.Instance);

        var loader = new PublishedParserLoader(
            _factory,
            catalog,
            compiler,
            Options.Create(new Bizigo.Parsing.ParsingOptions { ParserDirectory = _repository }),
            NullLogger<PublishedParserLoader>.Instance);

        return (authoring, loader, catalog);
    }

    private async Task<Guid> PublishAsync(ParserAuthoringService authoring, string yaml)
    {
        var draft = await authoring.SaveDraftAsync(null, yaml, "test", TestContext.Current.CancellationToken);
        Assert.True(draft.Ok, draft.Error);

        var submitted = await authoring.SubmitForReviewAsync(draft.Draft!.Id, TestContext.Current.CancellationToken);
        Assert.True(submitted.Ok, submitted.Error);

        var published = await authoring.PublishAsync(draft.Draft.Id, TestContext.Current.CancellationToken);
        Assert.True(published.Ok, published.Error);

        return draft.Draft.Id;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Taslak_incelemeden_gecip_yayinlaniyor()
    {
        var (authoring, loader, catalog) = Build();

        await PublishAsync(authoring, Yaml("test.authoring", "1.0.0", "accept"));

        var report = await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Loaded);
        Assert.Equal(1, report.FromDatabase);
        Assert.Empty(report.Shadowed);
        Assert.True(catalog.Current.ByParserId.ContainsKey("test.authoring"));
    }

    /// <summary>
    /// Kapıdan geçemeyen taslak <b>incelemeye bile</b> gönderilemiyor: inceleyenin
    /// zamanını harcamak, yayını sonda reddetmekten daha pahalı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bozuk_taslak_incelemeye_gonderilemiyor()
    {
        var (authoring, _, _) = Build();

        // `map` accept yazıyor ama `expect` deny bekliyor — gömülü test düşüyor.
        var bozuk = Yaml("test.bozuk", "1.0.0", "accept", expected: "deny");

        var draft = await authoring.SaveDraftAsync(null, bozuk, "test", TestContext.Current.CancellationToken);
        Assert.True(draft.Ok);

        var submitted = await authoring.SubmitForReviewAsync(draft.Draft!.Id, TestContext.Current.CancellationToken);

        Assert.False(submitted.Ok);
        Assert.False(submitted.Verdict!.Ok);

        // T19: reddin gerekçesi editöre YAPILANDIRILMIŞ iniyor. Düz metin hata
        // listesi kullanıcıya "bir yerde bir şey yanlış" demekti; ekran hangi
        // kapıda takıldığını ve hangi testin düştüğünü satırıyla gösteriyor.
        Assert.Equal(PublishGateStage.Tests, submitted.Verdict.Stage);
        Assert.Contains(submitted.Verdict.TestResults, t => !t.Passed && t.Line > 0);
    }

    /// <summary>
    /// <b>Taslak denemesi çalışan kataloğu kirletmiyor</b> (T19) — uçtan uca.
    ///
    /// <para>
    /// <c>POST /v1/parsers/try</c> keyfi YAML'ı sunucuda derliyor ve bunun
    /// güvenli olmasının tek sebebi derlemenin ad-hoc olması. Burada gerçek
    /// yükleyiciyle dolu bir katalog kuruluyor, ardından kapı taslağı denetliyor
    /// ve <b>aynı anlık görüntünün</b> yerinde kaldığı sınanıyor: taslak
    /// kataloğa sızsaydı, herhangi bir <c>author</c> inceleme ve yayın
    /// kapılarının tamamını atlayarak boru hattının davranışını
    /// değiştirebilirdi.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Taslak_denemesi_yayindaki_katalogu_degistirmiyor()
    {
        var (authoring, loader, catalog) = Build();

        await PublishAsync(authoring, Yaml("test.canli", "1.0.0", "accept"));
        await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);

        var before = catalog.Current;
        Assert.True(before.ByParserId.ContainsKey("test.canli"));

        // Editörün denediği taslak: kataloğa hiç girmemiş, apayrı bir kimlik.
        var verdict = new ParserPublishGate(Compiler()).Inspect(Yaml("test.denenen", "0.1.0", "accept"));

        Assert.True(verdict.Ok, string.Join(" | ", verdict.Errors));
        Assert.NotNull(verdict.Compiled);

        Assert.Same(before, catalog.Current);
        Assert.DoesNotContain("test.denenen", catalog.Current.ByParserId.Keys);
    }

    /// <summary>Yayın yalnızca incelemedeki kayıttan yapılabiliyor.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Taslak_dogrudan_yayinlanamiyor()
    {
        var (authoring, _, _) = Build();

        var draft = await authoring.SaveDraftAsync(
            null, Yaml("test.dogrudan", "1.0.0", "accept"), "test", TestContext.Current.CancellationToken);

        var published = await authoring.PublishAsync(draft.Draft!.Id, TestContext.Current.CancellationToken);

        Assert.False(published.Ok);
        Assert.Contains("incelemedeki", published.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Asıl bekçi — çakışma kuralı.</b> Aynı kimlik hem repoda hem yayınlanmış
    /// taslakta varsa <b>veritabanı kazanıyor</b>, ve bu durum
    /// <see cref="CatalogSourceReport.Shadowed"/> ile görünür oluyor.
    ///
    /// <para>
    /// Görünürlük şart: bir <c>git pull</c> repodaki dosyayı güncellese bile
    /// gölgelendiği için etkisiz kalır, ve sebebi hiçbir yerde yazmazsa katkı
    /// yapan kişi neden hiçbir şeyin değişmediğini bulamaz.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yayinlanan_taslak_repodaki_dosyayi_golgeliyor()
    {
        var (authoring, loader, catalog) = Build();

        // Repoda `accept` diyen bir dosya var.
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "golge.yaml"),
            Yaml("test.golge", "1.0.0", "accept"),
            TestContext.Current.CancellationToken);

        var before = await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);
        Assert.Equal(1, before.FromRepository);
        Assert.Empty(before.Shadowed);

        // Üstüne `deny` diyen bir sürüm yayınlanıyor.
        await PublishAsync(authoring, Yaml("test.golge", "2.0.0", "deny"));

        var after = await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Equal(1, after.Loaded);
        Assert.Equal(["test.golge"], after.Shadowed);

        // Katalogda yayınlanan sürüm var, repodaki değil.
        var parser = catalog.Current.ByParserId["test.golge"];
        Assert.Equal("2.0.0", parser.Version);
    }

    /// <summary>
    /// Geri alma önceki sürümü geri getiriyor ve katalog onu koşmaya başlıyor.
    /// Kapı <b>koşmuyor</b>: geri alınan sürüm zaten yayınlanmıştı.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Geri_alma_onceki_surume_donuyor()
    {
        var (authoring, loader, catalog) = Build();

        await PublishAsync(authoring, Yaml("test.gerial", "1.0.0", "accept"));
        await PublishAsync(authoring, Yaml("test.gerial", "2.0.0", "deny"));

        await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);
        Assert.Equal("2.0.0", catalog.Current.ByParserId["test.gerial"].Version);

        var rolled = await authoring.RollbackAsync("test.gerial", TestContext.Current.CancellationToken);
        Assert.True(rolled.Ok, rolled.Error);

        await loader.LoadAsync(_repository, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", catalog.Current.ByParserId["test.gerial"].Version);
    }

    /// <summary>Aynı kimlik için tek sürüm yayında kalıyor; öncekiler emekliye ayrılıyor.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yeni_yayin_oncekini_emekliye_ayiriyor()
    {
        var (authoring, _, _) = Build();

        await PublishAsync(authoring, Yaml("test.tek", "1.0.0", "accept"));
        await PublishAsync(authoring, Yaml("test.tek", "2.0.0", "deny"));

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var published = await db.Parsers
            .Where(p => p.ParserId == "test.tek" && p.State == ParserState.Published)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(published);
        Assert.Equal("2.0.0", published[0].Version);
    }
}

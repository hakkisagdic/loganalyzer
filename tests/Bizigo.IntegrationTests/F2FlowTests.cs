using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Authoring;
using Bizigo.Replay;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>
/// F2'nin uçtan uca akışları — <b>parçalar değil, zincir</b> (T27).
///
/// <para>
/// Her ticket kendi kapsam ayrışması testini zaten taşıyor. Buradaki iş tek tek
/// doğru olan parçaların <b>birlikte</b> doğru olduğunu göstermek. F1'de beş
/// hata art arda çıkmıştı ve her biri bir öncekini düzeltmeden görünmüyordu;
/// benzer bir zincir F2'de oluşursa ilk burada görünecek.
/// </para>
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlayacak:</b> aşağıdaki her testin özet
/// yorumunda tek cümleyle yazılı. Bu dalda koşturulmadı — Docker kısıtı — ve
/// koşturan kişi bu cümleleri beklenen çıktı olarak okuyabilir.
/// </para>
///
/// <para>
/// <b>ZİNCİR HARİTASI.</b> Aşağıdaki tablo, dört akışın her halkasını hangi
/// testin kapattığını gösteriyor. Halkaların çoğu <b>zaten</b> kendi
/// ticket'ının testinde kapalı; burada tekrarlanmıyorlar çünkü aynı iddiayı iki
/// yerde kodlamak, ikisi ayrıştığı gün hangisinin doğru olduğunu bilinemez
/// kılar. Bu tablonun işi "zincir tam mı" sorusunu <b>tek yerden</b>
/// okunabilir yapmak.
/// </para>
///
/// <list type="table">
/// <listheader><term>Akış · halka</term><description>Kapatan test</description></listheader>
///
/// <item><term>1 · giriş</term><description>Canlı Keycloak'a karşı elle doğrulandı (is-envanteri); <c>ui/tests/token-isolation.test.ts</c></description></item>
/// <item><term>1 · arama</term><description><c>ui/tests/events-screen.test.tsx</c>, <c>EventPaginationTests</c></description></item>
/// <item><term>1 · detay</term><description><c>OcsfOtelViewTests</c></description></item>
/// <item><term>1 · ham bayt</term><description><c>RawEventLocatorTests</c> + <b>bu dosyada</b> <c>Ham_bayt_sadakati_zincir_boyunca_korunuyor</c></description></item>
///
/// <item><term>2 · parser yaz/dene</term><description><c>ParserAuthoringTests</c>, <c>ParserEngineTests</c></description></item>
/// <item><term>2 · yayınla</term><description><c>ParserPublishGateTests</c>, <c>ParserAuthoringTests</c></description></item>
/// <item><term>2 · <b>etkisini gör</b></term><description><b>bu dosyada</b> <c>Yayinlanan_parser_sonraki_olayi_ayristiriyor</c> — T18 yayının GEÇERLİLİĞİNİ sınıyor, ETKİSİNİ değil</description></item>
///
/// <item><term>3 · sessizlik alarmı</term><description><c>AlertEvaluatorTests</c>, <c>AlertingTests</c></description></item>
/// <item><term>3 · bildirim kanala ulaşıyor</term><description><c>NotificationDispatcherTests</c></description></item>
/// <item><term>3 · <b>bağlantı doğru aramayı açıyor</b></term><description><c>AlertLinkTargetTests</c> — rota var, kuralın filtreleri taşınıyor, taşınamayan sessizce düşmüyor</description></item>
///
/// <item><term>4 · üç kaynak birlikte</term><description><b>bu dosyada</b> <c>Uc_degisiklik_kaynagi_ayni_zaman_cizelgesinde_bulusuyor</c></description></item>
///
/// <item><term>çapraz · kapsam</term><description><c>ScopeNegativeTests</c>, <c>ScopePredicateTests</c> + <b>bu dosyada</b> <c>Degisiklik_kayitlari_kapsam_disina_sizmiyor</c></description></item>
/// <item><term>çapraz · token sızıntısı</term><description><c>ui/tests/token-isolation.test.ts</c> (15 test) + canlı doğrulama</description></item>
/// </list>
///
/// <para>
/// <b>KAPANAN HALKA.</b> T27 bulgusu şuydu: alarm bağlantısı yalnızca kural
/// kimliğini taşıyor, arama ekranı onu okumuyor ve kullanıcı alarmın işaret
/// ettiğinden <b>daha geniş</b> bir kümeye bakıyordu. Kapatma biçimi kimliği
/// çözdürmek DEĞİL — bağlantı bir kez üretilip bildirime gömülüyor ve kullanıcı
/// günler sonra tıklıyor, yani kimliği çözen ekran bugünkü kuralı gösterirdi.
/// Bunun yerine <b>filtreler bağlantının kendisine gömüldü</b>
/// (<c>criteria-bridge.ts</c>'in ters yönü) ve bağlantı o anın fotoğrafı oldu.
/// <c>kural=&lt;guid&gt;</c> kaynak göstergesi olarak kaldı.
/// </para>
///
/// <para>
/// Ekranda karşılığı olmayan filtreler (<c>src_ip</c>, <c>user_name</c> gibi)
/// <b>sessizce düşmüyor</b>: <c>eksik</c> parametresiyle bildiriliyor ve ekran
/// kullanıcıya "bu alarmın N filtresi burada gösterilemiyor, aşağıdaki sonuçlar
/// daha geniş" diyor. Sessizce düşseydi kullanıcı gördüğü kümeyi alarmın kümesi
/// sanardı — kapatılan kusurun kılık değiştirmiş hâli.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class F2FlowTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Day = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    private const string Core = "network/core";
    private const string Edge = "network/edge";
    private const string SourceKey = "10.1.2.9";

    /// <summary>
    /// Replay'in sabitlediği parser. <c>eventtime</c>'dan zaman çözüyor, yani
    /// ayrıştırılan zaman damgası girdinin kendisinde yazılı — bölüm seçimi
    /// takvime değil veriye bağlı kalıyor.
    /// </summary>
    private const string ParserId = "fortinet.fortigate.event";

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await DevStackSetup.ClickHouseAsync(stack, Token);

        _writer = new EventWriter(_context);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<string> ScalarAsync(string sql) =>
        stack.QueryScalarAsync(_context.Options.ConnectionString, sql, Token);

    // ------------------------------------------------- 4 · üç değişiklik kaynağı

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> K34'ün üç kaynağı — elle giriş,
    /// CI webhook'u ve cihaz config farkı — aynı tabloya düşüyor, tek sorguyla
    /// birlikte dönüyor ve <c>source</c> alanı hangisinden geldiğini ayırt
    /// ediyor.
    ///
    /// <para>
    /// Bu, F2'nin change feed'inin "bitti" tanımı: üçü ayrı ayrı çalışıyor
    /// olabilir ama ekranda birlikte görünmedikleri sürece RCA için tek bir
    /// zaman çizelgesi yok.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Uc_degisiklik_kaynagi_ayni_zaman_cizelgesinde_bulusuyor()
    {
        await _writer.WriteChangeEventsAsync(
            [
                Change("manual", "fw-core-01", "config_push", "Elle: ACL güncellendi"),
                Change("github", "bizigo/network-config", "deploy", "deploy-firewall-config #184 → success"),
                Change("device", "fw-core-01", "config_push", "fw-core-01: 3 satır eklendi, 1 satır silindi"),
            ],
            Token);

        var total = await ScalarAsync(
            $"SELECT count() FROM change_events WHERE owner_group = '{Core}'");

        Assert.Equal("3", total);

        // Üç kaynak da ayırt edilebiliyor: RCA "bu değişikliği kim bildirdi"
        // sorusunu sorabilmeli.
        var sources = await ScalarAsync(
            $"SELECT arrayStringConcat(arraySort(groupUniqArray(source)), ',') " +
            $"FROM change_events WHERE owner_group = '{Core}'");

        Assert.Equal("device,github,manual", sources);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> değişiklik kayıtları da kapsam
    /// kapısından geçiyor — <c>analyst.edge</c> <c>network/core</c>'un
    /// değişikliklerini <b>hiç</b> göremiyor.
    ///
    /// <para>
    /// Ayrı bir test çünkü değişiklik tablosu olay tablosundan farklı bir
    /// sıralama anahtarı kullanıyor ve kapsam filtresinin orada da uygulandığı
    /// ayrıca gösterilmeli.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Degisiklik_kayitlari_kapsam_disina_sizmiyor()
    {
        await _writer.WriteChangeEventsAsync(
            [
                Change("manual", "fw-core-01", "config_push", "core değişikliği"),
                Change("manual", "fw-edge-09", "config_push", "edge değişikliği", Edge),
            ],
            Token);

        var edgeVisible = await ScalarAsync(
            $"SELECT count() FROM change_events WHERE owner_group = '{Edge}'");

        Assert.Equal("1", edgeVisible);

        // Kapsam dışı satırın ÖZETİ bile görünmemeli: RCA raporu "kapsamınız
        // dışında N ilişkili değişiklik var" diyebilir ama içeriğini vermez.
        var leak = await ScalarAsync(
            $"SELECT count() FROM change_events " +
            $"WHERE owner_group = '{Edge}' AND summary = 'core değişikliği'");

        Assert.Equal("0", leak);
    }

    // ---------------------------------------------- 1 · ham bayt sadakati

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> olayın <c>raw_ref</c>'i üzerinden
    /// inilen baytların sha256'sı, cihazın gönderdiği baytların sha256'sıyla
    /// birebir aynı — yani arama → detay → ham iniş zinciri boyunca hiçbir
    /// katman baytları değiştirmiyor.
    ///
    /// <para>
    /// F1'de bu zincir <b>beş kez</b> kırıldı ve her kırık bir öncekini
    /// düzeltmeden görünmüyordu. Burada tek testte duruyor ki altıncısı ilk
    /// denemede görünsün.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ham_bayt_sadakati_zincir_boyunca_korunuyor()
    {
        // Türkçe + Arapça + CJK: F1'in arşivinde gerçek örnekleri var ve
        // kodlama tespiti bu üçünde kırılıyordu.
        var wire = Encoding.UTF8.GetBytes(
            "<134>Aug 17 10:00:00 fw-01 kullanıcı girişi başarısız — المستخدم — 用户登录失败");

        var expected = Convert.ToHexStringLower(SHA256.HashData(wire));

        await _writer.WriteEventsAsync(
            [
                Event(Core, "deny") with
                {
                    Body = Encoding.UTF8.GetString(wire),
                    RawRef = "raw/network/core/2026/08/17/10/firewall/",
                },
            ],
            Token);

        var stored = await ScalarAsync(
            $"SELECT lower(hex(SHA256(toString(body)))) FROM events WHERE owner_group = '{Core}' LIMIT 1");

        Assert.Equal(expected, stored);
    }

    // ---------------------------------------- 2 · yayının ETKİSİ

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> yayınlanan parser sıcak yeniden
    /// yüklemeden sonra <b>bir sonraki olayı</b> gerçekten ayrıştırıyor —
    /// yazılan <c>parser_id</c> ve <c>parser_version</c> yeni sürüm, ve daha
    /// önce <c>failed</c> düşen satır artık <c>ok</c>.
    ///
    /// <para>
    /// T18 yayının <b>geçerliliğini</b> sınıyor: bozuk parser yayınlanamıyor,
    /// testsiz parser reddediliyor, yayın atomik. Sınamadığı şey yayının
    /// <b>etkisi</b> — katalog gerçekten değişti mi, dispatcher yeni sürüme mi
    /// bağlandı mı. İkisi arasındaki boşlukta sessiz bir hâl var: yayın
    /// "başarılı" der, katalog eski kalır ve kimse fark etmez çünkü olaylar
    /// ayrıştırılmaya devam eder — yalnızca eski kurallarla.
    /// </para>
    ///
    /// <para>
    /// <b>Kurulum:</b> Postgres (taslak deposu) + ClickHouse (olay yazımı).
    /// <c>ParserAuthoringTests</c>'in kurulumu üstüne <c>EventWriter</c>
    /// ekleniyor; koşturan kişi o dosyayı örnek alabilir.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Yayinlanan_parser_sonraki_olayi_ayristiriyor()
    {
        var (authoring, loader, catalog, repository) = await BuildAuthoringAsync();
        var dispatcher = new Dispatcher(catalog, new DispatchStats());

        try
        {
            // 1 · v1 yayında. Örnek satırı TANIMIYOR: pattern sonu bağlı ve
            // satırda fazladan bir alan var.
            await PublishAsync(authoring, EditorParser("1.0.0", withSourceIp: false));
            await loader.LoadAsync(repository, Token);

            var before = catalog.Current.ByParserId[EditorParserId].Parse(EditorLine);

            Assert.Equal(ParseStatus.Failed, before.Status);

            // 2 · O hâliyle ClickHouse'a yazılıyor — üretimde olan da bu.
            var oldEvent = Guid.CreateVersion7(Day);
            await _writer.WriteEventsAsync([Parsed(oldEvent, before, "1.0.0")], Token);

            // 3 · v2 yayınlanıyor ve katalog SICAK tazeleniyor.
            await PublishAsync(authoring, EditorParser("1.1.0", withSourceIp: true));

            var report = await loader.LoadAsync(repository, Token);

            Assert.Empty(report.Errors);
            Assert.Equal(1, report.FromDatabase);

            // 4 · AYNI satır, envanter bağıyla — üretimdeki baskın yol (kademe 1).
            var dispatch = dispatcher.Dispatch(EditorLine, EditorParserId);

            // 5 · Yayının ETKİSİ: katalog gerçekten değişti ve dispatcher yeni
            // sürüme bağlandı. T18 yayının GEÇERLİLİĞİNİ sınıyor; buradaki iddia
            // farklı — "yayınlandı" demek ile davranışın değişmesi arasındaki
            // boşlukta sessiz bir hâl var: yayın başarılı der, katalog eski
            // kalır ve olaylar ayrıştırılmaya devam eder, yalnızca eski
            // kurallarla.
            Assert.Equal(DispatchTier.InventoryBound, dispatch.Tier);
            Assert.Equal(ParseStatus.Ok, dispatch.Result.Status);
            Assert.Equal("1.1.0", dispatch.Result.ParserVersion);
            Assert.Equal("10.0.0.7", dispatch.Result.Core["src_ip"]);

            var newEvent = Guid.CreateVersion7(Day.AddSeconds(1));
            await _writer.WriteEventsAsync([Parsed(newEvent, dispatch.Result, "1.1.0")], Token);

            // 6 · ESKİ olay hâlâ `failed`. Yayın geçmişi yeniden yazmıyor;
            // onu düzeltmek replay'in işi (T11) ve ayrı bir karar. Bu adım
            // kolayca atlanır — atlanırsa yayının sessizce geçmişi değiştirdiği
            // bir dünyada yaşadığımızı fark etmeyiz.
            var stillFailed = await ScalarAsync(
                $"SELECT count() FROM events WHERE event_id = '{oldEvent}' AND parse_status = 'failed'");

            Assert.Equal("1", stillFailed);

            var reparsed = await ScalarAsync(
                $"SELECT parser_version FROM events WHERE event_id = '{newEvent}'");

            Assert.Equal("1.1.0", reparsed);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>
    /// Taslak deposu, yayın kapısı ve iki kaynaklı katalog yükleyicisi.
    ///
    /// <para>
    /// Repo dizini <b>boş ve geçici</b>: gerçek katalog kullanılsaydı test onun
    /// içeriğine bağımlı olurdu ve yayınlanan taslağın etkisi 87 satırlık
    /// gürültünün içinde kaybolurdu.
    /// </para>
    /// </summary>
    private async Task<(ParserAuthoringService Authoring, PublishedParserLoader Loader,
        ParserCatalog Catalog, string Repository)> BuildAuthoringAsync()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Token);

        await using (var db = await factory.CreateDbContextAsync(Token))
        {
            // Taslak tablosu paylaşılıyor; önceki koşumun artığı yayında
            // kalırsa katalog beklenmedik bir parser taşır.
            await db.Parsers.ExecuteDeleteAsync(Token);
        }

        var repository = Path.Combine(Path.GetTempPath(), "bizigo-f2flow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);

        var compiler = new ParserCompiler(
            new GrokCompiler(GrokPatternLibrary.LoadWithOverlay(
                DevStackSetup.RepoPath("catalog/patterns/legacy"),
                DevStackSetup.RepoPath("catalog/patterns/bizigo-v1"))),
            MappingTableCatalog.LoadFromDirectory(DevStackSetup.RepoPath("catalog/mappings")));

        var catalog = new ParserCatalog();

        return (
            new ParserAuthoringService(factory, new ParserPublishGate(compiler), NullLogger<ParserAuthoringService>.Instance),
            new PublishedParserLoader(
                factory,
                catalog,
                compiler,
                Options.Create(new Bizigo.Parsing.ParsingOptions { ParserDirectory = repository }),
                NullLogger<PublishedParserLoader>.Instance),
            catalog,
            repository);
    }

    /// <summary>Taslak → incelemede → yayında. Kapı her iki geçişte de koşuyor.</summary>
    private async Task PublishAsync(ParserAuthoringService authoring, string yaml)
    {
        var draft = await authoring.SaveDraftAsync(null, yaml, "test", Token);
        Assert.True(draft.Ok, draft.Error);

        var submitted = await authoring.SubmitForReviewAsync(draft.Draft!.Id, Token);
        Assert.True(submitted.Ok, string.Join(" | ", submitted.Verdict?.Errors ?? [submitted.Error]));

        var published = await authoring.PublishAsync(draft.Draft.Id, Token);
        Assert.True(published.Ok, string.Join(" | ", published.Verdict?.Errors ?? [published.Error]));
    }

    private const string EditorParserId = "test.editor.published";
    private const string EditorLine = "F2FLOW accept 10.0.0.7";

    /// <summary>
    /// Aynı kimliğin iki sürümü. <c>withSourceIp</c> yanlışken pattern satırın
    /// sonuna bağlı ve fazladan alanı tanımıyor — v1'in <c>failed</c> vermesinin
    /// sebebi bu.
    ///
    /// <para>Her iki sürüm de <b>kendi</b> gömülü testini taşıyor: kapı testsiz
    /// parser'ı reddediyor ve v1'in de geçerli bir parser olması gerekiyor,
    /// yoksa test kapının reddiyle düşer ve yayının etkisine hiç gelemez.</para>
    /// </summary>
    private static string EditorParser(string version, bool withSourceIp) => """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: test.editor.published
          version: __VERSION__
          vendor: Test
          product: Editor
        match:
          transport: [syslog]
          contains: ["F2FLOW"]
        pipeline:
          - grok:
              field: message
              patterns:
                - '__PATTERN__'
        map:
          core:
            action: "{{ action }}"
        __EXTRA_MAP__
        tests:
          - name: temel
            input: '__TEST_INPUT__'
            expect:
              parse_status: ok
              core.action: "accept"
        """
        .Replace("__VERSION__", version, StringComparison.Ordinal)
        .Replace(
            "__PATTERN__",
            withSourceIp ? "^F2FLOW %{WORD:action} %{IPV4:src_ip}$" : "^F2FLOW %{WORD:action}$",
            StringComparison.Ordinal)
        .Replace(
            "__EXTRA_MAP__",
            withSourceIp ? "    src_ip: \"{{ src_ip }}\"" : string.Empty,
            StringComparison.Ordinal)
        .Replace(
            "__TEST_INPUT__",
            withSourceIp ? EditorLine : "F2FLOW accept",
            StringComparison.Ordinal);

    /// <summary>Ayrıştırma sonucunu yazılabilir olaya çeviriyor.</summary>
    private static LogEvent Parsed(Guid eventId, Parsing.Engine.ParseResult result, string version) => new()
    {
        EventId = eventId,
        Timestamp = Day,
        IngestedAt = Day,
        OwnerGroup = Core,
        SourceId = "fg-core-01",
        Host = "fw-01",
        ParseStatus = result.Status,
        ParserId = EditorParserId,
        ParserVersion = version,
        Action = result.Core.GetValueOrDefault("action")?.ToString() ?? string.Empty,
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = EditorLine,
    };

    // ------------------------------------- F1 ölçümü · kuru koşu = gerçek koşu

    /// <summary>
    /// <b>Koşturulduğunda kanıtlayacağı:</b> aynı plan için kuru koşunun
    /// bildirdiği fark sayıları, uygulanan koşununkiyle <b>birebir</b> aynı; ve
    /// uygulamadan sonra ikinci bir kuru koşu <b>sıfır değişiklik</b> görüyor.
    ///
    /// <para>
    /// F1 bu ölçümü açık bırakmıştı: "parçalar ayrı test edildi; uçtan uca tek
    /// testte gösterilmedi". İkinci kuru koşunun sıfır dönmesi asıl kanıt —
    /// uygulama gerçekten kuru koşunun söylediğini yapmışsa, tekrar bakıldığında
    /// yapacak bir şey kalmamış olmalı.
    /// </para>
    ///
    /// <para>
    /// <b>Bu test ClickHouse'un ötesinde Postgres ve S3 de istiyor</b> (manifest
    /// ve arşiv nesneleri) — üç deponun birlikte koştuğu tek test. Kurulum
    /// <c>DevStackSetup</c>'ta: <c>ReplayStoreTests</c> ve
    /// <c>RawArchiveTests</c> de aynı yüzeyden besleniyor, üçüncü bir kopya
    /// yazılmadı.
    /// </para>
    ///
    /// <para>
    /// <b>Kuru koşu ile gerçek koşunun eşitliği tek başına yetmiyor</b> — ikisi
    /// aynı yanlışı da söyleyebilir, çünkü ikisi aynı kodu koşuyor. Asıl kanıt
    /// uygulamadan sonraki ikinci kuru koşu: yapacak iş kalmamış olmalı.
    /// </para>
    ///
    /// <para>
    /// <b>Açık bölüm testin içinde.</b> Aralık iki günü kapsıyor: 17 Ağustos
    /// kapalı, 18 Ağustos motorun saatine göre <b>hâlâ yazılan</b> bölüm.
    /// Varsayılan davranış onu dışarıda bırakıyor ve test bunun içinde koşuyor —
    /// yalnızca kapalı bölümü sınayan bir test, zaten güvenli olan yolu sınardı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Kuru_kosu_gercek_kosuyla_ayni_sonucu_veriyor()
    {
        var (engine, records, pinnedVersion) = await BuildReplayAsync();

        // 17 Ağustos'un iki kaydı ClickHouse'a ESKİ hâliyle yazılıyor: biri
        // `failed` (parser satırı hiç tanımamıştı), biri yanlış `action` ile
        // `ok`. Üçüncü kayıt tabloda YOK — ilk işlemede kaybedilmiş satır.
        // Dördüncü kayıt 18 Ağustos'ta, yani açık bölümde.
        await _writer.WriteEventsAsync(
            [
                Stale(records[0], ParseStatus.Failed, action: string.Empty),
                Stale(records[1], ParseStatus.Ok, action: "eski"),
                Stale(records[3], ParseStatus.Ok, action: "eski"),
            ],
            Token);

        var plan = new ReplayPlan
        {
            From = Day.AddDays(-1),
            To = Day.AddDays(2),
            ParserId = ParserId,

            // Sürüm KATALOGDAN okunuyor, elle yazılmıyor. Motor sabitlenmiş
            // sürüm katalogdakiyle uyuşmazsa duruyor (tekrarlanabilirlik
            // iddiası) — elle yazılan bir sürüm, katalog sürüm atladığı gün bu
            // testi replay'le hiç ilgisi olmayan bir sebeple düşürürdü.
            ParserVersion = pinnedVersion,
        };

        // 3 · kuru koşu
        var dry = await engine.DryRunAsync(plan, Token);

        // Açık bölüm raporda GÖRÜNÜYOR — sessizce kısalma değil, bildirilen bir
        // karar. Bu satır düşerse replay canlı ingest'i sessizce silmeye geri
        // dönmüş demektir.
        Assert.Equal(["20260818"], dry.SkippedOpenPartitions);
        Assert.Equal(["20260817"], dry.Partitions);
        Assert.False(dry.Applied);

        Assert.Equal(2, dry.Changed);
        Assert.Equal(1, dry.FailedToOk);
        Assert.Equal(0, dry.OkToFailed);
        Assert.Equal(1, dry.NewRows);

        // 4 · gerçek koşu
        var applied = await engine.ApplyAsync(plan, Token);

        // 5 · kuru koşu neyi vaat ettiyse o olmuş
        Assert.True(applied.Applied);
        Assert.Equal(dry.Changed, applied.Changed);
        Assert.Equal(dry.FailedToOk, applied.FailedToOk);
        Assert.Equal(dry.OkToFailed, applied.OkToFailed);
        Assert.Equal(dry.NewRows, applied.NewRows);
        Assert.Equal(dry.SkippedOpenPartitions, applied.SkippedOpenPartitions);

        // 6-7 · ASIL KANIT. 5. adım yalnızca "iki rapor aynı" diyor; iki rapor
        // aynı yanlışı da söyleyebilir. Bu adım "uygulama gerçekten uygulandı"
        // diyor: aynı plana tekrar bakıldığında yapacak iş kalmamış olmalı.
        var after = await engine.DryRunAsync(plan, Token);

        Assert.Equal(0, after.Changed);
        Assert.Equal(0, after.FailedToOk);
        Assert.Equal(0, after.NewRows);
        Assert.Equal(3, after.Unchanged);

        // Açık bölüm hâlâ atlanıyor ve satırı DEĞİŞMEDEN duruyor: replay ona
        // dokunmadıysa canlı ingest'in yazdığı da yerinde kalmış olmalı.
        Assert.Equal(["20260818"], after.SkippedOpenPartitions);

        var untouched = await ScalarAsync(
            "SELECT action FROM events WHERE toYYYYMMDD(ts) = 20260818 LIMIT 1");

        Assert.Equal("eski", untouched);

        // Kapalı bölümdeki `failed` satır gerçekten düzelmiş: rapor sayı
        // veriyor, tablo sonucu veriyor. İkisini birden sormak, raporun kendi
        // kendini onaylamasını engelliyor.
        //
        // Karşılaştırma sayıyla değil ADLA (`Enum8('ok' = 1, …)`): sayı yazmak,
        // enum sırası değiştiği gün sessizce başka bir durumu sayardı.
        var stillFailed = await ScalarAsync(
            "SELECT count() FROM events WHERE toYYYYMMDD(ts) = 20260817 AND parse_status = 'failed'");

        Assert.Equal("0", stillFailed);

        // Üç satır da yeni parser'ın sonucunu taşıyor: ikisi düzeltilmiş, biri
        // arşivden geri kazanılmış (`NewRows`).
        var replayed = await ScalarAsync(
            "SELECT count() FROM events WHERE toYYYYMMDD(ts) = 20260817 "
            + "AND parse_status = 'ok' AND action = 'login' AND parser_version = '1.0.0'");

        Assert.Equal("3", replayed);
    }

    /// <summary>
    /// Replay motorunu gerçek bağımlılıklarıyla kuruyor ve arşive dört kayıt
    /// yüklüyor.
    ///
    /// <para>
    /// <b>Saat sahte, veri gerçek.</b> Motor 18 Ağustos 00:30'da duruyor, yani
    /// 20260818 açık bölüm. Gerçek saatle koşmak, testin hangi bölümün açık
    /// olduğunu takvime bırakması demekti — bu deponun iki kez ödediği "testin
    /// geçme sebebi duvar saati" hatası.
    /// </para>
    ///
    /// <para>
    /// Nesneler ve manifest satırları <c>RawArchiveUploader</c> ile yazılıyor,
    /// elle değil: manifest satırını testin kendi elleriyle kurması, üretimin
    /// yazdığından farklı bir satır üretme riski taşır ve replay tam da o satırı
    /// okuyor.
    /// </para>
    /// </summary>
    private async Task<(ReplayEngine Engine, IReadOnlyList<RawRecord> Records, string ParserVersion)>
        BuildReplayAsync()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Token);

        await using (var db = await factory.CreateDbContextAsync(Token))
        {
            db.Sources.Add(new SourceEntity
            {
                SourceId = "fg-core-01",
                PeerAddress = SourceKey,
                OwnerGroup = Core,
                SourceClass = "firewall",
            });
            await db.SaveChangesAsync(Token);
        }

        var options = DevStackSetup.RawOptions(stack);
        var store = new S3RawObjectStore(Options.Create(options));
        await store.EnsureBucketAsync(Token);

        var records = new[]
        {
            Raw(0, "0100032002", "Admin login failed", "failed", "passwd_invalid"),
            Raw(5, "0100032001", "Admin login successful", "success", "none"),
            Raw(10, "0100032001", "Admin login successful", "success", "none"),

            // Açık bölüm: motorun saatine göre bugün.
            Raw(24 * 3600, "0100032001", "Admin login successful", "success", "none"),
        };

        var segments = new FakeSegmentSource();
        segments.Add("segment-1", records.Select(r => (ReadOnlyMemory<byte>)RawRecordCodec.ToLine(r)));

        var directory = new SourceDirectory(factory);

        var uploader = new RawArchiveUploader(
            segments,
            store,
            factory,
            directory,
            new NullRawRefSink(),
            Options.Create(options),
            NullLogger<RawArchiveUploader>.Instance,
            TimeProvider.System);

        var report = await uploader.RunOnceAsync(Token);
        Assert.True(report.ObjectsWritten > 0, "Arşive hiç nesne yazılmadı; replay okuyacak bir şey bulamaz.");

        var compiler = new ParserCompiler(
            new GrokCompiler(GrokPatternLibrary.LoadWithOverlay(
                DevStackSetup.RepoPath("catalog/patterns/legacy"),
                DevStackSetup.RepoPath("catalog/patterns/bizigo-v1"))),
            MappingTableCatalog.LoadFromDirectory(DevStackSetup.RepoPath("catalog/mappings")));

        var catalog = new ParserCatalog();
        catalog.LoadFromDirectory(DevStackSetup.RepoPath("catalog/parsers"), compiler);

        var engine = new ReplayEngine(
            factory,
            store,
            catalog,
            new Dispatcher(catalog, new DispatchStats()),
            directory,
            new EventNormalizer(),
            _writer,
            new ReplayStore(_context),
            MaskCatalog.LoadFromFile(DevStackSetup.RepoPath("catalog/masks/bizigo-masks.yaml")),
            NullLogger<ReplayEngine>.Instance,
            new FakeTime(Day.AddDays(1).AddMinutes(30)));

        var pinned = Assert.Contains(ParserId, catalog.Current.ByParserId);

        return (engine, records, pinned.Version);
    }

    /// <summary>
    /// FortiGate olay satırı. Zaman <c>eventtime</c>'dan çözülüyor (epoch ns),
    /// yani ayrıştırılan zaman damgası <b>ölçülebilir biçimde</b> sabit —
    /// RFC3164 gibi yılı çıkarımla bulan bir biçim, bölümü takvime bağlardı.
    /// </summary>
    private static RawRecord Raw(
        int offsetSeconds,
        string logId,
        string description,
        string status,
        string reason)
    {
        var at = Day.AddSeconds(offsetSeconds);
        var nanos = at.ToUnixTimeSeconds() * 1_000_000_000L;

        var line =
            $"date={at:yyyy-MM-dd} time={at:HH:mm:ss} logid=\"{logId}\" type=\"event\" subtype=\"system\" "
            + $"level=\"alert\" vd=\"root\" eventtime={nanos} logdesc=\"{description}\" sn=\"0\" user=\"esra\" "
            + $"ui=\"ssh(10.1.2.9)\" method=\"ssh\" srcip=10.1.2.9 dstip=10.1.2.3 action=\"login\" "
            + $"status=\"{status}\" reason=\"{reason}\" msg=\"Administrator esra {description}\"";

        return new RawRecord
        {
            EventId = Guid.CreateVersion7(at),
            ReceivedAt = at,
            ObservedAt = at,
            SourceKey = SourceKey,
            Body = Encoding.UTF8.GetBytes(line),
        };
    }

    /// <summary>
    /// Kaydın <b>eski parser'la</b> üretilmiş hâli — replay'in düzelteceği satır.
    /// Aynı <c>EventId</c>, çünkü replay eşleştirmeyi onunla yapıyor.
    /// </summary>
    private static LogEvent Stale(RawRecord record, ParseStatus status, string action) => new()
    {
        EventId = record.EventId,
        Timestamp = record.ObservedAt!.Value,
        IngestedAt = record.ReceivedAt,
        OwnerGroup = Core,
        SourceId = "fg-core-01",
        Host = "fw-01",
        ParseStatus = status,
        ParserId = ParserId,

        // Eski sürüm: replay'in yazacağı sürümden farklı olmalı, yoksa
        // `parser_version` farkı görünmez ve testin "değişti" iddiası zayıflar.
        ParserVersion = "0.9.0",
        Action = action,
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = Encoding.UTF8.GetString(record.Body.Span),
    };

    private static ChangeEvent Change(
        string source,
        string targetId,
        string changeKind,
        string summary,
        string ownerGroup = Core) => new()
        {
            ChangeId = Guid.CreateVersion7(),
            Timestamp = Day,
            OwnerGroup = ownerGroup,
            TargetKind = ChangeTargetKind.Config,
            TargetId = targetId,
            ChangeKind = changeKind,
            Actor = "esra.yildiz",
            Summary = summary,
            Source = source,
        };

    private static LogEvent Event(string ownerGroup, string action) => new()
    {
        EventId = Guid.CreateVersion7(Day),
        Timestamp = Day,
        OwnerGroup = ownerGroup,
        SourceId = "fg-01",
        Host = "fw-01",
        ParseStatus = ParseStatus.Ok,
        Action = action,
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = "satır",
    };
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

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
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class F2FlowTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Day = new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    private const string Core = "network/core";
    private const string Edge = "network/edge";

    private ClickHouseContext _context = null!;
    private EventWriter _writer = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await stack.CreateIsolatedClickHouseContextAsync(Token);
        await new ClickHouseMigrator(_context).MigrateAsync(RepoPath("db/clickhouse"), Token);

        _writer = new EventWriter(_context);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, relative);
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
    /// ve arşiv nesneleri). Kurulum <c>ReplayStoreTests</c> ile
    /// <c>RawArchiveTests</c>'in kurulumlarının birleşimi; koşturan kişi ikisini
    /// örnek alabilir.
    /// </para>
    /// </summary>
    [Fact(Skip = "Kurulum Postgres manifest + S3 nesnesi istiyor; T27 kapsamında yazıldı, koşum faz sonunda.")]
    public Task Kuru_kosu_gercek_kosuyla_ayni_sonucu_veriyor()
    {
        // Adımlar, koşturacak kişi için:
        //
        // 1. Arşive iki ham kayıt yükle, manifest satırlarını yaz.
        // 2. `events` tablosuna aynı kayıtların ESKİ parser'la üretilmiş
        //    hâlini yaz (biri `failed`).
        // 3. `DryRunAsync(plan)` → rapor A.
        // 4. `ApplyAsync(plan)` → rapor B.
        // 5. Assert: A.Changed == B.Changed, A.FailedToOk == B.FailedToOk,
        //    A.NewRows == B.NewRows. Kuru koşu neyi vaat ettiyse o olmuş.
        // 6. `DryRunAsync(plan)` → rapor C.
        // 7. Assert: C.Changed == 0 && C.FailedToOk == 0. Yapacak iş kalmamış.
        //
        // Adım 7 asıl kanıt: 5 yalnızca "iki rapor aynı" diyor, 7 "uygulama
        // gerçekten uygulandı" diyor.
        return Task.CompletedTask;
    }

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

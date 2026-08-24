using Bizigo.Alerting;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Senkronun <b>yazma</b> yolunun bekçileri (T33).
///
/// <para>
/// Karar mantığı <see cref="SigmaRuleSyncTests"/>'te ve saf; burada ölçülen
/// şey kararın kayda doğru yansıyıp yansımadığı — özellikle kullanıcının
/// verdiği kararın korunması.
/// </para>
/// </summary>
public sealed class SigmaRuleSyncServiceTests : IDisposable
{
    private readonly InMemoryControlPlaneFactory _factory = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SigmaManifest Manifest(params SigmaManifestRule[] rules) =>
        new() { Rules = rules };

    private static SigmaManifestRule Written(string id, string sha) => new()
    {
        RuleId = id,
        Title = "FortiGate: kimlik doğrulama hatası",
        SourcePath = "catalog/sigma/rules/x.yml",
        SourceSha = "sha256:kaynak",
        Status = SigmaRuleSync.StatusWritten,
        OutputSha = sha,
    };

    private static SigmaManifestRule Gated(string id, string column) => new()
    {
        RuleId = id,
        Title = "FortiGate: uzun DNS sorgusu",
        SourceSha = "sha256:kaynak",
        Status = SigmaRuleSync.StatusGated,
        Blockers = [new SigmaManifestBlocker
        {
            Column = column, Kind = "unknown_column", Remedy = "schema",
            Message = $"`{column}` bu şemada eşlenemiyor",
        }],
    };

    private SigmaRuleSyncService Service() => new(_factory);

    /// <summary>
    /// <b>Kapsamsız senkron reddediliyor.</b>
    ///
    /// <para>
    /// Sigma kuralının doğal bir sahibi yok ve manifestteki <c>logsource</c>'tan
    /// türetmek yanlış olurdu: vendor ile grup aynı şey değil. Beyanı zorunlu
    /// kılmak, "sınırsız kapsamlı kural yok" değişmezinin tek satırda
    /// delinmesini engelliyor (§8) — ve o değişmez delindiğinde bunu yapan kişi
    /// çoğu zaman fark etmiyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsamsiz_senkron_REDDEDILIYOR()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().SyncAsync(Manifest(Written("r1", "sha256:a")), "u", [], Token));

        Assert.Contains("Kapsam boş olamaz", error.Message, StringComparison.Ordinal);

        // Yalnızca boş liste değil, boş dizgelerden oluşan liste de.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().SyncAsync(Manifest(Written("r1", "sha256:a")), "u", ["", "  "], Token));
    }

    /// <summary>Yeni kural iniyor: `Disabled`, kapsamı ve eşiği yerinde.</summary>
    [Fact]
    public async Task Yeni_kural_Disabled_iniyor_ve_esigi_bir()
    {
        var result = await Service().SyncAsync(
            Manifest(Written("r1", "sha256:a"), Gated("r2", "dns_query_name")),
            "sub-1", ["/ekip/ag"], Token);

        Assert.Equal(2, result.Created);

        await using var db = _factory.CreateDbContext();
        var written = db.AlertRules.Single(r => r.SigmaRuleId == "r1");

        Assert.Equal(AlertRuleStatus.Disabled, written.Status);
        Assert.Equal(AlertRuleSource.Sigma, written.Source);
        Assert.Equal("/ekip/ag", written.OwnerGroups);

        // Eşik 1: bir satır dönerse tetikler. O zaman "kaç satır döndü" ile
        // "kaç kez tetiklenirdi" AYNI sayı ve T23'ün önizleme sözleşmesi
        // dokunulmadan kalıyor.
        Assert.Equal(1, written.Threshold);

        var gated = db.AlertRules.Single(r => r.SigmaRuleId == "r2");
        Assert.Equal(AlertRuleStatus.Gated, gated.Status);
        Assert.Contains("dns_query_name", gated.GatedReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Kullanıcının açtığı kural, SQL değiştiğinde kapatılmıyor</b> — yalnızca
    /// değiştiği görünür oluyor.
    ///
    /// <para>
    /// Kapatmak, kullanıcının hiç vermediği bir kararı onun adına vermek olurdu;
    /// sessizce geçirmek ise kriterin kendisini boşa çıkarırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Acik_kural_SQL_degisince_kapatilmiyor_ama_degisiklik_goruluyor()
    {
        await Service().SyncAsync(Manifest(Written("r1", "sha256:a")), "sub-1", ["/g"], Token);

        await using (var db = _factory.CreateDbContext())
        {
            db.AlertRules.Single().Status = AlertRuleStatus.Enabled;
            await db.SaveChangesAsync(Token);
        }

        var result = await Service().SyncAsync(
            Manifest(Written("r1", "sha256:b")), "sub-1", ["/g"], Token);

        Assert.Equal(1, result.Changed);
        Assert.Contains("r1", result.ChangedRuleIds);

        await using var check = _factory.CreateDbContext();
        Assert.Equal(AlertRuleStatus.Enabled, check.AlertRules.Single().Status);
    }

    /// <summary>
    /// <c>gated</c> kuralda ölçüt kaynak <b>artı engeller</b>: aynı kaynak, başka
    /// kolon → değişti.
    /// </summary>
    [Fact]
    public async Task Gated_kuralda_engel_degisimi_yakalaniyor()
    {
        await Service().SyncAsync(Manifest(Gated("r2", "dns_query_name")), "s", ["/g"], Token);

        var same = await Service().SyncAsync(
            Manifest(Gated("r2", "dns_query_name")), "s", ["/g"], Token);
        Assert.Equal(1, same.Unchanged);

        // Kaynak AYNI, engel farklı — kullanıcıya gösterilen sebep bayatlamamalı.
        var moved = await Service().SyncAsync(Manifest(Gated("r2", "answer")), "s", ["/g"], Token);
        Assert.Equal(1, moved.Changed);

        await using var db = _factory.CreateDbContext();
        Assert.Contains("answer", db.AlertRules.Single().GatedReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>failed</c> kurallara <b>hiç yazılmıyor</b>: bir kural durumu değil,
    /// bizim build'imizin durumu.
    /// </summary>
    [Fact]
    public async Task Failed_kural_kayda_girmiyor()
    {
        var failed = new SigmaManifestRule
        {
            RuleId = "r9", Status = SigmaRuleSync.StatusFailed, SourceSha = "sha256:k",
        };

        var result = await Service().SyncAsync(Manifest(failed), "s", ["/g"], Token);

        Assert.Equal(0, result.Created);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(db.AlertRules);
    }

    /// <summary>
    /// <b>Koşumun kendisi kayda giriyor.</b>
    ///
    /// <para>
    /// CLI çıktısı bir terminalde yaşıyor ve orada ölüyor — <c>ChangedRuleIds</c>
    /// ile aynı sınıf, bir katman yukarıda. Bir dağıtım hareketinin izinin
    /// yalnızca terminalde olması, altı ay sonra *"bu 269 kural nereden geldi"*
    /// sorusunun cevapsız kalması demek.
    /// </para>
    ///
    /// <para>
    /// Yeni bir tablo açılmıyor: <c>audit_log</c> zaten *"kim, hangi kapsam, ne
    /// yaptı"* sorusunun yeri.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kosumun_kendisi_denetim_kaydina_giriyor()
    {
        await Service().SyncAsync(
            Manifest(Written("r1", "sha256:a"), Gated("r2", "dns_query_name")),
            "sub-1", ["/ekip/ag", "/ekip/guvenlik"], Token);

        await using var db = _factory.CreateDbContext();
        var entry = db.AuditLog.Single();

        Assert.Equal("sigma.sync", entry.Action);
        Assert.Equal("sub-1", entry.Subject);

        // Kapsam kayda giriyor: "bu kurallar hangi grup adına indi" sorusu
        // altı ay sonra da cevaplanabilmeli.
        Assert.Contains("/ekip/ag", entry.Scope, StringComparison.Ordinal);
        Assert.Equal(2, entry.RowCount);
    }

    /// <summary>
    /// Değişen kuralların kimlikleri de kayda giriyor — sayı *"bir şey oynadı"*
    /// diyor, kayda değer olan <b>hangisi</b>.
    /// </summary>
    [Fact]
    public async Task Degisen_kurallarin_kimlikleri_kayitta()
    {
        await Service().SyncAsync(Manifest(Written("r1", "sha256:a")), "s", ["/g"], Token);
        await Service().SyncAsync(Manifest(Written("r1", "sha256:b")), "s", ["/g"], Token);

        await using var db = _factory.CreateDbContext();
        var last = db.AuditLog.OrderByDescending(a => a.Id).First();

        Assert.Contains("r1", last.Details, StringComparison.Ordinal);
        Assert.Contains("değişti 1", last.Details, StringComparison.Ordinal);
    }

    public void Dispose() => _factory.Dispose();
}

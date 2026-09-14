using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M14'ün iki aracı: <c>rca.trigger</c> (ürünün TEK yazması) ve
/// <c>rca.runs</c>.</b>
///
/// <para>
/// Ayrı bir dosya, çünkü ayrı bir soru: <c>McpReadToolTests</c> okuma
/// gövdelerinin kapsam/redaksiyon/kesilme davranışını ölçüyor; buradaki sorular
/// <b>yazma sözleşmesi</b> (anahtar nereden geliyor, kapsam genişletilebiliyor
/// mu) ve <b>T46 sadakati</b> (üç yönlü ayrım olduğu gibi mi taşınıyor).
/// </para>
/// </summary>
public sealed class McpRcaToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------
    // 1 · rca.trigger — anahtar SUNUCUDAN
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Şemada bir idempotency argümanı YOK</b> — ve olmaması kararın kendisi.
    ///
    /// <para>
    /// M05 gerekçeyi ölçtü: her denemede yeni anahtar üreten model kotayı
    /// defalarca yer, aynı anahtarı ısrarla üreten model farklı bir tetiklemeyi
    /// bastırır — ikisi de sessiz. Argümanı kabul eden bir şema o kapıyı
    /// açardı, ve <c>additionalProperties: false</c> ile birlikte bu iddia bir
    /// kapı: ilan edilmemiş bir alan yüke sızamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Rca_trigger_semasi_idempotency_argumani_kabul_etmiyor()
    {
        var tool = new RcaTriggerTool(Scopes(new InMemoryControlPlaneFactory()));

        var declared = tool.InputSchema.GetProperty("properties").EnumerateObject()
            .Select(static field => field.Name)
            .ToArray();

        Assert.DoesNotContain("idempotency_key", declared);
        Assert.DoesNotContain("idempotency", declared);
        Assert.DoesNotContain("key", declared);

        // Şema KAPALI: ilan edilmemiş bir alan sızamıyor.
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>
    /// <b>Yazılan anahtar <see cref="McpIdempotency.KeyFor"/>'un ürettiği
    /// anahtarın TA KENDİSİ.</b>
    ///
    /// <para>
    /// Şemada argüman olmaması yetmiyor: araç anahtarı <b>hiç</b> vermeyebilir
    /// (o zaman her çağrı yeni koşum) ya da kendi ürettiği başka bir dizgeyi
    /// verebilir. Bu test veritabanına yazılan satırı okuyup değeri
    /// karşılaştırıyor — yani anahtarın <b>o fonksiyondan</b> geldiğini ölçüyor.
    /// </para>
    ///
    /// <para>
    /// İkinci iddia idempotansın <b>çalıştığı</b>: aynı kapsam + aynı pencere
    /// ile ikinci çağrı yeni satır açmıyor ve <c>existing</c> ile bunu
    /// <b>söylüyor</b>. Söylemeseydi model aynı cevabı iki kez alıp iki koşum
    /// sanardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_trigger_anahtari_sunucudan_ureiyor_ve_idempotent()
    {
        using var factory = new InMemoryControlPlaneFactory();

        var tool = new RcaTriggerTool(Scopes(factory), new FakeTimeProvider(Now));

        var first = await tool.ExecuteScopedAsync(Invocation(), Core, Ct);

        Assert.False(first.IsError);

        var expected = McpIdempotency.KeyFor(
            Core.Subject, ["network/core"], Now - TimeSpan.FromHours(24), Now);

        await using (var db = factory.CreateDbContext())
        {
            var run = await db.RcaRuns.SingleAsync(Ct);

            Assert.Equal(expected, run.IdempotencyKey);

            // AKTÖR KAYDI — dört kalemden biri, ve `External` DEĞİL.
            Assert.Equal(RcaTriggerSource.Agent, run.Source);
            Assert.Equal(Core.Subject, run.RequestedBy);
        }

        // İKİNCİ ÇAĞRI: yeni satır YOK, ve `existing` bunu söylüyor.
        var second = await tool.ExecuteScopedAsync(Invocation(), Core, Ct);

        Assert.True(second.Payload.GetProperty("existing").GetBoolean());
        Assert.False(first.Payload.GetProperty("existing").GetBoolean());

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, await db.RcaRuns.CountAsync(Ct));
        }
    }

    /// <summary>
    /// <b>Kapsam dışı gruba yazılmıyor — ve hiç satır açılmıyor.</b>
    ///
    /// <para>
    /// Okuma araçlarında filtre sorguya iniyor; burada iş bir grup <b>adına</b>
    /// yapılıyor, dolayısıyla grubun kapsamda olduğu açıkça sorulmak zorunda.
    /// İkinci iddia asıl olan: <b>satır açılmıyor</b>. Açılsaydı reddedilmiş bir
    /// koşum başka grubun defterinde görünürdü ve <c>rca.runs</c> onu
    /// listelerdi.
    /// </para>
    ///
    /// <para>
    /// Cevap <c>not_found</c>, <c>forbidden</c> değil: okuma tarafındaki 404
    /// kararının aynısı — 403 <i>"böyle bir grup var"</i> bilgisini sızdırırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_trigger_kapsam_disi_gruba_yazmiyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        var result = await new RcaTriggerTool(Scopes(factory), new FakeTimeProvider(Now))
            .ExecuteScopedAsync(Invocation(("owner_group", "database/prod")), Core, Ct);

        Assert.True(result.IsError);
        Assert.Equal(McpToolError.NotFound, result.Error!.Code);

        await using var db = factory.CreateDbContext();

        Assert.Equal(0, await db.RcaRuns.CountAsync(Ct));
    }

    /// <summary>
    /// <b><c>rca.trigger</c> yazma tabanından türüyor ve <c>ReadOnlyHint</c>
    /// yalan söylemiyor.</b>
    ///
    /// <para>
    /// <c>IsReadOnly</c> `true` kalsaydı model aracı serbestçe deneyebileceği
    /// bir okuma sanardı — kota tüketen bir araç için en pahalı yanlış bilgi.
    /// Ve <c>DestructiveHint</c> onun tersinden geliyor, yani ikisi ayrı ayrı
    /// yanlış olamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Rca_trigger_yazma_olarak_ilan_ediliyor()
    {
        var tool = new RcaTriggerTool(Scopes(new InMemoryControlPlaneFactory()));

        Assert.IsAssignableFrom<ProductWriteTool>(tool);
        Assert.False(tool.IsReadOnly);

        var annotations = tool.ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.False(annotations!.ReadOnlyHint);
        Assert.True(annotations.DestructiveHint);
    }

    // ---------------------------------------------------------------------
    // 2 · rca.runs — T46'nın üç yönlü ayrımı
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Reddedilen satır yükte DURUYOR, ve kotayı yemediği görünüyor.</b>
    ///
    /// <para>
    /// Boş listenin <i>"hiç tetiklenmedi"</i> garantisi reddin bir <b>satır</b>
    /// olmayı sürdürmesine bağlı. Reddedilenleri gizleyen bir yük,
    /// <i>"kota reddetti"</i> ile <i>"hiç denenmedi"</i>yi tek cevaba indirir ve
    /// modelin çıkarımı <i>"burada RCA'ya gerek görülmedi"</i> olur.
    /// </para>
    ///
    /// <para>
    /// <c>reason</c> <b>dolu</b> olmalı: <see cref="RcaRunLifecycle.Describe"/>
    /// motorun bildirimdeki gerekçesiyle aynı kaynak, ve boş bir cümle
    /// <i>"neden RCA yok"</i> sorusunu cevapsız bırakırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_runs_reddedilen_satiri_gizlemiyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        await using (var db = factory.CreateDbContext())
        {
            db.RcaRuns.Add(Run(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded, quota: false));
            db.RcaRuns.Add(Run(RcaRunState.Empty, RcaRejectionReason.None, quota: true));

            await db.SaveChangesAsync(Ct);
        }

        var result = await new RcaRunsTool(factory).ExecuteScopedAsync(Invocation(), Core, Ct);

        var runs = result.Payload.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal(2, result.Payload.GetProperty("count").GetInt32());

        var rejected = runs.Single(r => r.GetProperty("state").GetString() == "rejected");

        // KOTAYI YEMİYOR — listedeki satır sayısı tüketim değil.
        Assert.False(rejected.GetProperty("counts_against_quota").GetBoolean());

        // CÜMLE MOTORUN KENDİ KAYNAĞINDAN — ikinci bir `Describe` yok.
        Assert.Equal(
            RcaRunLifecycle.Describe(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded),
            rejected.GetProperty("reason").GetString());

        // Ve `empty` ONDAN AYRI: bakıldı, bulunamadı, kotadan düşüldü.
        var empty = runs.Single(r => r.GetProperty("state").GetString() == "empty");

        Assert.True(empty.GetProperty("counts_against_quota").GetBoolean());
        Assert.NotEqual(
            rejected.GetProperty("reason").GetString(),
            empty.GetProperty("reason").GetString());
    }

    /// <summary>
    /// <b>Dördüncü cevap: boş liste.</b>
    ///
    /// <para>
    /// Ayrı bir test çünkü ayrı bir kayıp. Yukarıdaki üç durum satır olarak
    /// dönüyor; <i>"hiç tetiklenmedi"</i> ise <b>sıfır satır</b>. Aracın hata
    /// dönmemesi de iddianın parçası: <c>not_found</c> dönmek <i>"böyle bir
    /// tetikleyici yok"</i> derdi, oysa doğru cevap <i>"denenmemiş"</i>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_runs_hic_tetiklenmemis_icin_bos_liste()
    {
        using var factory = new InMemoryControlPlaneFactory();

        var result = await new RcaRunsTool(factory).ExecuteScopedAsync(Invocation(), Core, Ct);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Payload.GetProperty("count").GetInt32());
        Assert.Empty(result.Payload.GetProperty("runs").EnumerateArray());
    }

    /// <summary>
    /// <b>Başka grubun koşumu dönmüyor</b> — ve filtre <c>Take</c>'ten önce.
    ///
    /// <para>
    /// Bellekte süzülseydi en yeni satırların hepsi kapsam dışı olduğunda cevap
    /// boş dönerdi ve boş listenin garantisi (<i>"hiç tetiklenmedi"</i>)
    /// <b>sessizce yalan</b> olurdu. Bu testte kapsam dışı satır daha yeni.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rca_runs_baska_grubun_kosumunu_dondurmuyor()
    {
        using var factory = new InMemoryControlPlaneFactory();

        await using (var db = factory.CreateDbContext())
        {
            db.RcaRuns.Add(Run(RcaRunState.Complete, RcaRejectionReason.None, quota: true));

            var foreign = Run(RcaRunState.Complete, RcaRejectionReason.None, quota: true);
            foreign.OwnerGroup = "database/prod";

            // DAHA YENİ: bellekte süzen bir uygulama bunu alıp sonra eleyerek
            // boş liste döndürürdü.
            foreign.RequestedAt = Now.AddMinutes(5);
            db.RcaRuns.Add(foreign);

            await db.SaveChangesAsync(Ct);
        }

        var result = await new RcaRunsTool(factory)
            .ExecuteScopedAsync(InvocationOf(("limit", 1)), Core, Ct);

        var groups = result.Payload.GetProperty("runs").EnumerateArray()
            .Select(static r => r.GetProperty("owner_group").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["network/core"], groups);
    }

    // ---------------------------------------------------------------------

    private static RcaRunEntity Run(RcaRunState state, RcaRejectionReason rejection, bool quota) => new()
    {
        OwnerGroup = "network/core",
        Source = RcaTriggerSource.Agent,
        TriggerIdentity = "mcp:analyst.core",
        State = state,
        Rejection = rejection,
        CountsAgainstQuota = quota,
        RequestedAt = Now,
        WindowFrom = Now.AddHours(-1),
        WindowTo = Now,
    };

    /// <summary>
    /// <c>RcaAdmission</c> GERÇEK — sahte bir kabul yazılmadı.
    ///
    /// <para>
    /// Ölçülmek istenen şey anahtarın <b>o fonksiyondan</b> gelip veritabanına
    /// yazılması; sahte bir kabul o zinciri taklit ederdi, yani testin kendi
    /// iddiasını doğrulaması olurdu (§6). Kota kapısı
    /// <c>AlwaysAllowQuotaGate</c>: kotanın kendisi T46'nın testlerinin konusu ve
    /// burada ölçülen şey değil.
    /// </para>
    /// </summary>
    private static IServiceScopeFactory Scopes(IDbContextFactory<ControlPlaneDbContext> factory) =>
        new ServiceCollection()
            .AddSingleton(factory)
            .AddSingleton<IRcaQuotaGate>(new AlwaysAllowQuotaGate())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<RcaAdmission>>(
                NullLogger<RcaAdmission>.Instance)
            .AddScoped<RcaAdmission>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static McpToolInvocation Invocation(params (string Name, string Value)[] arguments)
    {
        var map = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["owner_group"] = System.Text.Json.JsonSerializer.SerializeToElement("network/core"),
        };

        foreach (var (name, value) in arguments)
        {
            map[name] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        }

        // Çağrı nesnesinin kapsamı bilerek `Denied`: gövde kapsamı PARAMETRE
        // olarak alıyor ve `invocation.Scope`'u okuyan bir gövde bu değerle
        // hiçbir iddiayı sağlayamıyor. Gerekçe `McpReadToolFixtures`'ta.
        return new McpToolInvocation(map, AccessScope.Denied);
    }

    private static McpToolInvocation InvocationOf(params (string Name, object? Value)[] arguments)
    {
        var map = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            map[name] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        }

        return new McpToolInvocation(map, AccessScope.Denied);
    }
}

using System.Globalization;
using System.Net;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Bitti tanımı §3'ün KANITI: <c>notifications/cancelled</c> uzun bir
/// ClickHouse sorgusunu gerçekten iptal ediyor.</b>
///
/// <para>
/// <b>Mekanizma M01'in, kanıt M04'ün</b> — ve ayrım bu dosyanın var olma sebebi.
/// M01 <c>NeverEndingTool</c> ile <b>belirtecin uca ulaştığını</b> ölçtü:
/// konteynersiz, hızlı, ve doğru. Ama o ölçüm bir şeyi <b>ölçmüyor</b>: belirteç
/// aracın gövdesine ulaşsa bile <b>ClickHouse tarafındaki sorgunun durduğunu</b>
/// söylemiyor. İptal edilen bir <c>Task</c> ile iptal edilen bir <i>sorgu</i>
/// aynı şey değil; ilki bekleyeni serbest bırakıyor, ikincisi sunucudaki işi
/// durduruyor.
/// </para>
///
/// <para>
/// Aradaki fark bir kaynak sızıntısı: istemci <i>"iptal ettim"</i> sanarken
/// ClickHouse taramaya devam ederse, iptali tekrar tekrar deneyen bir ajan
/// sunucuyu <b>biriken</b> sorgularla doldurur. Hata yok, sayaç yok, belirti
/// yok — §7'nin sınıfı.
/// </para>
///
/// <para>
/// <b>KOŞTURULMADI (CLAUDE.md §2).</b> Bu test Testcontainers istiyor ve
/// ajanlar Docker'a dokunmuyor. Aşağıda <b>koşturulduğunda ne kanıtlayacağı</b>
/// test başına yazılı.
/// </para>
///
/// <para>
/// <b>Ölçüm duvar saatine bağlı DEĞİL</b> ve bu bilinçli (§6: bir testin geçme
/// sebebinin duvar saatiyle ilgisi olmamalı). Eşik bir süre değil bir
/// <b>karşılaştırma</b>: aynı sorgu iptal edilmediğinde <c>system.processes</c>'te
/// <i>duruyor</i>, iptal edildiğinde <i>düşüyor</i>. Kontrol kolu olmadan
/// "sorgu listede yok" sonucu iki şey anlatabilirdi — <i>iptal çalıştı</i> ya da
/// <i>sorgu zaten bitmişti</i> — ve ikincisi varsayılırdı.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class McpQueryCancellationTests(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>
    /// Sorgunun <b>ölçülebilir süre</b> alması için yeterli satır. Sayı bir
    /// performans hedefi değil: iptal penceresinin var olmasını sağlıyor. Çok
    /// küçük olursa sorgu iptal edilmeden biter ve test <b>iptali değil hızı</b>
    /// ölçer — yeşil, ve anlamsız.
    /// </summary>
    private const int TotalEvents = 400_000;

    /// <summary>
    /// <c>system.processes</c> yoklamasının üst sınırı. Bir performans bütçesi
    /// değil, <b>askıda kalmama</b> sınırı: aşılırsa test kırmızı yanıyor ve
    /// mesajı "iptal görülmedi" diyor.
    /// </summary>
    private static readonly TimeSpan CancellationDeadline = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClickHouseContext context = null!;

    public async ValueTask InitializeAsync()
    {
        context = await stack.CreateIsolatedClickHouseContextAsync(Ct);

        await new ClickHouseMigrator(context).MigrateAsync(
            DevStackSetup.RepoPath("db/clickhouse"), Ct);

        var writer = new EventWriter(context);

        foreach (var chunk in Enumerable.Range(0, TotalEvents).Select(Sample).Chunk(20_000))
        {
            await writer.WriteEventsAsync(chunk, Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        context.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <b>KOŞTURULDUĞUNDA NE KANITLAR:</b> <c>logs.search</c> ClickHouse'a inen
    /// uzun bir sorgu başlatıyor, istemci <c>notifications/cancelled</c>
    /// gönderiyor, ve <b>ClickHouse tarafındaki sorgu düşüyor</b> —
    /// <c>system.processes</c> onu artık göstermiyor.
    ///
    /// <para>
    /// <b>Kontrol kolu aynı testin içinde.</b> Önce iptal <b>edilmeyen</b> bir
    /// çağrının aynı anda <c>system.processes</c>'te <b>görüldüğü</b> ölçülüyor.
    /// O adım olmadan "listede yok" sonucu, sorgunun zaten bitmiş olmasıyla
    /// karışırdı ve testin yeşili hiçbir şey ifade etmezdi.
    /// </para>
    ///
    /// <para>
    /// <b>Kırmızı yanarsa ilk bakılacak yer sunucu ayarı, kod değil:</b>
    /// ClickHouse istemci bağlantısı düştüğünde sorguyu ancak
    /// <c>cancel_http_readonly_queries_on_client_close</c> açıkken iptal ediyor.
    /// Kapalıysa sürücü isteği iptal eder, sunucu taramayı <b>sürdürür</b>, ve
    /// bu testin kırmızısı doğru cevabı vermiş olur: <i>iptal uca kadar
    /// gitmiyor.</i> O hâlde düzeltme bu dosyada değil <c>db/clickhouse</c>
    /// ayarlarında ya da bağlantı dizesinde.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Iptal_bildirimi_ClickHouse_sorgusunu_gercekten_durduruyor()
    {
        await using var services = await ServicesAsync();
        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            McpBoundaryDeclaration.Declare(DataBoundary.Internal, "iptal ölçümü: entegrasyon testi"),
            [typeof(LogsSearchTool).Assembly],
            services);

        // ---- KONTROL KOLU -------------------------------------------------
        //
        // İptal edilmeyen bir çağrı ClickHouse'ta GÖRÜLÜYOR. Bu adım testin
        // ölçüm gücünü kuruyor: aşağıdaki "listede yok" iddiası ancak "iptal
        // edilmeden VAR" ölçüldükten sonra bir şey söylüyor.
        await using (var control = await McpIntegrationSession.StartAsync(options, services, Ct))
        {
            using var abandoned = new CancellationTokenSource();

            var running = control.Client.CallToolAsync(
                LogsSearchTool.ToolIdentifier, WideSearch(), cancellationToken: abandoned.Token);

            Assert.True(
                await WaitForQueryAsync(present: true, Ct),
                "Kontrol kolu düştü: iptal EDİLMEYEN bir `logs.search` çağrısı ClickHouse'un "
                + "`system.processes`'inde hiç görünmedi. Bu, iptal hakkında bir şey söylemiyor — "
                + $"sorgu {TotalEvents.ToString(CultureInfo.InvariantCulture)} satırı iptal "
                + "penceresinden hızlı taramış olabilir. Satır sayısını artırın ya da sorguyu "
                + "ağırlaştırın; aksi hâlde aşağıdaki iddia BOŞ ölçüm yapar.");

            await abandoned.CancelAsync();

            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                // Beklenen.
            }
        }

        // ---- ÖLÇÜM --------------------------------------------------------
        await using var session = await McpIntegrationSession.StartAsync(options, services, Ct);

        using var caller = new CancellationTokenSource();

        var call = session.Client.CallToolAsync(
            LogsSearchTool.ToolIdentifier, WideSearch(), cancellationToken: caller.Token);

        // Sorgu GERÇEKTEN başladı: iptali başlamamış bir sorguya göndermek
        // "iptal çalıştı" yerine "hiç koşmadı" ölçerdi.
        Assert.True(
            await WaitForQueryAsync(present: true, Ct),
            "Sorgu ClickHouse'ta hiç görünmedi; iptal edilecek bir şey yoktu.");

        // SDK bunu `notifications/cancelled` olarak tele koyuyor.
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        Assert.True(
            await WaitForQueryAsync(present: false, Ct),
            "`notifications/cancelled` gönderildi, istemci iptal gördü, ama ClickHouse sorguyu "
            + $"{CancellationDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} saniye "
            + "boyunca `system.processes`'te taşımaya DEVAM etti.\n\n"
            + "Bu bir yavaşlık değil bir SIZINTI: istemci \"iptal ettim\" sanarken sunucu "
            + "taramayı sürdürüyor, ve iptali tekrar deneyen bir ajan sorguları BİRİKTİRİYOR.\n\n"
            + "İlk bakılacak yer: `cancel_http_readonly_queries_on_client_close`. Sürücü isteği "
            + "iptal ediyor ama ClickHouse bağlantı düşünce sorguyu ancak o ayar açıkken "
            + "durduruyor.");
    }

    /// <summary>
    /// <b>KOŞTURULDUĞUNDA NE KANITLAR:</b> iptal, sorgunun <b>kısmi sonucunu</b>
    /// modele göndermiyor.
    ///
    /// <para>
    /// Ayrı bir test, çünkü ayrı bir hata: sorgu durdurulmuş olsa bile araç o
    /// ana kadar okuduğu satırları döndürebilirdi ve model onu <b>tam bir
    /// sonuç</b> sanardı — sayfalama alanları dolu, <c>has_more</c> anlamsız.
    /// Yarım bir cevap, cevapsızlıktan tehlikelidir (§7).
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Iptal_edilen_cagri_kismi_sonuc_dondurmuyor()
    {
        await using var services = await ServicesAsync();
        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            McpBoundaryDeclaration.Declare(DataBoundary.Internal, "iptal ölçümü: entegrasyon testi"),
            [typeof(LogsSearchTool).Assembly],
            services);

        await using var session = await McpIntegrationSession.StartAsync(options, services, Ct);

        using var caller = new CancellationTokenSource();

        var call = session.Client.CallToolAsync(
            LogsSearchTool.ToolIdentifier, WideSearch(), cancellationToken: caller.Token);

        Assert.True(await WaitForQueryAsync(present: true, Ct), "Sorgu hiç başlamadı.");

        await caller.CancelAsync();

        // Yalnızca istisna bekleniyor: DÖNEN bir sonuç, kısmi bir sonuç olurdu.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Bütün satırları tarayan bir arama. <c>full_text</c> bilerek eşleşmesi
    /// düşük bir terim: az satır dönmesi, çok satır <b>okunmasını</b>
    /// engellemiyor — iptal penceresi tam olarak o okumada.
    /// </summary>
    private static Dictionary<string, object?> WideSearch() => new(StringComparer.Ordinal)
    {
        ["from"] = Start.AddDays(-1).ToString("O", CultureInfo.InvariantCulture),
        ["to"] = Start.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
        ["full_text"] = "bu-terim-hicbir-satirda-yok",
        ["limit"] = 200,
    };

    /// <summary>
    /// <c>logs.search</c>'ün sorgusu <c>system.processes</c>'te var mı — yokluğu
    /// ya da varlığı beklenene ulaşana kadar yokluyor.
    ///
    /// <para>
    /// <b>Yoklama, sabit bir bekleme değil.</b> Sabit bir <c>Delay</c> makine
    /// yüküne göre ya erken ya geç bakardı ve testin geçme sebebi duvar saatine
    /// bağlanırdı (§6, bu depoda iki kez ısırdı).
    /// </para>
    /// </summary>
    private async Task<bool> WaitForQueryAsync(bool present, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CancellationDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await QueryIsRunningAsync(cancellationToken) == present)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// <c>database = currentDatabase()</c> şart: her test sınıfı kendi izole
    /// veritabanını açıyor ve filtresiz bir <c>system.processes</c> sorgusu
    /// <b>başka testlerin</b> sorgularını sayar — paralel koşumda sessizce
    /// yanlış cevap.
    /// </summary>
    private async Task<bool> QueryIsRunningAsync(CancellationToken cancellationToken)
    {
        var running = await stack.QueryScalarAsync(
            context.Options.ConnectionString,
            "SELECT count() FROM system.processes "
            + "WHERE current_database = currentDatabase() "
            + "AND query LIKE '%bu-terim-hicbir-satirda-yok%' "

            // Yoklamanın kendisi de `system.processes`'te duruyor; kendini
            // saymak "sorgu hâlâ koşuyor" diye okunurdu.
            + "AND query NOT LIKE '%system.processes%'",
            cancellationToken);

        return long.Parse(running, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Üretimin kendi servis grafiği <b>değil</b>, ama üretimin kendi
    /// <c>ScopedQuery</c>'si: iptalin ölçülmek istendiği yer o sınıfın
    /// ClickHouse çağrısı.
    /// </summary>
    private async Task<ServiceProvider> ServicesAsync()
    {
        var controlPlane = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var services = new ServiceCollection();

        // Kayıt ÜRETİMDEKİ ÖMÜRLE aynı: `IScopedQuery` scoped. Singleton
        // yazmak, araçların çağrı başına kapsam açtığını ölçülmez kılardı ve
        // esir bağımlılık burada da görünmezdi.
        services.AddScoped<IScopedQuery>(_ => new ScopedQuery(
            new EventReader(context),
            new ChangeEventReader(context),
            new CorrelationReader(context),
            new EventWriter(context),
            controlPlane.CreateDbContext(),
            new NoOpAuditSink()));

        services.AddSingleton<IAccessScopeResolver>(
            new IntegrationScopeResolver(AccessScope.ForGroups("iptal-olcumu", ["net-core"])));

        return services.BuildServiceProvider();
    }

    private static LogEvent Sample(int index) => new()
    {
        EventId = Guid.NewGuid(),
        Timestamp = Start.AddSeconds(index),
        OwnerGroup = "net-core",
        SourceId = index % 2 == 0 ? "fw-core-01" : "fw-core-02",
        SrcIp = IPAddress.IPv6Any,
        DstIp = IPAddress.IPv6Any,
        Body = string.Create(CultureInfo.InvariantCulture, $"satır {index}"),
    };
}

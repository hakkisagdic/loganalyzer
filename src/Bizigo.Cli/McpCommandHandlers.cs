using System.Reflection;
using Bizigo.Alerting;
using Bizigo.Commands.Mcp;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Mcp;
using Bizigo.Rca;
using Bizigo.Mcp.Product;
using Bizigo.Parsing;
using Bizigo.Query;
using Bizigo.Simulators.Mcp;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo mcp serve</c> — MCP'nin <b>stdio</b> taşıması.
///
/// <para>
/// <b>Neden ayrı bir çalıştırılabilir değil.</b> stdio sunucusu için ikinci bir
/// host projesi açmak, M02'nin (<i>komut çekirdeği ve CLI paritesi</i>) tam
/// olarak kaçınmak için var olduğu ikinci kopyayı doğururdu: aynı komutların
/// bir kez CLI'da bir kez MCP host'unda kurulması. M01 yalnızca <b>girişi</b>
/// açıyor; komut çekirdeği M02'nin işi ve o geldiğinde bu dosyanın çözdüğü
/// servis grafiği ortak çekirdeği de taşıyacak.
/// </para>
///
/// <para>
/// <b>stdout protokolün kendisi.</b> Bu komut hiçbir şey yazdırmıyor — tek bir
/// bilgi satırı bile JSON-RPC akışını bozar ve istemci bunu ayrıştırma hatası
/// olarak görür, yani ürün hakkında hiçbir şey söylemeyen bir arıza. Günlükler
/// <c>--verbose</c> ile <b>stderr</b>'e açılıyor.
/// </para>
/// </summary>
public static class McpCommandHandlers
{
    /// <summary>
    /// stdio yüzeyinin araç derlemeleri — <b>CLI'nın kendi beyanı.</b>
    ///
    /// <para>
    /// <c>Bizigo.Api</c>'ninkinden ayrı olması bilinçli: iki host farklı araç
    /// kümeleri sunabilir (stdio'da simülatör yüzeyi de açık, HTTP'de değil).
    /// Tek bir liste paylaşmak, o ayrımı sessizce yok ederdi.
    /// </para>
    ///
    /// <para>
    /// <b>Beyan YÜZEY BAŞINA</b>, ve bu M05'in getirdiği modelin asıl kazancı.
    /// Kök tarama modelinde <c>bizigo-sim</c> yüzeyi CLI derlemesinden
    /// bakıyordu ve oradan ürün araçlarına da ulaşılıyordu; keşif hepsini
    /// <b>kuruyor</b>, yüzeye göre ancak kurduktan sonra eliyor. Sonucu iki
    /// yönlü: simülatörden başka bir şey sunmayan bir süreç ClickHouse ve
    /// alarm servislerini çözmek zorunda kalıyordu, ve bir ürün aracının
    /// simülatör yüzeyine sızmaması <b>bir çalışma zamanı süzgecine</b>
    /// bağlıydı. Beyan yüzeye bağlanınca ikisi de yapısal olarak kapanıyor:
    /// ürün aracı simülatör yüzeyinde <b>hiç keşfedilmiyor</b> (K6).
    /// </para>
    ///
    /// <para>
    /// <c>Bizigo.Mcp</c> hiçbir listede <b>yok</b> — çekirdek örtük olarak
    /// ekleniyor. Gerekçe ve bekçi <c>McpEndpoints.ToolAssemblies</c>
    /// belgesinde.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Assembly> ToolAssembliesFor(McpSurface surface) => surface switch
    {
        McpSurface.Product =>
        [
            typeof(Bizigo.Mcp.Product.Tools.LogsSearchTool).Assembly,
            typeof(Bizigo.Commands.Mcp.CommandTool).Assembly,
        ],
        McpSurface.Simulator => [typeof(Bizigo.Simulators.Mcp.SimulatorTool).Assembly],
        _ => throw new ArgumentOutOfRangeException(
            nameof(surface),
            surface,
            "Beyan edilmemiş bir yüzeyin araç derlemesi yok. Yeni bir yüzey eklendiğinde "
            + "burası kırmızı yanıyor — ve yanması gerekiyor: beyansız bir yüzey, araçları "
            + "hiç bulunmayan ve bu yüzden SESSİZCE yeşil kalan bir kapı demek."),
    };

    /// <summary>
    /// stdio'nun sunduğu <b>bütün</b> araç derlemeleri — yalnızca
    /// <c>McpToolAssemblyTests</c> için.
    ///
    /// <para>
    /// Bekçinin sorusu <i>"araç taşıyan her derleme bir yerde ilan edilmiş
    /// mi"</i>, yani birleşim. Koşan sunucu bunu <b>hiç</b> kullanmıyor:
    /// kullanırsa yukarıdaki yüzey ayrımı geri alınmış olur.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Assembly> ToolAssemblies { get; } =
    [
        .. ToolAssembliesFor(McpSurface.Product),
        .. ToolAssembliesFor(McpSurface.Simulator),
    ];

    /// <summary>
    /// Sunucuyu koşturur. Süreç kapanana kadar dönmez.
    /// </summary>
    /// <param name="surfaceName">
    /// <c>bizigo</c> ya da <c>bizigo-sim</c>. Tanınmayan ad <b>reddediliyor</b>:
    /// bir varsayılana düşmek, yüzeyini yanlış yazan çağrıyı sessizce ürün
    /// verisi kümesine bağlardı.
    /// </param>
    /// <param name="boundaryName">
    /// <c>internal</c> ya da <c>external</c> — yüzeyin <b>K6 beyanı</b> (M06).
    /// <b>Varsayılanı yok</b> ve olmamalı; gerekçe aşağıda.
    /// </param>
    /// <param name="verbose">Günlükleri stderr'e ver.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task<int> ServeAsync(
        string surfaceName,
        string? boundaryName,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (McpSurfaces.Parse(surfaceName) is not { } surface)
        {
            await Console.Error.WriteLineAsync(
                $"Bilinmeyen MCP yüzeyi: '{surfaceName}'. "
                + $"Beklenen: '{McpSurfaces.ProductName}' ya da '{McpSurfaces.SimulatorName}'.")
                .ConfigureAwait(false);

            return 2;
        }

        // K6 beyanı. `--surface`'in aksine VARSAYILANI YOK — ve bu, ikisinin
        // farklı sorular sorduğunun kaydı: yüzeyin makul bir varsayılanı var
        // (ürün), sınırın YOK. Sınırı varsayan her değer, beyan etmeyi unutan
        // operatörün yerine karar vermiş olurdu.
        //
        // Buradan "stdio, demek ki iç ağ" diye geçilmedi: gerekçe
        // `McpStdioHost.RunAsync`'in `boundary` parametresinde yazılı ve özeti
        // şu — bu taşımanın en olası istemcisi log metnini buluta gönderiyor.
        if (!Enum.TryParse<DataBoundary>(boundaryName, ignoreCase: true, out var declared)
            || declared == DataBoundary.Unspecified)
        {
            await Console.Error.WriteLineAsync(
                $"MCP ağ sınırı beyan edilmedi ya da çözümlenemedi: '{boundaryName}'. "
                + "`--data-boundary internal` ya da `--data-boundary external` yazın. "
                + "Beyansız bir yüzey 'iç ağ' SAYILMIYOR: K6 (`log verisi kurum dışına "
                + "çıkmaz`) bir varsayılan kabul etmiyor.")
                .ConfigureAwait(false);

            return 2;
        }

        McpBoundaryDeclaration boundary;

        try
        {
            boundary = McpBoundaryDeclaration.Declare(declared, "CLI seçeneği: `--data-boundary`");
            McpBoundaryGate.Require(boundary, surface);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            // Kapının reddi burada YAKALANIYOR çünkü bu bir operatör hatası,
            // bir program kusuru değil: yığın izi basmak yerine ne yazması
            // gerektiğini söyleyip çıkış kodu veriyoruz. stdout'a tek satır
            // gitmiyor — orası protokolün.
            await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);

            return 2;
        }

        using var loggerFactory = verbose
            ? LoggerFactory.Create(logging => logging
                .SetMinimumLevel(LogLevel.Debug)

                // stderr — stdout protokolün. Konsol sağlayıcısının varsayılanı
                // stdout ve orada bırakmak akışı ilk günlük satırında bozardı.
                .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace))
            : LoggerFactory.Create(static logging => logging.ClearProviders());

        // ── M12 · AYARLAR, ve ret DERLEME ANINDA DEĞİL KALKIŞTA ─────────────
        //
        // Okuma yolu CLI'ın kendi yolu: aynı iki ortam değişkeni `schema
        // migrate`, `seed golden`, `fleet apply` ve `sigma sync` tarafından
        // zaten okunuyor (§9 — ikinci bir yol yazılmadı).
        //
        // Ret SUNUCU KURULMADAN önce: eksik ayar bir DI hatası olarak
        // patladığında (bugüne kadarki hâl) okuyan kişi `AlertRuleService`'in
        // kaydına bakıyor, oysa eksik olan bir ayar. Kalıp M06'nın
        // "yarım başlamıyor" kararının aynısı.
        var clickHouse = Environment.GetEnvironmentVariable(ClickHouseVariable);
        var controlPlane = Environment.GetEnvironmentVariable(ControlPlaneVariable);

        if (surface is McpSurface.Product
            && MissingProductSetting(clickHouse, controlPlane) is { } missing)
        {
            // stdout'a tek satır gitmiyor — orası protokolün.
            await Console.Error.WriteLineAsync(missing).ConfigureAwait(false);

            return 2;
        }

        // ── M13 · KİMLİK: ortamdan belirteç, doğrulanmış ─────────────────────
        //
        // SIRA ÖNEMLİ: kimlik servis grafiğinden ÖNCE doğrulanıyor, çünkü
        // `IMcpIdentityRefusal` grafiğe kaydediliyor ve `McpCallerScope` onu
        // oradan okuyor. Sonradan kaydetmek, kabı kurulduktan sonra
        // değiştirmeye çalışmak olurdu.
        //
        // Ayarlarda belirteç yoksa `identity` null kalıyor ve yüzey M13
        // öncesindeki hâlde koşuyor: kimlik isteyen araçlar `unauthenticated`
        // dönüyor. Sessiz bir varsayılana DÜŞÜLMÜYOR.
        McpStdioIdentity? identity = null;

        if (surface is McpSurface.Product)
        {
            var settings = McpStdioIdentitySettings.FromEnvironment();

            (identity, var failure) = await McpStdioIdentity
                .ValidateAsync(settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                // BELİRTEÇ VARDI VE GEÇERSİZDİ → yüzey BAŞLAMIYOR.
                //
                // Alternatif kimliksiz başlamaktı ve elendi: operatör bir
                // belirteç verdiğinde onu kullanmamızı bekliyor, ve sessizce
                // kimliksiz koşan bir yüzeyin belirtisi "araçlar bir şey
                // döndürmüyor" olurdu — sebebi hiçbir yerde durmayan bir
                // belirti (§7).
                await Console.Error.WriteLineAsync(failure).ConfigureAwait(false);

                return 2;
            }
        }

        await using var services = BuildServices(surface, clickHouse, controlPlane, identity);

        if (identity is not null)
        {
            // KAPSAM EŞLEMESİ YÜKLENİYOR — ve bu satır M12'nin bıraktığı bir
            // boşluğu kapatıyor.
            //
            // `AccessScopeResolver` eşleme tablosunu BELLEĞE ALIYOR ve
            // `RefreshAsync` çağrılmazsa `GroupMapping.Empty` kalıyor: her kimlik
            // BOŞ kapsama çözülür ve kapsamlı her araç `not_found` döner.
            // `Bizigo.Api` bunu açılışta çağırıyor (`Program.cs`), stdio
            // çağırmıyordu.
            //
            // M12'de GÖRÜNMÜYORDU çünkü hiç kimlik gelmiyordu — yani boşluk
            // ancak kimlik yolu açıldığında ısırabilirdi. Kimliğin geldiği ilk
            // tur bu.
            await services.GetRequiredService<AccessScopeResolver>()
                .RefreshAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await McpStdioHost.RunAsync(
            surface,
            boundary,
            ToolAssembliesFor(surface),
            services,
            loggerFactory,
            identity is null ? null : identity.Principal,
            cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Ürün yüzeyinin ihtiyaç duyduğu ayarların adları — ve bunlar CLI'ın
    /// <b>zaten kullandığı</b> değişkenler, ikinci bir okuma yolu değil (§9):
    /// <c>schema migrate</c> ile <c>seed golden</c>
    /// <see cref="ClickHouseVariable"/>'ı, <c>fleet apply</c> ile
    /// <c>sigma sync</c> <see cref="ControlPlaneVariable"/>'ı okuyor.
    /// </summary>
    public const string ClickHouseVariable = "BIZIGO_CLICKHOUSE";

    /// <inheritdoc cref="ClickHouseVariable"/>
    public const string ControlPlaneVariable = "BIZIGO_CONTROLPLANE";

    /// <summary>
    /// Ürün yüzeyi için eksik ayar var mı — varsa <b>adıyla</b> söyleyen mesaj,
    /// yoksa <see langword="null"/>.
    ///
    /// <h3>Neden localhost'a DÜŞMÜYOR — CLI'ın diğer komutlarından bilinçli bir
    /// ayrılık</h3>
    ///
    /// <para>
    /// CLI'da iki politika bir arada duruyor: <c>schema migrate</c> ile
    /// <c>fleet apply</c> ayar yoksa <c>localhost</c>'a düşüyor;
    /// <c>sigma sync</c> ise <b>reddediyor</b> ve eksik değişkenin adını
    /// yazıyor. Burada ikincisi seçildi, iki ölçütle:
    /// </para>
    ///
    /// <list type="number">
    /// <item>
    /// <b>Bu komutu bir insan değil BAŞKA BİR PROGRAM başlatıyor.</b> stdio'nun
    /// gerçek istemcisi bir masaüstü MCP istemcisi ve <c>stdout</c> protokolün
    /// kendisi. Yanlış ama makul bir varsayılan burada terminalde görülen bir
    /// hata üretmiyor; <b>çağrı anında</b>, <b>modelin bağlamında</b>, aracın
    /// suçu gibi görünen bir arıza üretiyor.
    /// </item>
    /// <item>
    /// <b>Ve tahmin TUTABİLİR.</b> Asıl tehlike bağlanamamak değil: bir
    /// geliştiricinin makinesinde <c>localhost</c>'ta gerçekten bir ClickHouse
    /// olabilir, ve o zaman yüzey <b>başka bir kurulumun log verisini</b> modele
    /// okur. K6'nın alanında sessizce yanlış çalışan bir varsayılan, hiç
    /// çalışmayan bir varsayılandan pahalıdır.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <c>schema migrate</c> için aynı ölçütler geçerli değil: onu bir insan
    /// depo kökünden koşturuyor, çıktısını görüyor ve yanlış tahmin gürültülü
    /// biçimde düşüyor. Politika farkı komutların farkından geliyor, bir
    /// tutarsızlıktan değil.
    /// </para>
    ///
    /// <para>
    /// <b>Mesaj DI'dan şikâyet ETMİYOR</b>, ve bu madde bir bulgudan doğdu: bu
    /// yüzeyin bugüne kadarki arızası
    /// <c>Unable to resolve service for type 'AlertRuleService'</c> diyordu —
    /// eksik olan bir <i>ayar</i>ken okuyanı <b>DI kaydına</b> gönderiyordu.
    /// Hata mesajının yanlış yüzeyi işaret etmesi S04'ün düzelttiği sınıf;
    /// bekçisi <c>McpStdioSurfaceTests.Eksik_ayar_adiyla_reddediliyor</c>.
    /// </para>
    /// </summary>
    public static string? MissingProductSetting(string? clickHouse, string? controlPlane)
    {
        var missing = new List<string>(2);

        if (string.IsNullOrWhiteSpace(clickHouse))
        {
            missing.Add(ClickHouseVariable);
        }

        if (string.IsNullOrWhiteSpace(controlPlane))
        {
            missing.Add(ControlPlaneVariable);
        }

        if (missing.Count == 0)
        {
            return null;
        }

        return $"`{McpSurfaces.ProductName}` yüzeyi için bağlantı ayarı eksik: "
            + string.Join(", ", missing.Select(static name => $"`{name}`"))
            + ". Bu yüzey ürünün kendi verisini okuyor ve adres TAHMİN EDİLMİYOR: yanlış ama "
            + "ulaşılabilir bir adres, başka bir kurulumun log verisini modele okumak olurdu (K6). "
            + "Ortam değişkenlerini kurun — MCP istemci yapılandırmasında `env` bloğu — ya da "
            + $"yalnızca simülatörü sunmak için `--surface {McpSurfaces.SimulatorName}` verin.";
    }

    /// <summary>
    /// Yüzeyin araçlarının ihtiyaç duyduğu servisler.
    ///
    /// <para>
    /// <b>Bu metot aynı zamanda bir DERLEME BAĞI, ve bu ikinci işi kasıtlı</b>
    /// (M03/M04). Derleyici, kodunda hiçbir tipine dokunulmayan bir
    /// <c>ProjectReference</c>'ı meta veriden <b>buduyor</b>; budanan derlemeyi
    /// <c>McpToolDiscovery.ProductAssemblies</c> referans tablosunda
    /// <b>göremiyor</b> ve araçları <b>sessizce</b> ilan edilmiyor — hata yok,
    /// sayaç yok, uyum kapısı yeşil.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçüldü, ve tuzağın canlı örneği bu depoda duruyor:</b>
    /// <c>bizigo.dll</c>'in derleme referansları arasında
    /// <c>Bizigo.Simulators</c> <b>var</b> ama <c>Bizigo.Query</c> <b>yok</b> —
    /// ikisinin de <c>ProjectReference</c>'ı olmasına rağmen. Simülatör bugün
    /// ayakta çünkü <c>FleetCommandHandlers</c> <c>bizigo fleet apply</c> için
    /// <c>FleetStore</c>'a dokunuyor. <b>Yani bugün çalışması bir güvence değil
    /// tesadüf:</b> o komut kaldırılırsa ya da başka bir derlemeye taşınırsa
    /// yedi <c>sim.*</c> aracı ilan edilmez.
    /// </para>
    ///
    /// <para>
    /// Buradaki çağrı bağı tesadüften çıkarıyor: araçların ilan edilmesi
    /// artık <b>araçların kaydına</b> bağlı, filo komutunun varlığına değil.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>GEÇİCİ.</b> M05 <c>AddBizigoMcpCore</c>'a araç derlemelerini
    /// <b>açıkça</b> aldıracak; o gün doğru cevap bir çağrı yan etkisi değil
    /// <c>SimulatorMcpSetup.ToolAssembly</c>'nin o listeye verilmesi olacak ve
    /// bu paragraf silinecek.
    /// </para>
    /// </summary>
    public static ServiceProvider BuildServices(
        McpSurface surface,
        string? clickHouse,
        string? controlPlane,
        IMcpIdentityRefusal? identity = null)
    {
        var services = new ServiceCollection();

        // M13 · Kimlik ÜRETİLEMEDİĞİNDE sebebi taşıyan servis. `McpCallerScope`
        // onu `GetService` ile arıyor; yoksa eski cümlede kalıyor. İsteğe bağlı
        // olması bilinçli: simülatör yüzeyinde ve belirteçsiz koşumda kimlik
        // yolu hiç açılmıyor.
        if (identity is not null)
        {
            services.AddSingleton(identity);
        }

        // KAYIT YÜZEYE BAĞLI DEĞİL — ve ilk hâli öyleydi, ÖLÇÜLEREK düzeltildi.
        //
        // `McpToolDiscovery.Instantiate` bulduğu HER aracı KURUYOR, yüzeye göre
        // ancak kurduktan SONRA eliyor — `Surface` bir örnek özelliği, yani
        // örneklemeden okunamıyor. Dolayısıyla bir yüzeyi sunmak, keşfin
        // ulaştığı BÜTÜN araçların bağımlılıklarını istiyor.
        //
        // Kayıt `if (surface is Simulator)` ile sınırlıyken ölçülen sonuç:
        // `bizigo mcp serve --surface bizigo` (ÜRÜN yüzeyi) hiç ayağa
        // kalkmıyordu — keşif `Bizigo.Simulators`'a ulaşıp yedi aracı kurmaya
        // çalışıyor ve `SimulatorMcpContext` kayıtlı olmadığı için patlıyordu.
        // Aynı kusur `McpIdentityTests.Kapsam_cozucusu_kayitli_degilse_kurulum_patliyor`'u
        // da düşürdü: o test kökü `Bizigo.UnitTests` veriyor ve oradan da
        // simülatöre ulaşılıyor.
        //
        // Kaydın kendisi bir şey İLAN ETMİYOR: araçlar yüzeylerini kendileri
        // beyan ediyor ve ürün yüzeyinde hiçbiri ilan edilmiyor. Burada olan
        // tek şey, kurulabilir olmaları.
        //
        // NOT (M05 sonrası): keşif artık referans kapanışını yürümüyor,
        // `ToolAssembliesFor(surface)` ne diyorsa onu geziyor — yani yukarıdaki
        // "ürün yüzeyi simülatör araçlarını kurmak zorunda" hâli **yapısal
        // olarak** ortadan kalktı. Kayıt yine de yüzeye bağlanmadı: iki yüzeyin
        // grafiğini ayırmak, hangi servisin hangi yüzey için kayıtlı olduğunu
        // ikinci bir listeye yazmak olurdu (§9), ve bugün ikisinin de kurulması
        // ölçülebilir bir maliyet üretmiyor.
        services.AddBizigoSimulatorTools();

        // M02 — komut çekirdeğinin araçları. Bugün tek bağımlılık
        // `ParserToolbox` (dört parser aracı ve `fields.coverage` onu istiyor).
        // Kurulum PAHALI (grok kütüphanesi + eşleme tabloları diskten okunuyor)
        // ve süreç boyunca değişmiyor — singleton.
        services.AddBizigoCommandTools();

        // ── M12 · ÜRÜN YÜZEYİNİN GRAFİĞİ, ve bu blok YÜZEYE BAĞLI ────────────
        //
        // Yukarıdaki iki kayıt yüzeye bağlı DEĞİL ve öyle kalıyor: ikisi de
        // ayar istemiyor, ölçülebilir bir maliyet üretmiyor, ve ayırmak "hangi
        // servis hangi yüzey için" diye ikinci bir liste yazmak olurdu (§9).
        //
        // Bu blok BAŞKA bir şey: bağlantı ayarı ZORUNLU. Koşulsuz kaydetmek,
        // ClickHouse'a ve kontrol düzlemine hiç dokunmayan simülatör yüzeyinden
        // de iki ortam değişkeni istemek demek olurdu — ve o yüzey bugün
        // ayarsız çalışıyor (ölçüldü: `--surface bizigo-sim` → exit 0).
        // İlgisiz bir ayarı zorunlu kılmak, bekçisi olan bir gerileme.
        if (surface is not McpSurface.Product)
        {
            return services.BuildServiceProvider();
        }

        // Ayarlar BURADA ARANMIYOR. Eksikliğin cevabı `MissingProductSetting`'te
        // ve çağrı yeri `ServeAsync`: kalkış reddi bir DI hatası olarak
        // patlamıyor, ayarı adıyla söylüyor. Buraya bir kontrol daha koymak,
        // aynı kararı iki yerde tutmak olurdu.
        ArgumentException.ThrowIfNullOrWhiteSpace(clickHouse);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPlane);

        // BOŞ YAPILANDIRMA, ve bilerek. `AddBizigoAlerting`/`AddBizigoParsing`
        // bir `IConfiguration` istiyor ve bu süreçte `appsettings.json` YOK —
        // stdio sunucusunu başlatan şey bir masaüstü MCP istemcisi ve onun
        // verdiği kanal `env`. Boş yapılandırma ikisini de kendi
        // varsayılanlarına bırakıyor; ihtiyaç duyulan tek iki ayar yukarıdaki
        // bağlantı dizgeleri ve onlar açık parametre olarak geliyor.
        //
        // ⚠ SONUCU YAZILI: `Security:SecretKey` bu süreçte YOK, yani
        // `SecretProtector` anahtarsız kuruluyor. Ürün kuralı zaten
        // "anahtar yoksa gizli bilgi KAYDEDİLMİYOR" ve bu yüzey hiçbir şey
        // yazmıyor (bekçi: `Hicbir_urun_araci_yazma_cagirmiyor`), dolayısıyla
        // anahtarsızlık bu yüzeyde bir yetenek kaybı değil.
        var configuration = new ConfigurationBuilder().Build();

        // ÜRETİMİN KENDİ UZANTILARI — elle kayıt listesi YAZILMIYOR. İkinci bir
        // liste, HTTP yüzeyinde çözülen bir servisin stdio'da farklı ömürle ya
        // da hiç kaydedilmemiş olması demek; bu depo o ayrışmayı
        // `AddBizigoCommandTools`/`AddBizigoSimulatorTools` kararlarıyla iki kez
        // ödedi (§9).
        services.AddControlPlane(controlPlane);
        services.AddBizigoDataPlane(new ClickHouseOptions { ConnectionString = clickHouse });
        services.AddBizigoParsing(configuration);
        services.AddBizigoAlerting(configuration);
        services.AddBizigoEvidence();

        // M07 — KAYNAKLARIN deposu (`RcaReportStore`), ve bu satır ölçülerek
        // eklendi. M12 bu grafiği ARAÇLAR için kurdu; M07 keşfi ilkellere
        // genişletti (`McpPrimitiveDiscovery`) ve `RcaReportResource`
        // `RcaReportStore` istiyor. Yani ürün yüzeyi yeniden kalkmıyordu ve
        // arıza bu kez M12'nin kendi bekçisinde göründü — bekçi işini yaptı.
        //
        // Kayıt `AddBizigoRcaTriggers` üzerinden, elle değil: `RcaReportStore`
        // orada kayıtlı ve ömrü orada gerekçeli. Ayrıca uzantı yapılandırmayı
        // isteğe bağlı alıyor, yani kota bölümü olmayan bir stdio süreci de
        // varsayılanlarla kurulabiliyor.
        services.AddBizigoRcaTriggers(configuration);

        // `TimeProvider` (TryAdd) — `logs.search`'ün varsayılan penceresi ve
        // `alerts.maintenance`'ın "şimdi"si buradan.
        services.AddBizigoReadTools();

        // M08 · KAPSAM ÇÖZÜCÜSÜ. `BizigoMcpServer.Apply` kimlik isteyen bir
        // araç varsa bunu KURULUMDA arıyor ve yoksa sunucuyu hiç kaldırmıyor.
        //
        // Kayıt `Bizigo.Api`'nin `AuthenticationSetup`'ındakiyle AYNI ŞEKİLDE:
        // somut tip tekil, arayüz ona YÖNLENDİRİLİYOR. `AddSingleton<IAccessScopeResolver,
        // AccessScopeResolver>()` yazmak ikinci bir örnek doğurur ve
        // `RefreshAsync` yalnızca birini tazeler — eşleme tablosu
        // güncellendiğinde iki yüzeyin farklı kapsam vermesi, üstelik sessizce.
        //
        // `AddBizigoAuthentication` çağrılamıyor: o `Bizigo.Api`'de ve CLI'ın
        // ASP.NET'e bağlanması `Bizigo.Mcp` csproj'unda ölçülmüş olarak
        // reddedilmiş bir şey. Çağrılan tip ise aynı tip.
        services.AddSingleton<AccessScopeResolver>();
        services.AddSingleton<Contracts.IAccessScopeResolver>(
            static sp => sp.GetRequiredService<AccessScopeResolver>());

        return services.BuildServiceProvider();
    }
}

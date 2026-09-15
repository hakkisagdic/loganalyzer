using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Bizigo.Query;

namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>IScopedQuery</c>'nin tüketici kümesi — TÜRETİLİYOR, elle yazılmıyor</b>
/// (M18).
///
/// <h3>Neden var: elle liste iki yönde birden yanlıştı</h3>
///
/// <para>
/// <c>IScopedQuery</c>'nin belgesi bir tüketici listesi taşıyordu —
/// <i>"REST uçları, CLI, replay okuma, F3'ün kanıt toplayıcısı ve F4'ün MCP
/// sunucusu"</i>. M18'de ölçüldü ve <b>iki yönde birden</b> yanlıştı:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>Fazla saydığı:</b> <c>Bizigo.Cli</c> ve <c>Bizigo.Replay</c> bu arayüzü
/// <b>hiç anmıyor</b> — ikisi de veri erişimini doğrudan kuruyor
/// (<c>ClickHouseContext</c>, <c>EventWriter</c>).
/// </item>
/// <item>
/// <b>Eksik saydığı:</b> <c>Bizigo.Alerting</c> tüketiyor ve listede yoktu.
/// </item>
/// <item>
/// <b>Yanlış adlandırdığı:</b> <i>"F4'ün MCP sunucusu"</i> — tüketen şey
/// <c>Bizigo.Mcp.Product</c> (araçlar), <b>çekirdek değil</b>. Fark önemli:
/// çekirdeğin bu tipi tanıması, taşıma katmanının veri okuyabildiği anlamına
/// gelirdi.
/// </item>
/// </list>
///
/// <para>
/// Yani liste hem <i>olmayan</i> bir güvence veriyordu (CLI kapıdan geçiyor
/// sanılıyordu) hem <i>olan</i> bir tüketiciyi gizliyordu. Ve yanlışların
/// hiçbirini bir şey yakalamadı, çünkü listeyi tutan tek şey hatırlamaktı.
/// </para>
///
/// <h3>⚠️ Ölçüm aracı seçimi bir kez YANLIŞ ölçtü</h3>
///
/// <para>
/// M18'in ilk ölçümü <c>grep</c>'ti ve <b>yedi</b> derleme saydı; doğru cevap
/// <b>dört</b>. Fark, <c>grep</c>'in <b>yorumları</b> da saymasıydı:
/// <c>Bizigo.Mcp</c> ile <c>Bizigo.ScenarioPlugin</c> bu tipi yalnızca belge
/// yorumlarında anıyor. Bekçinin meta veriye bakmasının sebebi bu — derleyici
/// yorumdan <c>TypeRef</c> satırı yazmıyor. <b>Bir adı metinde bulmak, o tipe
/// dokunmak değil.</b>
/// </para>
///
/// <h3>Bekçinin ASIL yönü: bir katmanın kapıyı KULLANMAYI BIRAKMASI</h3>
///
/// <para>
/// Kümenin <b>büyümesi</b> zaten görünür bir hareket: yeni bir katman kapsamlı
/// veri okumaya başlıyor ve bu bilinçli olmalı. Asıl değer <b>küçülmesinde</b>:
/// bugün buradan geçen bir katman yarın doğrudan <c>EventReader</c>'a inerse
/// kapsam filtresi ve denetim kaydı sessizce atlanır. Bugün onu yakalayan
/// hiçbir şey yok — <c>ArchitectureTests</c> yalnızca <b>API</b> için bu yolu
/// kapatıyor, diğer katmanlar için değil.
/// </para>
///
/// <h3>Neden TypeRef tablosu</h3>
///
/// <para>
/// Soru dar: <i>bu derleme <c>IScopedQuery</c> tipini tanıyor mu.</i> Kalıp
/// <c>McpProductScopeGateTests</c> ve <c>ComputedButUnreadTests</c>'ten —
/// meta veri tablosu erişilebilirliğe bakmıyor, derleyici tipe dokunan her yer
/// için satır yazıyor. Bir IL çağrı grafiği yürüyüşü burada fazla: hangi kökten
/// başlanacağını seçmek gerekirdi ve cevap değişmezdi.
/// </para>
///
/// <para>
/// <c>Bizigo.Query</c> kümede <b>yok</b> ve olamaz: tipi o tanımlıyor, yani
/// kendi içindeki kullanım <c>TypeDef</c> — <c>TypeRef</c> tablosuna hiç
/// girmiyor. Kapı bunu varsaymıyor, <b>ölçüyor</b>.
/// </para>
///
/// <h3>⚠️ Bu bekçinin GÖREMEDİĞİ</h3>
///
/// <para>
/// <b>Tipi tanımak, kapıdan geçmek değil.</b> Bir derleme
/// <c>IScopedQuery</c>'yi yalnızca bir imzada taşıyıp (örneğin başkasına
/// devrederek) hiç çağırmıyor olabilir; TypeRef satırı yine oluşur. Yani bu
/// kapı <i>"tanıyor mu"</i> sorusunu cevaplıyor, <i>"kullanıyor mu"</i>
/// sorusunu değil. Aynı ayrımı M16 ölçtü ve bedelini ödedi:
/// <b>bir alanın okunduğunu ölçmek, okunanın kullanıldığını ölçmek değil.</b>
/// Buradaki hâli daha zayıf ve bilinçli — kullanımın kendisi her tüketicinin
/// kendi testlerinde ölçülüyor.
/// </para>
/// </summary>
public sealed class ScopedQueryConsumerTests
{
    /// <summary>
    /// <b>Kapsam kapısını tanıyan derlemeler — BEYAN.</b>
    ///
    /// <para>
    /// Denetlenen küme türetiliyor; bu <b>beyan</b>. Kalıp
    /// <c>McpExpectedTools</c>'tan ve gerekçesi aynı: iki tarafı da türetmek,
    /// testin kendini kendisiyle karşılaştırması olurdu ve <b>hiçbir zaman
    /// kırmızı yanamazdı</b>. Bir tarafın insan eliyle yazılması bu testin bir
    /// şey söyleyebilmesinin şartı.
    /// </para>
    ///
    /// <para>
    /// Her satır bir <b>ürün yüzeyi ya da onun altındaki bir katman</b>.
    /// Buradan bir ad <b>düşerse</b> o katman kapıyı kullanmayı bırakmış demek —
    /// ve K17 açısından sorulacak soru şu: yerine ne kullanıyor?
    /// </para>
    /// </summary>
    private static readonly string[] Declared =
    [
        // REST uçları — kapsamı `ICurrentUser`'dan alıyor.
        "Bizigo.Api",

        // Alarm motoru: kural değerlendirmesi kapsamlı sayım yapıyor.
        "Bizigo.Alerting",

        // F3'ün kanıt toplayıcısı ve sağlayıcıları.
        "Bizigo.Evidence",

        // MCP ürün araçları — okuma ve (M14'ten beri) tek yazma. MCP
        // ÇEKİRDEĞİ (`Bizigo.Mcp`) kümede YOK ve olmamalı: kapsamı
        // `McpToolInvocation` ile taşıyor ama `AccessScope` olarak, sorguyu
        // araçlar yapıyor. Çekirdeğin bu tipi tanıması, taşıma katmanının
        // veri okuyabildiği anlamına gelirdi.
        "Bizigo.Mcp.Product",
    ];

    /// <summary>
    /// <b>Türetilen küme beyanla aynı.</b>
    ///
    /// <para>
    /// Küçülme yönü asıl olan — gerekçe sınıf belgesinde. Büyüme yönü de
    /// bilinçli bir hareket olmayı sürdürüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapsam_kapisinin_tuketicileri_beyanla_ayni()
    {
        var derived = ProductAssemblies()
            .Where(static assembly => ReferencesScopedQuery(assembly))
            .Select(static assembly => assembly.GetName().Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Declared.Order(StringComparer.Ordinal).ToArray(), derived);
    }

    /// <summary>
    /// <b>Ölçüm aracının kendisi: kapı gerçekten ayırt ediyor.</b>
    ///
    /// <para>
    /// Yukarıdaki test, <c>ReferencesScopedQuery</c> her zaman
    /// <see langword="true"/> dönse de geçebilirdi — beyan o hâlde bütün ürün
    /// derlemelerini saymak zorunda kalırdı ama bunu fark etmek insana kalırdı.
    /// Bu test ayırt ettiğini <b>iki yönde</b> ölçüyor: tüketen bir derleme
    /// <see langword="true"/>, tüketmeyen <see langword="false"/>.
    /// </para>
    ///
    /// <para>
    /// Negatif özne <c>Bizigo.Cli</c> ve seçimi bilinçli: eski listenin
    /// <b>yanlış saydığı</b> derleme o, yani kapının onu doğru sınıflandırdığını
    /// ölçmek aynı zamanda M18'in bulgusunu çiviliyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Olcum_araci_tuketen_ile_tuketmeyeni_ayirt_ediyor()
    {
        Assert.True(
            ReferencesScopedQuery(typeof(Bizigo.Mcp.Product.Tools.LogsSearchTool).Assembly),
            "Ürün araçları kapsam kapısını tanımıyor görünüyor — kapı bozuk.");

        // `Bizigo.Cli` TANIMIYOR: eski liste onu sayıyordu ve yanlıştı.
        Assert.False(
            ReferencesScopedQuery(typeof(Bizigo.Cli.RcaQuotaCommandHandlers).Assembly),
            "`Bizigo.Cli` kapsam kapısını tanıyor görünüyor. Bu bir gerileme DEĞİL de "
            + "olabilir — bir komut ürün yüzeyine sarıldıysa doğru olan bu. O hâlde "
            + "`IScopedQuery` belgesindeki CLI muafiyeti yeniden yazılmalı.");

        // Tanımlayan derleme kümede OLAMAZ: kendi içindeki kullanım `TypeDef`.
        // Kapı bunu varsaymıyor, ölçüyor.
        Assert.False(
            ReferencesScopedQuery(typeof(IScopedQuery).Assembly),
            "`Bizigo.Query` TypeRef tablosunda kendi tipini gösteriyor — ölçüm aracı "
            + "beklenmeyen bir şey görüyor.");
    }

    /// <summary>
    /// Derlenmiş çıktıdaki <c>Bizigo.*</c> derlemeleri — <b>diskten</b>, keşif
    /// yerine.
    /// </summary>
    /// <remarks>
    /// Referans kapanışını yürümek bu soruda <b>yanlış</b> olurdu: budanmış bir
    /// referans (M04'te ölçüldü) bir derlemeyi görünmez yapar ve kapı sessizce
    /// eksik bir kümeyi denetler. Kalıp <c>ProductDiscovery</c>'den (T55) —
    /// konvansiyon yerine diske bak.
    /// </remarks>
    private static IEnumerable<Assembly> ProductAssemblies()
    {
        var directory = Path.GetDirectoryName(typeof(ScopedQueryConsumerTests).Assembly.Location)!;

        foreach (var path in Directory.EnumerateFiles(directory, "Bizigo.*.dll").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            // Test derlemeleri ve simülatör ÜRÜN yüzeyi değil: birincisi kapının
            // öznesi olamaz (kendi kendini sayardı), ikincisi ürün verisine hiç
            // dokunmuyor (K6) ve tanısaydı bu bir bulgu olurdu — ama o soru
            // `McpExpectedTools`'un işi, burada gürültü olurdu.
            if (name.Contains("Tests", StringComparison.Ordinal)
                || string.Equals(name, "Bizigo.Simulators", StringComparison.Ordinal))
            {
                continue;
            }

            Assembly loaded;

            try
            {
                loaded = Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            yield return loaded;
        }
    }

    /// <summary>
    /// Derleme <c>IScopedQuery</c> tipini <c>TypeRef</c> tablosunda taşıyor mu.
    /// </summary>
    private static bool ReferencesScopedQuery(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var reader = new PEReader(stream);

        if (!reader.HasMetadata)
        {
            return false;
        }

        var metadata = reader.GetMetadataReader();

        foreach (var handle in metadata.TypeReferences)
        {
            if (string.Equals(
                metadata.GetString(metadata.GetTypeReference(handle).Name),
                nameof(IScopedQuery),
                StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

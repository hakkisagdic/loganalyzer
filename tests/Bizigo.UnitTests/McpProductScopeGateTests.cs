using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Bizigo.Contracts;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M04'ün kabul kriteri: kapsamı çözücüden AL, kendin KURMA.</b>
///
/// <para>
/// <c>IScopedQuery</c>'nin her metodu <see cref="AccessScope"/> istiyor, yani
/// kapsamsız çağrı <b>yazılamıyor</b>. Ama bir kaçış deliği var ve derleme
/// hatası vermiyor: <c>AccessScope.System(subject)</c> statik ve
/// <c>IsUnrestricted = true</c>. Kapsamı atlamak için <b>unutmak gerekmiyor,
/// çağırmak yetiyor</b>.
/// </para>
///
/// <para>
/// <b>Kriter "System'e hiç başvurma" DEĞİL</b> ve bu ayrım ölçülerek
/// keskinleşti. Ürün kodunda iki çağıran var ve ikisi de meşru:
/// </para>
/// <list type="bullet">
/// <item><c>Bizigo.Replay/ReplayEngine.cs</c> — sistem içi yazım.</item>
/// <item>
/// <c>Bizigo.ControlPlane/AccessScopeResolver.cs</c> — kimlik doğrulanmış <b>ve</b>
/// <c>admin</c> rolündeyse tam kapsam. Kaynağın kendi yorumu: <i>"admin kapsam
/// filtresinden muaf — ama bu BİLİNÇLİ ve tek yerde."</i>
/// </item>
/// </list>
///
/// <para>
/// Bir aracın <c>AccessScope.System</c>'i <b>kendi</b> çağırması, resolver'ın o
/// kararını <b>atlamak</b> olur — admin kontrolünü, kimlik doğrulamasını,
/// hepsini. Kriter bunu ölçüyor.
/// </para>
///
/// <para>
/// <b>Aramanın kapsamı — beyan.</b> Ürün kodunda `AccessScope.System` çağıranları
/// <c>src/</c>, <c>tests/</c>, <c>tools/</c> ve <c>sim/</c> altında <b>arandı</b>:
/// <c>src/</c> altında yukarıdaki iki çağıran (artı üç dosyanın <i>neden
/// kullanmadığını</i> yazan yorumları), <c>tests/</c> altında yirmi çağıran
/// (hepsi test kurulumu), <c>tools/</c> ve <c>sim/</c> altında <b>hiç yok</b>.
/// Bu kapı yalnızca araç derlemesine bakıyor, dolayısıyla test çağıranları onu
/// ilgilendirmiyor — ama bir sonraki kişi kapsamı geniş sanmasın diye yazılı.
/// </para>
/// </summary>
public sealed class McpProductScopeGateTests
{
    /// <summary>
    /// <b>Araç derlemesi <c>AccessScope.System</c>'e HİÇ başvurmuyor.</b>
    ///
    /// <para>
    /// <b>Neden IL çağrı grafiği değil MemberRef tablosu.</b> <c>T50</c>'nin
    /// kalıbı bir kökten başlayıp <b>erişilebilir</b> çağrıları yürüyor ve o soru
    /// için doğru. Buradaki soru daha dar ve daha sert: <i>bu derleme o metodu
    /// tanıyor mu.</i> Erişilebilirlik yürüyüşü, ölü koda gömülü bir çağrıyı
    /// <b>kaçırırdı</b> — ve ölü kod bir gün canlanıyor. Meta veri tablosu
    /// erişilebilirliğe bakmıyor: derleyici bir metodu çağıran her <c>call</c>
    /// için oraya bir satır yazıyor.
    /// </para>
    ///
    /// <para>
    /// <c>AccessScope</c> ayrı bir derlemede (<c>Bizigo.Contracts</c>), yani
    /// çağrısı <b>MemberRef</b> olarak görünüyor — aynı derleme içi bir çağrı
    /// olsaydı <c>MethodDef</c> olurdu ve bu tablo onu göstermezdi. Bu bağımlılık
    /// varsayılmıyor: kapı ayrıca <c>AccessScope</c>'un gerçekten dışarıda
    /// olduğunu <b>ölçüyor</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Arac_derlemesi_AccessScope_System_cagirmiyor()
    {
        // Kapının kendi ön şartı: `AccessScope` bu derlemenin DIŞINDA olmalı,
        // yoksa MemberRef tablosuna hiç girmez ve kapı sessizce yeşil yanar —
        // bu deponun adını koyduğu hata sınıfı.
        Assert.NotEqual(
            typeof(LogsSearchTool).Assembly,
            typeof(AccessScope).Assembly);

        var referenced = MemberReferences(typeof(LogsSearchTool).Assembly);

        Assert.DoesNotContain(
            $"{nameof(AccessScope)}.{nameof(AccessScope.System)}",
            referenced);

        // Kapının kırmızı yanabildiğinin kanıtı aynı ölçümün içinde: derleme
        // `AccessScope`'un BAŞKA üyelerine gerçekten başvuruyor. Tablo boş
        // gelseydi yukarıdaki iddia hiçbir şey ifade etmezdi.
        Assert.Contains(
            $"{nameof(AccessScope)}.get_{nameof(AccessScope.IsEmpty)}",
            referenced);
    }

    /// <summary>
    /// <b>Kapsam kapısını atlayan bir araç gövdesi YAZILAMIYOR.</b>
    ///
    /// <para>
    /// M01'in <c>ExecuteAsync</c> kancası <see cref="ProductReadTool"/>'da
    /// <c>sealed</c>; araçlar yalnızca kapsamı <b>parametre olarak alan</b>
    /// aşırı yüklemeyi uygulayabiliyor. Bu test o mühürün gerçekten orada
    /// olduğunu ölçüyor — kaldırılırsa her yeni araç kendi kapsamını kurmaya
    /// açılır.
    /// </para>
    /// </summary>
    [Fact]
    public void M01_kancasi_muhurlu()
    {
        var hook = typeof(ProductReadTool).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(McpToolInvocation), typeof(CancellationToken)]);

        Assert.NotNull(hook);
        Assert.True(hook!.IsFinal, "M01 kancası `sealed` değil: bir araç onu geçersiz kılıp kapsamı atlayabilir.");
    }

    /// <summary>
    /// <b>Her ürün aracı kapsam kapısını GÖREN bir tabandan türüyor</b> — okuma
    /// ya da yazma.
    ///
    /// <para>
    /// Yukarıdaki mühür yalnızca o tabanlardan türeyenleri bağlıyor. Ürün
    /// yüzeyinde <see cref="BizigoMcpTool"/>'dan <b>doğrudan</b> türeyen bir
    /// araç, kapsam kapısını hiç görmeden ürün verisi döndürebilirdi — ve
    /// yeşil bir test paketiyle birlikte.
    /// </para>
    ///
    /// <para>
    /// <b>İKİ taban kabul ediliyor (M14) ve bu bir gevşetme DEĞİL:</b>
    /// <see cref="ProductWriteTool"/> kapsam reddini <b>aynı statikten</b>
    /// çağırıyor (<see cref="ProductReadTool.ScopeRejection"/>), yani ikinci bir
    /// kapı açılmadı — açılsaydı §9'un yasakladığı kopya olurdu ve bu testin
    /// ölçtüğü şey de zayıflardı. Kabul ölçütü <i>"şu iki tipten biri"</i> değil,
    /// <b>"o statiği çağıran mühürlü bir kanca"</b>; aşağıdaki iddia bunu ayrıca
    /// ölçüyor.
    /// </para>
    ///
    /// <para>
    /// <c>ServerInfoTool</c> muaf ve muafiyeti gerekçeli: çekirdeğin kendi aracı,
    /// ürün verisine hiç dokunmuyor, ve iki yüzeyde de var.
    /// </para>
    /// </summary>
    [Fact]
    public void Urun_yuzeyindeki_her_arac_kapsam_tabanindan_turuyor()
    {
        var strays = McpToolDiscovery
            .ToolTypes([typeof(LogsSearchTool).Assembly])
            .Where(static type =>
                !typeof(ProductReadTool).IsAssignableFrom(type)
                && !typeof(ProductWriteTool).IsAssignableFrom(type))
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            "Bu araç(lar) ne `ProductReadTool` ne `ProductWriteTool`'dan türüyor, yani kapsam "
            + "kapısını hiç görmüyor:\n  " + string.Join("\n  ", strays));

        // YAZMA TABANI DA MÜHÜRLÜ ve AYNI STATİĞİ çağırıyor. Olmadan yukarıdaki
        // iddia bir tip adı listesine inerdi: `ProductWriteTool` kendi kapsam
        // kontrolünü yazsaydı bu test yine yeşil kalırdı, ve K17'nin yolunda
        // ikinci bir kapı sessizce doğardı (§9).
        var writeHook = typeof(ProductWriteTool).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(McpToolInvocation), typeof(CancellationToken)]);

        Assert.NotNull(writeHook);
        Assert.True(
            writeHook!.IsFinal,
            $"`{nameof(ProductWriteTool)}` kancası `sealed` değil: yazan bir araç onu geçersiz "
            + "kılıp kapsamı atlayabilir.");

        var unresolved = 0;

        var callsSharedGate = IlCallReader
            .Callees(writeHook, ref unresolved)
            .Any(static callee => string.Equals(
                callee.Name, nameof(ProductReadTool.ScopeRejection), StringComparison.Ordinal));

        Assert.True(
            callsSharedGate,
            $"`{nameof(ProductWriteTool)}` kapsam reddini `{nameof(ProductReadTool)}."
            + $"{nameof(ProductReadTool.ScopeRejection)}`'dan ÇAĞIRMIYOR — yani ikinci bir kapsam "
            + "kapısı yazılmış. §9: tek kapı, iki çağıran.");
    }

    /// <summary>
    /// <b>Kapsam filtresinden muaf araçlar SAYILIYOR.</b>
    ///
    /// <para>
    /// <see cref="ProductReadTool.ReadsScopedData"/> <see langword="false"/> olan
    /// araçlarda boş kapsam reddedilmiyor — gerekçesi REST'in kendi kararı:
    /// <i>"katalog veri değil, yapılandırma."</i> Muafiyet <b>listeyle</b> ve
    /// <b>sayıyla</b> tutuluyor: eklemek iki ayrı bilinçli hareket gerektiriyor
    /// (§8, kalıp <c>ProducesContractTests.ExpectedExemptCount</c>).
    /// </para>
    /// </summary>
    [Fact]
    public void Kapsam_muafiyeti_sayili()
    {
        string[] expectedExempt = [CatalogParsersTool.ToolIdentifier];

        const int expectedExemptCount = 1;

        var exempt = McpToolDiscovery
            .Instantiate(
                McpToolDiscovery.ToolTypes([typeof(LogsSearchTool).Assembly]),
                McpSurface.Product,
                McpTestServices.ForDiscoveredTools())
            .OfType<ProductReadTool>()
            .Where(static tool => !tool.ReadsScopedData)
            .Select(static tool => tool.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedExempt.Order(StringComparer.Ordinal).ToArray(), exempt);

        // Sayı ayrıca çivili: liste ile sayı BİRLİKTE değişmek zorunda, yani
        // muafiyet eklemek iki ayrı bilinçli hareket (§8).
        Assert.True(
            exempt.Length == expectedExemptCount,
            $"Kapsam muafiyeti sayısı {exempt.Length} oldu; beklenen {expectedExemptCount}.");
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Derlemenin <c>MemberRef</c> tablosu, <c>Tip.üye</c> biçiminde.
    ///
    /// <para>
    /// <c>System.Reflection.Metadata</c> ile okunuyor; yansıma bunu vermiyor
    /// (yansıma <b>tanımlara</b> bakıyor, <b>başvurulara</b> değil).
    /// </para>
    /// </summary>
    private static IReadOnlySet<string> MemberReferences(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var peReader = new PEReader(stream);

        var metadata = peReader.GetMetadataReader();
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in metadata.MemberReferences)
        {
            var reference = metadata.GetMemberReference(handle);
            var member = metadata.GetString(reference.Name);

            var owner = reference.Parent.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(
                    metadata.GetTypeReference((TypeReferenceHandle)reference.Parent).Name),
                HandleKind.TypeDefinition => metadata.GetString(
                    metadata.GetTypeDefinition((TypeDefinitionHandle)reference.Parent).Name),
                _ => null,
            };

            if (owner is not null)
            {
                found.Add($"{owner}.{member}");
            }
        }

        return found;
    }
}

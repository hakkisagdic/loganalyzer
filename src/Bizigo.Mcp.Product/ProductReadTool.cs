using Bizigo.Contracts;

namespace Bizigo.Mcp.Product;

/// <summary>
/// <b>`bizigo` yüzeyindeki her okuma aracının tabanı — ve kapsamın tek kapısı.</b>
///
/// <para>
/// M01'in <see cref="BizigoMcpTool.ExecuteAsync(McpToolInvocation, CancellationToken)"/>
/// kancası burada <c>sealed</c>. Bir okuma aracı onu <b>geçersiz kılamıyor</b>;
/// yalnızca <see cref="ExecuteScopedAsync"/>'i uygulayabiliyor ve o imza
/// <see cref="AccessScope"/>'u <b>parametre olarak alıyor</b>. Sonuç bir
/// alışkanlık değil bir derleme gerçeği:
/// </para>
/// <list type="number">
/// <item>
/// <b>Kapsamsız bir araç gövdesi yazılamıyor.</b> Kapsam parametre olarak
/// geliyor, aranmıyor.
/// </item>
/// <item>
/// <b>Araç kendi kapsamını kuramıyor.</b> <c>AccessScope.System(...)</c>
/// çağırmasının hiçbir sebebi kalmıyor — ve kalmadığı
/// <c>McpProductScopeGateTests</c> tarafından derlemenin MemberRef tablosundan
/// ÖLÇÜLÜYOR. Kriter *"System'e hiç başvurma"* değil: <c>admin</c>'in tam
/// kapsam alması <c>AccessScopeResolver</c>'ın bilinçli ve tek yerdeki kararı.
/// Kriter <b>kapsamı çözücüden AL, kendin KURMA</b>.
/// </item>
/// </list>
///
/// <para>
/// <b>Neden taban sınıf, neden bir kod incelemesi kuralı değil.</b> Bu depoda
/// hatırlamaya dayanan mekanizmanın kaç kez kaybettiği ölçüldü (§7). Kapsam
/// atlanmasının bedeli ürünün en pahalı hatası: <i>log verisinin kurumdan
/// çıkması</i>. Bir aracın <c>ExecuteAsync</c>'i doğrudan uygulayabilmesi,
/// K17'nin kapısını <b>her yeni araçta yeniden kurulması gereken</b> bir şeye
/// çevirirdi.
/// </para>
/// </summary>
public abstract class ProductReadTool : BizigoMcpTool
{
    /// <summary>
    /// Yüzey <c>sealed</c>: bir okuma aracı <c>bizigo-sim</c>'e düşemiyor.
    ///
    /// <para>
    /// <c>ServerInfoTool</c> yüzeyini yapıcıdan alıyor çünkü iki yüzeyde
    /// de var. Ürün verisi döndüren bir araç için aynı esneklik bir kusur olurdu:
    /// simülatör yüzeyinin gevşekliği ürün verisine uygulanırdı (K6, bkz.
    /// <see cref="McpSurface"/> belgesi).
    /// </para>
    /// </summary>
    public sealed override McpSurface Surface => McpSurface.Product;

    /// <summary>
    /// Okuma araçları ürünün durumunu değiştirmiyor. <c>sealed</c>: yazma yapan
    /// bir araç bu tabandan türemesin — <c>ReadOnlyHint</c> ile yalan söylemek,
    /// modele "bunu serbestçe deneyebilirsin" demek olurdu.
    /// </summary>
    public sealed override bool IsReadOnly => true;

    /// <summary>
    /// <b>Ömür uyarısı — ölçülmüş.</b>
    ///
    /// <para>
    /// Araçlar <b>bir kez</b> kuruluyor (<c>BizigoMcpServer.Apply</c> sunucu
    /// seçeneklerini doldururken) ve sunucunun ömrü boyunca
    /// <c>ToolCollection</c>'da duruyorlar. Yani bir araç yapıcısında
    /// tutabileceği tek şey <b>singleton</b>.
    /// </para>
    ///
    /// <para>
    /// <c>IScopedQuery</c> <b>scoped</b> (<c>QueryServiceCollectionExtensions</c>:
    /// <c>AddScoped&lt;IScopedQuery, ScopedQuery&gt;</c>). Yapıcıda istemek iki
    /// ayrı arıza üretiyor ve ilki ölçüldü:
    /// </para>
    /// <list type="number">
    /// <item>
    /// Kök sağlayıcıdan scoped servis çözülemiyor — araç kurulumu
    /// <b>patlıyor</b> ve MCP yüzeyinin tamamı ayağa kalkmıyor.
    /// </item>
    /// <item>
    /// Kalksaydı daha kötüsü olurdu: <b>esir bağımlılık</b>. Tek bir
    /// <c>ScopedQuery</c> örneği sunucunun ömrü boyunca <b>bütün</b> araç
    /// çağrılarına hizmet ederdi — yani bütün kullanıcılara. Bir bağlantı
    /// havuzu nesnesinin paylaşılması burada bir performans notu değil, K17'nin
    /// tam ortasında duran bir durum paylaşımı.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// Bu yüzden scoped bağımlılık isteyen araçlar <c>IServiceScopeFactory</c>
    /// (singleton) alıyor ve <b>çağrı başına</b> kapsam açıyor. Singleton
    /// bağımlılıklar (<c>AlertRuleService</c>, <c>ParserCatalog</c>) doğrudan
    /// yapıcıda duruyor.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Gerekçe bilerek <b>taban sınıfta</b>: iki araç aynı kalıbı kullanıyor ve
    /// açıklamanın iki yere kopyalanması, bir gün birinde güncellenip diğerinde
    /// bayatlaması demek olurdu.
    /// </remarks>
    protected ProductReadTool()
    {
    }

    /// <summary>
    /// Bu araç <b>kapsamlı veri</b> mi döndürüyor.
    ///
    /// <para>
    /// Neredeyse her araç için <see langword="true"/>, ve o hâlde boş kapsam
    /// <c>not_found</c> ile reddediliyor. <see langword="false"/> yalnızca
    /// <b>yapılandırma</b> döndüren araçlar için: REST tarafında
    /// <c>/v1/parsers</c>'ın kendi yorumu bu ayrımı çiziyor —
    /// <i>"katalog veri değil, yapılandırma: kapsam filtresi uygulanmıyor. Bir
    /// ekibin hangi parser'ların var olduğunu görmesi kimsenin logunu görmesi
    /// anlamına gelmiyor."</i>
    /// </para>
    ///
    /// <para>
    /// <b>Kimlik yine şart.</b> <see langword="false"/> kapsamı atlatmıyor,
    /// yalnızca <i>boş</i> kapsamı reddetmeyi kapatıyor: çözücü hâlâ koşuyor ve
    /// kimliksiz çağrı hâlâ araca hiç ulaşmıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Neden bir bayrak, neden ikinci bir taban sınıf değil.</b> Muafiyetin
    /// <b>sayılabilir</b> olması isteniyor: <c>McpProductScopeGateTests</c>
    /// muaf araç kümesini elle yazılmış bir listeye karşı sınıyor ve sayıyı
    /// sabitle tutuyor. Kalıp <c>ProducesContractTests.ExpectedExemptCount</c>'tan
    /// — muafiyet eklemek <b>iki ayrı bilinçli hareket</b> gerektiriyor (§8).
    /// Ayrı bir taban sınıf bunu sayılamaz kılardı.
    /// </para>
    /// </summary>
    protected internal virtual bool ReadsScopedData => true;

    /// <summary>
    /// Aracın gerçek işi. Kapsam <b>verilmiş</b> geliyor.
    ///
    /// <para>
    /// <c>internal protected</c>: türeyen araçlar uyguluyor, birim testleri
    /// doğrudan çağırıyor. İkincisi olmadan gövde ancak
    /// <see cref="BizigoMcpTool.SampleAsync"/> üzerinden görülürdü ve o yol
    /// şemayı ölçüyor, kapsam davranışını ölçmüyor.
    /// </para>
    /// </summary>
    protected internal abstract ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// M01'in kancası. <c>sealed</c> — gerekçesi sınıf belgesinde.
    /// </summary>
    protected sealed override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ProductToolScope.TryResolve(invocation, out var scope, out var error))
        {
            return ValueTask.FromResult(McpToolResult.Failure(error));
        }

        if (ScopeRejection(scope, ReadsScopedData) is { } rejection)
        {
            return ValueTask.FromResult(McpToolResult.Failure(rejection));
        }

        return ExecuteScopedAsync(invocation, scope, cancellationToken);
    }

    /// <summary>
    /// Boş kapsam reddi — <b>saf fonksiyon</b>. <see langword="null"/> dönerse
    /// araç koşuyor.
    ///
    /// <para>
    /// Kontrol ARAÇ BAŞINA DEĞİL burada: REST tarafında her uç kendi
    /// <c>scope.IsEmpty</c> kontrolünü yazıyor (<c>EventsEndpoints</c>,
    /// <c>SourcesEndpoints</c>, …) ve o kalıp bir ucun unutmasına açık. Burada
    /// unutulamıyor.
    /// </para>
    ///
    /// <para>
    /// Cevap <c>not_found</c>, <b>boş liste değil</b>: <c>logs.search</c>'ün sıfır
    /// satırı model tarafından <i>"eşleşme yok"</i> diye okunurdu — oysa
    /// söylenmesi gereken şey <i>"bu kimliğin göreceği hiçbir grup yok"</i>.
    /// </para>
    ///
    /// <para>
    /// <b>Neden ayrı ve <c>static</c>.</b> Kararın kendisi mühürlü kancanın
    /// içine gömülüydü ve <c>protected</c> bir kancayı test edemiyordu; testi
    /// yazmanın tek yolu cevabı <b>testte tekrar yazmak</b> olurdu — yani testin
    /// kendi iddiasını doğrulaması, §6'nın adını koyduğu şey. Karar buraya
    /// çıkarıldı: kanıt <b>bu fonksiyonun</b> ölçülmesi, kullanımı ise tek yerde
    /// ve <c>sealed</c>.
    /// </para>
    /// </summary>
    protected internal static McpToolError? ScopeRejection(AccessScope scope, bool readsScopedData)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!readsScopedData || !scope.IsEmpty)
        {
            return null;
        }

        return new McpToolError(
            McpToolError.NotFound,
            "Bu kimliğin görebileceği hiçbir `owner_group` yok; sorgu koşturulmadı.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subject"] = scope.Subject,
            });
    }
}

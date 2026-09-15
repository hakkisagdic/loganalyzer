using Bizigo.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// Bu üründeki <b>her</b> MCP kaynağının tabanı — araç tarafındaki
/// <see cref="BizigoMcpTool"/>'un ikizi.
///
/// <para>
/// <b>Araç değil veri.</b> Bir araç çağrılır ve bir iş yapar; bir kaynak
/// adreslenir ve okunur. Ayrımın ölçülen kazancı bağlam bütçesi: kaynaklar
/// <c>tools/list</c>'e girmiyor, yani araç başına ödenen <b>194 belirteç</b>
/// (M01'in ölçümü) üç belge türü için ödenmiyor.
/// </para>
///
/// <h3>Protokol yüzeyi mühürlü — gerekçe araç tarafındakiyle aynı</h3>
///
/// <para>
/// <see cref="ReadAsync"/> ve <see cref="ProtocolResourceTemplate"/>
/// <c>sealed</c>. Bir kaynak kendi <c>ReadResourceResult</c>'ını kuramıyor,
/// yalnızca <see cref="McpResourceBody"/> döndürebiliyor — ve o tip serbest
/// <c>string</c> kabul etmiyor (<see cref="McpResourceBody"/>). Mühürleme
/// kapının kendisi değil, <b>kapının takılabildiği yer</b>.
/// </para>
///
/// <h3>Kapsam okuma yolunda, adreste değil</h3>
///
/// <para>
/// Kabul kriteri 3'ün şartı: bir kaynak URI'si <b>tahmin edilebilir</b> olsa da
/// (sıralı kimlik, bilinen <c>Guid</c>) kapsam kontrolü URI'nin kendisinde
/// değil okuma yolunda. Burada mühürlü <see cref="ReadAsync"/> kimliği çözüyor
/// ve <see cref="ReadBodyAsync"/>'e kapsamı <b>parametre olarak</b> veriyor —
/// yani kapsamsız bir kaynak gövdesi <b>yazılamıyor</b>. Kalıp
/// <c>ProductReadTool.ExecuteScopedAsync</c>'ten; ikinci bir gösterim değil,
/// aynı kapının ikinci kanaldaki hâli.
/// </para>
///
/// <h3>Hata ayrımı araç tarafından FARKLI — ve fark spesifikasyonun kendisinde</h3>
///
/// <para>
/// M01 §4 hata ayrımını şöyle kurdu: <i>araç hatası <c>isError</c> ile döner,
/// protokol istisnası değil.</i> Kaynak kanalında <b><c>isError</c> yok</b> —
/// <c>ReadResourceResult</c> böyle bir alan taşımıyor. Yani "bu belge yok"
/// cevabının spesifikasyondaki tek yeri bir JSON-RPC hatası
/// (<see cref="McpErrorCode.ResourceNotFound"/>). Bu bir tutarsızlık değil, iki
/// kanalın şeklinin farkı; yazılı olması gerekiyor çünkü <i>"MCP'de iş hatası
/// hep <c>isError</c>"</i> cümlesi buraya geldiğinde <b>yanlış</b> oluyor.
/// </para>
///
/// <para>
/// <b>Ve iki ret aynı cevaba inmiyor:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Kimlik yok</b> → <see cref="McpErrorCode.InvalidRequest"/> ve mesaj sebebi
/// söylüyor. "Bulunamadı" demek, kimliği kaybolmuş bir istemciye <i>"o belge
/// yok"</i> demek olurdu ve o istemci belgeyi aramaya giderdi.
/// </item>
/// <item>
/// <b>Kimlik var, kapsam yetmiyor</b> → <see cref="McpErrorCode.ResourceNotFound"/>,
/// yani <b>gerçekten yok olanla ayırt edilemez</b>. Gerekçe REST tarafında
/// yazılı ve aynen geçerli (<c>BundleScope.IsReadableBy</c>): 403 paketin var
/// olduğunu <b>doğrular</b> ve bu tek başına bir sızıntı — bir pencerede RCA
/// koşulduğu bilgisi.
/// </item>
/// </list>
/// </summary>
public abstract class BizigoMcpResource : McpServerResource
{
    private ResourceTemplate? cachedTemplate;
    private Resource? cachedResource;

    /// <summary>
    /// Adresin tür segmenti — <c>rca-report</c>, <c>evidence-bundle</c>, …
    /// Şablon buradan türetiliyor, elle yazılmıyor.
    /// </summary>
    public abstract string Kind { get; }

    /// <summary>
    /// Hangi yüzeye ait. <see cref="McpSurface.Unspecified"/> ilan sırasında
    /// <b>reddediliyor</b> — araç tarafındaki kuralın aynısı: beyansız bir
    /// kaynak sessizce ürün verisi kümesine düşerdi (K6).
    /// </summary>
    public abstract McpSurface Surface { get; }

    /// <summary>İnsana gösterilen başlık.</summary>
    public abstract string ResourceTitle { get; }

    /// <summary>
    /// Modele gösterilen açıklama. <b>Bağlam bütçesinin kalemi</b>: bu metin
    /// <c>resources/templates/list</c> yanıtında taşınıyor ve bedeli ölçülüyor
    /// (<c>McpResourceBudgetTests</c>).
    /// </summary>
    public abstract string ResourceDescription { get; }

    /// <summary>Gövdenin türü — <see cref="McpResourceMimeTypes"/>.</summary>
    public abstract string BodyMimeType { get; }

    /// <summary>
    /// Kaynak bir <b>kimlikle</b> mi adresleniyor.
    ///
    /// <para>
    /// <see langword="true"/> → şablon (<c>bizigo://tür/{id}</c>), yani
    /// <c>resources/templates/list</c>'te görünüyor. <see langword="false"/> →
    /// tek bir adres (<c>bizigo://tür</c>) ve <c>resources/list</c>'te
    /// görünüyor. Aboneliğin hedefi ikinci biçim: abone olunacak şey <b>bir</b>
    /// belge, değişen şey içeriği.
    /// </para>
    /// </summary>
    public virtual bool IsAddressedById => true;

    /// <summary>
    /// Kaynak çağıranın <b>kimliğini</b> istiyor mu.
    ///
    /// <para>
    /// Varsayılan araç tarafıyla aynı gerekçeyle güvenli tarafta: ürün
    /// yüzeyindeki her kaynak kimlik ister. Muafiyet iki bilinçli hareket —
    /// burayı ezmek <i>ve</i> <c>McpResourceIdentityTests</c>'in gerekçeli
    /// listesine girmek.
    /// </para>
    /// </summary>
    public virtual bool RequiresCallerIdentity => Surface is McpSurface.Product;

    /// <summary>
    /// Kaynak <b>kapsamlı veri</b> mi döndürüyor.
    ///
    /// <para>
    /// <see langword="false"/> yalnızca <b>yapılandırma</b> döndüren kaynaklar
    /// için — parser tanımı gibi. Ayrım REST tarafında zaten çizili
    /// (<c>/v1/parsers</c>: <i>"katalog veri değil, yapılandırma"</i>) ve burada
    /// ikinci kez çizilmiyor, aynı cümle taşınıyor. <b>Kimlik yine şart</b>:
    /// bu bayrak yalnızca <i>boş</i> kapsamı reddetmeyi kapatıyor.
    /// </para>
    /// </summary>
    public virtual bool ReadsScopedData => true;

    /// <summary>
    /// Kaynak <b>abonelik</b> destekliyor mu.
    ///
    /// <para>
    /// <b>Hangi RPC — ve burada bir bayat ad duruyordu.</b> Bu satır
    /// <c>resources/subscribe</c> yazıyordu ve o metot çivilediğimiz revizyonda
    /// <b>yok</b>: <c>2026-07-28</c> (SEP-2575) onu ve <c>resources/unsubscribe</c>'ı
    /// kaldırıp yerine <c>subscriptions/listen</c> + <c>resourceSubscriptions</c>
    /// koydu. Ölçüldü — sunucu eski metodu göç ipucuyla reddediyor:
    /// <i>"The method 'resources/subscribe' is not available on protocol version
    /// '2026-07-28'. Use 'subscriptions/listen' with 'resourceSubscriptions'
    /// instead."</i>
    /// </para>
    ///
    /// <para>
    /// Yani bayrak <c>subscriptions/listen</c>'in <c>resourceSubscriptions</c>
    /// listesini <b>onaylayıp onaylamamayı</b> belirliyor, ve bu bir süsleme
    /// değil: ölçüldü — bayrak kapalıyken sunucu o isteği <b>onaylamıyor</b>
    /// (<c>subscriptions/acknowledged</c>'ın <c>notifications</c>'ı boş dönüyor).
    /// </para>
    ///
    /// <para>
    /// Varsayılan <see langword="false"/>, ve varsayılanın burada olması
    /// bilinçli: abonelik ilan etmek istemciye <i>"bu değiştiğinde haber
    /// vereceğim"</i> demek. Bildirimi göndermeyen bir abonelik, istemciyi
    /// <b>hiç sormamaya</b> ikna eder — yani sessizce bayat veri. Plan yalnızca
    /// koşum durumunu <i>"anlamlı"</i> işaretliyor ve bugün abonelik destekleyen
    /// tek kaynak <c>bizigo://rca-runs</c>.
    /// </para>
    /// </summary>
    public virtual bool SupportsSubscription => false;

    /// <summary>
    /// SDK'nın kaynak başına üstveri torbası. Araç tarafındaki gerekçeyle boş:
    /// kimlik kararının verildiği yer <see cref="ReadAsync"/>'in içi, çağrı
    /// başına, istek bağlamı elimizdeyken.
    /// </summary>
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>
    /// <b>Uyum kapısının okuduğu örnek gövde.</b>
    ///
    /// <para>
    /// Araç tarafındaki <c>SampleAsync</c> ile aynı şart: gerçek okuma yolunun
    /// <b>şekillendirme</b> kısmını kullanmalı — yani gövdeyi
    /// <see cref="McpResourceBody.Of"/>'a gerçek belge tipini serileştirerek
    /// vermeli. Elle yazılmış bir sabit döndüren örnek, kapının kendini
    /// doğrulaması olurdu.
    /// </para>
    ///
    /// <para>
    /// <b>Şart değil:</b> altyapıya bağlanmak. Kanıtlanması gereken şey
    /// gövdenin redaksiyon kapısından geçtiği ve ilan edilen <c>mimeType</c> ile
    /// tutarlı olduğu; veritabanının cevabı entegrasyon testinin işi (§2).
    /// </para>
    ///
    /// <para>
    /// <b>İptal:</b> uyum kapısı bunu <b>zaten iptal edilmiş</b> bir belirteçle
    /// de çağırıyor ve <c>OperationCanceledException</c> bekliyor — araç
    /// tarafındaki ölçümün aynısı, çünkü bir kaynak okuması da ClickHouse'a
    /// inebiliyor.
    /// </para>
    /// </summary>
    public abstract ValueTask<McpResourceBody> SampleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kaynağın gerçek okuması. Kapsam <b>verilmiş</b> geliyor.
    ///
    /// <para>
    /// <see langword="null"/> dönmek "bu belge yok" demek ve
    /// <see cref="McpErrorCode.ResourceNotFound"/>'a çevriliyor. <b>Kapsam
    /// yetmediğinde de <see langword="null"/> dönülmeli</b> — ayrı bir hata
    /// kodu, belgenin var olduğunu doğrulardı (sınıf belgesi).
    /// </para>
    /// </summary>
    protected abstract ValueTask<McpResourceBody?> ReadBodyAsync(
        McpResourceRead read,
        CancellationToken cancellationToken);

    /// <summary>
    /// Şablonlu ilan. <c>sealed</c>: bir kaynak kendi ilanını yeniden
    /// yazamıyor, yoksa uyum kapısının denetlediği şey ile sunucunun ilan ettiği
    /// şey ayrışabilirdi.
    /// </summary>
    public sealed override ResourceTemplate ProtocolResourceTemplate =>
        cachedTemplate ??= new ResourceTemplate
        {
            UriTemplate = IsAddressedById ? McpResourceUri.Template(RequireKind()) : McpResourceUri.Fixed(RequireKind()),
            Name = Kind,
            Title = ResourceTitle,
            Description = ResourceDescription,
            MimeType = BodyMimeType,
        };

    /// <summary>
    /// Şablonsuz ilan — yalnızca <see cref="IsAddressedById"/> false ise.
    /// Şablonlu bir kaynağın <c>resources/list</c>'te görünmesi, istemciye
    /// okuyabileceği <b>somut</b> bir adres olduğunu söylemek olurdu.
    ///
    /// <para>
    /// <b><c>IsTemplated</c> geçersiz kılınmıyor — ÖLÇÜLDÜ.</b> İlk hâlinde
    /// <c>sealed override bool IsTemplated => IsAddressedById;</c> yazılıydı ve
    /// derlenmedi (<c>CS0506</c>): SDK o üyeyi sanal bırakmıyor, <b>buradan</b>
    /// hesaplıyor — <c>ProtocolResource</c> <see langword="null"/> ise şablonlu.
    /// Yani şablonluluk iki yerde beyan edilemiyor ve doğrusu bu: tek beyan
    /// <see cref="IsAddressedById"/>, geri kalanı ondan türüyor.
    /// </para>
    /// </summary>
    public sealed override Resource? ProtocolResource =>
        IsAddressedById ? null : cachedResource ??= ProtocolResourceTemplate.AsResource();

    /// <summary>
    /// Bu adres bu kaynağa mı ait.
    ///
    /// <para>
    /// <b>Kendi ayrıştırıcısını kullanıyor</b> (<see cref="McpResourceUri"/>),
    /// SDK'nın şablon eşleştirmesini değil: ilan edilen şablon ile okumada kabul
    /// edilen adres <b>aynı</b> kurala bakmalı. İki ayrıştırıcı, bir gün ilan
    /// edilen şablonun kabul edilmediği (ya da tersi) bir hâl demekti.
    /// </para>
    /// </summary>
    public sealed override bool IsMatch(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!IsAddressedById)
        {
            return string.Equals(uri, McpResourceUri.Fixed(RequireKind()), StringComparison.Ordinal);
        }

        return McpResourceUri.TryParse(uri, out var parsed)
            && string.Equals(parsed.Kind, Kind, StringComparison.Ordinal);
    }

    /// <summary>
    /// Protokol okuması. <c>sealed</c> — gerekçesi sınıf belgesinde.
    /// </summary>
    public sealed override async ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.Params?.Uri;

        if (!Addressed(uri, out var parsed))
        {
            // Bozuk adres BULUNAMADI ile aynı cevaba iniyor ve bu bilinçli:
            // "şema hatalı" demek, doğru şemanın ne olduğunu söyleyen bir
            // yankı olurdu ve adres uzayı hakkında bilgi verirdi.
            throw NotFound(uri);
        }

        // KİMLİK BURADA GİRİYOR, VE YALNIZCA BURADA — araç tarafındaki
        // `InvokeAsync` ile aynı yapı, aynı çözücü (`McpCallerScope`).
        var scope = AccessScope.Denied;

        if (RequiresCallerIdentity && McpCallerScope.Resolve(request, out scope) is { } refusal)
        {
            // Kimliksizlik "bulunamadı" DEĞİL: sebep söylenmezse istemci
            // belgeyi aramaya gider ve kimliğinin kaybolduğunu hiç görmez.
            throw new McpProtocolException(refusal.Message, McpErrorCode.InvalidRequest);
        }

        if (ReadsScopedData && scope.IsEmpty)
        {
            // Kimlik var, hiçbir `owner_group`'a çevrilmiyor. Cevap yine
            // "bulunamadı": bu kimlik için o belge GERÇEKTEN yok.
            throw NotFound(uri);
        }

        var body = await ReadBodyAsync(new McpResourceRead(parsed, scope), cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            throw NotFound(uri);
        }

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri!,
                    MimeType = body.MimeType,
                    Text = body.Text,
                },
            ],
        };
    }

    private bool Addressed(string? uri, out McpResourceUri parsed)
    {
        if (!IsAddressedById)
        {
            parsed = new McpResourceUri(Kind, string.Empty);

            return uri is not null && IsMatch(uri);
        }

        if (uri is not null && McpResourceUri.TryParse(uri, out var candidate)
            && string.Equals(candidate.Kind, Kind, StringComparison.Ordinal))
        {
            parsed = candidate;

            return true;
        }

        parsed = new McpResourceUri(Kind, string.Empty);

        return false;
    }

    private static McpProtocolException NotFound(string? uri) =>
        new(
            $"Kaynak bulunamadı: `{uri}`. Adres yanlış olabilir ya da bu kimliğin kapsamı "
            + "onu görmüyor olabilir — ikisi BİLEREK aynı cevaba iniyor: farklı bir kod, "
            + "belgenin var olduğunu doğrulardı.",
            McpErrorCode.ResourceNotFound);

    private string RequireKind() =>
        Surface is McpSurface.Unspecified
            ? throw new InvalidOperationException(
                $"`{Kind}` kaynağı yüzeyini beyan etmiyor. `Unspecified` bir varsayılan değil, "
                + "bir rettir — beyansız bir kaynak sessizce ürün verisi kümesine düşerdi (K6).")
            : Kind;
}

/// <summary>
/// Bir kaynak okumasının girdisi. Araç tarafındaki
/// <see cref="McpToolInvocation"/>'ın ikizi ve <b>aynı sebeple</b> bir sınıf:
/// her <c>struct</c>'ın bir <c>default</c>'u var, yani kapsamsız bir okuma
/// <b>derlenebilirdi</b>.
/// </summary>
/// <param name="Uri">Adreslenen belge. <b>Kapsam taşımıyor</b> (§6.1).</param>
/// <param name="Scope">
/// Çağıranın veri kapsamı (K17) — REST uçlarının geçtiği <b>aynı</b> çözücüden.
/// </param>
public sealed record McpResourceRead(McpResourceUri Uri, AccessScope Scope);

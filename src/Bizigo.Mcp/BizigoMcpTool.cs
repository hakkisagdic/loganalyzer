using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// Bu üründeki <b>her</b> MCP aracının tabanı.
///
/// <para>
/// SDK'nın <c>McpServerTool</c>'undan türüyor ama protokol yüzeyini
/// <b>mühürlüyor</b>: <see cref="ProtocolTool"/> ve <see cref="InvokeAsync"/>
/// <c>sealed</c>. Bir araç kendi <c>CallToolResult</c>'ını kuramıyor, yalnızca
/// <see cref="McpToolResult"/> döndürebiliyor — ve o tip serbest
/// <c>string</c> kabul etmiyor.
/// </para>
///
/// <para>
/// <b>Mühürleme kapının kendisi değil, kapının takılabildiği yer.</b> Gerekçe
/// ve M06'nın ne yapacağı <see cref="McpLogText"/> belgesinde.
/// </para>
///
/// <para>
/// <b>Neden şemalar elle yazılıyor, tipten üretilmiyor.</b> §8: depolama tipi
/// tel sözleşmesi değildir. Yansımayla üretilen bir şema, domain tipine
/// eklenen her alanı kimse karar vermeden protokole sızdırır — MCP'de bunun
/// bedeli ayrıca büyük, çünkü sızan alan doğrudan modelin bağlamına giriyor.
/// Elle yazılan şemanın riski sürüklenme, ve onu uyum kapısı tutuyor: örnek
/// çağrının çıktısı <see cref="OutputSchema"/>'ya <b>uymak zorunda</b>.
/// </para>
/// </summary>
public abstract class BizigoMcpTool : McpServerTool
{
    private Tool? cached;

    /// <summary>Aracın protokoldeki adı — örn. <c>logs.search</c>.</summary>
    public abstract string ToolName { get; }

    /// <summary>
    /// Hangi yüzeye ait. <see cref="McpSurface.Unspecified"/> kayıt sırasında
    /// <b>reddediliyor</b>.
    /// </summary>
    public abstract McpSurface Surface { get; }

    /// <summary>İnsana gösterilen başlık.</summary>
    public abstract string ToolTitle { get; }

    /// <summary>
    /// Modele gösterilen açıklama. <b>Bağlam bütçesinin kalemi</b>: bu metin
    /// her <c>tools/list</c> yanıtında taşınıyor. Uzunluğunun bedeli ölçülüyor
    /// (bkz. <c>McpSchemaBudgetTests</c>).
    /// </summary>
    public abstract string ToolDescription { get; }

    /// <summary>Girdi şeması (JSON Schema, 2020-12).</summary>
    public abstract JsonElement InputSchema { get; }

    /// <summary>
    /// Çıktı şeması (JSON Schema, 2020-12). <b>Zorunlu</b>: şemasız bir araç,
    /// modelin çıktıyı tahmin etmesi demek. Uyum kapısı örnek çağrının buna
    /// uyduğunu sınıyor.
    /// </summary>
    public abstract JsonElement OutputSchema { get; }

    /// <summary>Araç ürünün durumunu değiştiriyor mu.</summary>
    public virtual bool IsReadOnly => true;

    /// <summary>
    /// Araç çağıranın <b>kimliğini</b> istiyor mu (M08).
    ///
    /// <para>
    /// <b>Varsayılan güvenli tarafta:</b> ürün yüzeyindeki her araç kimlik
    /// ister. Kalıp <see cref="McpSurface.Unspecified"/>'ınkiyle aynı —
    /// unutkanlığın bedeli en tehlikeli tarafa düşmemeli. Yüzeyini yazmayı
    /// unutan araç ürün kümesine düşmüyordu; kimlik satırını yazmayı unutan araç
    /// da kimliksiz koşmuyor.
    /// </para>
    ///
    /// <para>
    /// <b>Muafiyet iki bilinçli hareket</b> (§8): burayı ezmek <i>ve</i>
    /// <c>McpIdentityTests</c>'teki gerekçeli listeye + sabit sayıya girmek.
    /// Tek hareketle muaf olunabilseydi liste bir gün sessizce büyürdü.
    /// </para>
    ///
    /// <para>
    /// Simülatör yüzeyi bilerek dışarıda: <c>bizigo-sim</c> ürün verisi
    /// döndürmüyor, simülatör durumu döndürüyor (<see cref="McpSurface"/>).
    /// Kapsamı yine de taşıyor — <see cref="Contracts.AccessScope.Denied"/>
    /// olarak, yani bir gün ürün verisine uzanırsa <b>hiçbir şey</b> görür.
    /// </para>
    /// </summary>
    public virtual bool RequiresCallerIdentity => Surface is McpSurface.Product;

    /// <summary>
    /// SDK'nın araç başına üstveri torbası — ASP.NET'in uç üstverisiyle aynı
    /// fikir (örn. yetkilendirme politikası).
    ///
    /// <para>
    /// M01 burayı <c>sealed</c> yapmadı çünkü M08'in araç başına yetkilendirmeyi
    /// buradan geçirmesi ihtimali vardı. <b>M08 geçirmedi</b> ve sebebi
    /// ölçüldü: SDK üstveriyi <c>tools/list</c> ilanına ve çağrı yönlendirmesine
    /// bakan bir torba olarak taşıyor, oysa kimlik kararının verilmesi gereken
    /// yer <see cref="InvokeAsync"/>'in içi — çağrı başına, istek bağlamı
    /// elimizdeyken. Kapsam <see cref="McpToolInvocation.Scope"/> ile
    /// <b>zorunlu argüman</b> olarak taşınıyor; üstveriye yazılan bir politika
    /// adı, unutulabilen bir dize olurdu.
    /// </para>
    ///
    /// <para>
    /// Yine de boş ve <c>sealed</c> değil: M06'nın redaksiyon kapısı ya da
    /// M07'nin kaynak ilanı buraya bir şey koyabilir. Mühürleme kararı yalnızca
    /// <see cref="ProtocolTool"/> ve <see cref="InvokeAsync"/> için verildi,
    /// çünkü kaçak yol oradan açılıyor.
    /// </para>
    /// </summary>
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>
    /// <b>Uyum kapısının koşturduğu örnek çağrı.</b>
    ///
    /// <para>
    /// <b>Şart:</b> gerçek çağrı yolunun <b>şekillendirme</b> kısmını
    /// kullanmalı — yani <c>McpToolResult.Structured(...)</c>'a gerçek yanıt
    /// tipini vermeli. Elle yazılmış bir JSON sabiti döndüren örnek, şemayı
    /// değil <b>kendini</b> doğrular ve kapı yanlış sebeple yeşil yanar; bu
    /// deponun beş kez adını koyduğu şey.
    /// </para>
    ///
    /// <para>
    /// <b>Şart değil:</b> altyapıya bağlanmak. Kanıtlanması gereken şey
    /// şema ile çıktının uyumu; ClickHouse'un cevabı entegrasyon testinin işi
    /// (§2). Örnek, sabit bir domain nesnesiyle gerçek şekillendirmeyi koşturur.
    /// </para>
    ///
    /// <para>
    /// <b>İptal:</b> uyum kapısı bu metodu <b>zaten iptal edilmiş</b> bir
    /// belirteçle de çağırıyor ve <c>OperationCanceledException</c> bekliyor.
    /// Ölçtüğü şey aracın belirteci gerçekten taşıyıp taşımadığı — planın
    /// §2'sindeki "iptal gerçekten iptal etsin" şartının, ClickHouse
    /// gerektirmeyen ve <b>her</b> araca uygulanabilen hâli.
    /// </para>
    /// </summary>
    public abstract ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken);

    /// <summary>Aracın gerçek işi.</summary>
    protected abstract ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Protokolde ilan edilen hâl. <c>sealed</c>: bir araç kendi ilanını
    /// yeniden yazamıyor, yoksa uyum kapısının denetlediği şey ile sunucunun
    /// ilan ettiği şey ayrışabilirdi.
    /// </summary>
    public sealed override Tool ProtocolTool => cached ??= Build();

    /// <summary>
    /// Protokol çağrısı. <c>sealed</c> — gerekçesi sınıf belgesinde.
    /// </summary>
    public sealed override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = request.Params?.Arguments is { } supplied
            ? new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal)
            : [];

        // M08 · KİMLİK BURADA GİRİYOR, VE YALNIZCA BURADA.
        //
        // Bu metot `sealed`, yani `McpToolInvocation`'ı kuran tek yer burası.
        // Bir aracın kendi kapsamını uydurabilmesi için önce bu satırı geçmesi
        // gerekiyor ve geçemiyor — kapsam derleyicide zorunlu (§8'in
        // `IScopedQuery` kalıbının MCP yüzeyindeki hâli).
        var scope = Contracts.AccessScope.Denied;

        if (RequiresCallerIdentity && McpCallerScope.Resolve(request, out scope) is { } refusal)
        {
            // ARAÇ HİÇ KOŞMUYOR. Kimliksiz bir çağrıya `Denied` verip aracı
            // koşturmak da "kapalı" olurdu ama SESSİZ: boş sonuç "eşleşme yok"
            // diye okunur ve kimliğin kaybolduğunu kimse görmez (§7).
            return ToProtocol(McpToolResult.Failure(refusal));
        }

        McpToolResult result;

        try
        {
            result = await ExecuteAsync(new McpToolInvocation(arguments, scope), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (McpToolArgumentException error)
        {
            // İŞ hatası, protokol hatası değil: istemcinin yanlış argüman
            // göndermesi bağlantı arızası değil. Fırlatıp geçseydik istemci
            // yeniden dener, bekler ve sunucuyu suçlardı.
            result = McpToolResult.Failure(
                new McpToolError(McpToolError.InvalidArgument, error.Message));
        }

        return ToProtocol(result);
    }

    /// <summary>
    /// <see cref="McpToolResult"/> → tel. Tek dönüşüm noktası; araçların
    /// erişimi yok.
    /// </summary>
    internal static CallToolResult ToProtocol(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var content = new List<ContentBlock>
        {
            // Yapısal yükün metin kopyası. BEDELİ VAR — yük iki kez taşınıyor
            // ve bağlam bütçesine iki kez giriyor — ama spesifikasyon
            // `structuredContent` desteklemeyen istemci için `content`'i
            // istiyor. Bedel ölçülüyor: `McpSchemaBudgetTests` bu kopyayı da
            // sayıyor, yani "farkında olmadan ödenen" bir maliyet değil.
            new TextContentBlock { Text = result.Payload.GetRawText() },
        };

        foreach (var log in result.LogText)
        {
            content.Add(new TextContentBlock { Text = log.Text });
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = result.Payload,

            // `false` yerine `null`: spesifikasyon alanı isteğe bağlı tutuyor ve
            // her yanıtta `"isError": false` taşımak bağlam bütçesinden bedava
            // değil.
            IsError = result.IsError ? true : null,
        };
    }

    private Tool Build()
    {
        if (Surface is McpSurface.Unspecified)
        {
            throw new InvalidOperationException(
                $"`{ToolName}` yüzeyini beyan etmiyor. `Unspecified` bir varsayılan değil, bir rettir "
                + "— beyansız bir araç sessizce ürün verisi kümesine düşerdi (K6).");
        }

        return new Tool
        {
            Name = ToolName,
            Title = ToolTitle,
            Description = ToolDescription,
            InputSchema = InputSchema,
            OutputSchema = OutputSchema,
            Annotations = new ToolAnnotations
            {
                Title = ToolTitle,
                ReadOnlyHint = IsReadOnly,
                DestructiveHint = !IsReadOnly,
            },
        };
    }
}

using System.Net;
using System.Text;
using Bizigo.Contracts.Security;
using Bizigo.Rca.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// OpenAI-uyumlu sağlayıcı (T42). Ağ yok: <see cref="SahteHandler"/> gerçek
/// soketin yerine geçiyor (§2).
///
/// <para>
/// <b>Sınanan asıl şey "istek gitti mi" değil, arızanın ne olduğu.</b> RCA'nın
/// tek gerçek riski inandırıcı ama yanlış rapor; <i>"model cevap vermedi"</i>
/// ile <i>"model boş cevap verdi"</i> farklı cümleler ve ikisi de rapora
/// yazılabilmeli. Bir istisna ikisini de aynı şeye — bir yığın izine —
/// çevirirdi.
/// </para>
/// </summary>
public sealed class ModelProviderTests
{
    private sealed class SahteHandler(
        HttpStatusCode status,
        string body,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Son { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Son = request;

            if (request.Content is not null)
            {
                Govde = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        public string? Govde { get; private set; }
    }

    private static async Task<ModelEndpoint> UcAsync(int timeoutSeconds = 120)
    {
        var kapi = new ModelBoundaryGate(new TekAdresCozucu("10.1.2.3"));

        var karar = await kapi.VerifyAsync(new ModelEndpointOptions
        {
            Name = "yerel-ollama",
            BaseUrl = "http://ollama.kurum.local:11434/v1/",
            Model = "qwen3:14b",
            DataBoundary = DataBoundary.Internal,
            TimeoutSeconds = timeoutSeconds,
        });

        return karar.Endpoint!;
    }

    private sealed class TekAdresCozucu(string address) : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse(address)]);
    }

    private static ModelRequest Istek(ModelEndpoint uc) =>
        ModelRequest.Create(
            uc,
            PromptContentLevel.Masked,
            RedactedPrompt.Redact("Sen bir RCA yardımcısısın."),
            RedactedPrompt.Redact("EV-01: edge-rtr-07 sustu.")).Request!;

    private static OpenAiCompatibleModelProvider Saglayici(SahteHandler handler) =>
        new(new HttpClient(handler), NullLogger<OpenAiCompatibleModelProvider>.Instance);

    [Fact]
    public async Task Basarili_cevap_metni_ve_olculen_belirteci_donuyor()
    {
        var handler = new SahteHandler(
            HttpStatusCode.OK,
            """
            {"choices":[{"message":{"role":"assistant","content":"Kök neden: MTU"}}],
             "usage":{"prompt_tokens":812,"completion_tokens":96}}
            """);

        var sonuc = await Saglayici(handler).CompleteAsync(Istek(await UcAsync()), TestContext.Current.CancellationToken);

        Assert.True(sonuc.Ok, sonuc.Failure);
        Assert.Equal("Kök neden: MTU", sonuc.Text);
        Assert.Equal(812, sonuc.PromptTokens);
        Assert.Equal(96, sonuc.CompletionTokens);

        Assert.Equal(
            new Uri("http://ollama.kurum.local:11434/v1/chat/completions"),
            handler.Son!.RequestUri);
    }

    /// <summary>
    /// <b>Bildirilmemiş belirteç sayısı <c>null</c>, sıfır DEĞİL.</b>
    ///
    /// <para>
    /// Sıfır "ölçüldü ve sıfır" demek. Bildirilmemiş bir sayıyı sıfır yazmak,
    /// token bütçesini (T46) sessizce yanıltırdı: bütçe hiç tükenmez, kapı hiç
    /// kapanmaz, ve hiçbir sayaç sebebini söylemez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bildirilmemis_belirtec_sifir_degil_null()
    {
        var handler = new SahteHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"metin"}}]}""");

        var sonuc = await Saglayici(handler).CompleteAsync(Istek(await UcAsync()), TestContext.Current.CancellationToken);

        Assert.True(sonuc.Ok);
        Assert.Null(sonuc.PromptTokens);
        Assert.Null(sonuc.CompletionTokens);
    }

    /// <summary>Arıza bir istisna değil bir sonuç — koşum düşmüyor, sebep kalıyor.</summary>
    [Fact]
    public async Task Uc_hata_donerse_kosum_dusmuyor_sebep_kaliyor()
    {
        var sonuc = await Saglayici(new SahteHandler(HttpStatusCode.ServiceUnavailable, "{}"))
            .CompleteAsync(Istek(await UcAsync()), TestContext.Current.CancellationToken);

        Assert.False(sonuc.Ok);
        Assert.Contains("503", sonuc.Failure, StringComparison.Ordinal);
        Assert.Empty(sonuc.Text);
    }

    /// <summary>
    /// <b>"Gövde okunamadı" ile "model boş cevap verdi" ayrı cümleler.</b>
    /// İkisi aynı çıktıya inseydi, uç sözleşmeyi bozduğu gün bu modelin kalitesi
    /// düşük sanılırdı.
    /// </summary>
    [Fact]
    public async Task Beklenmeyen_govde_bos_cevaptan_ayirt_ediliyor()
    {
        var sonuc = await Saglayici(new SahteHandler(HttpStatusCode.OK, """{"choices":[]}"""))
            .CompleteAsync(Istek(await UcAsync()), TestContext.Current.CancellationToken);

        Assert.False(sonuc.Ok);
        Assert.Contains("gövdeyi döndürmedi", sonuc.Failure, StringComparison.Ordinal);

        var bos = await Saglayici(new SahteHandler(
                HttpStatusCode.OK,
                """{"choices":[{"message":{"role":"assistant","content":""}}]}"""))
            .CompleteAsync(Istek(await UcAsync()), TestContext.Current.CancellationToken);

        Assert.True(bos.Ok);
        Assert.Empty(bos.Text);
    }

    /// <summary>
    /// Zaman aşımı ucun kendi süresine bağlı ve çağıranın iptalinden ayırt
    /// ediliyor: <b>testin geçme sebebinin duvar saatiyle ilgisi</b> yalnızca
    /// burada var ve ölçtüğü şey tam olarak süre.
    /// </summary>
    [Fact]
    public async Task Zaman_asimi_ucun_kendi_suresine_bagli()
    {
        var handler = new SahteHandler(HttpStatusCode.OK, "{}", TimeSpan.FromSeconds(30));

        var sonuc = await Saglayici(handler).CompleteAsync(
            Istek(await UcAsync(timeoutSeconds: 1)),
            TestContext.Current.CancellationToken);

        Assert.False(sonuc.Ok);
        Assert.Contains("cevap vermedi", sonuc.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gövdeye giden şey <b>redakte edilmiş</b> metin. Sağlayıcı ham metni hiç
    /// görmüyor çünkü göremiyor — girdisi <see cref="RedactedPrompt"/>.
    /// </summary>
    [Fact]
    public async Task Govdeye_giden_metin_redakte()
    {
        var handler = new SahteHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");

        var uc = await UcAsync();

        var istek = ModelRequest.Create(
            uc,
            PromptContentLevel.Masked,
            RedactedPrompt.Redact("sistem"),
            RedactedPrompt.Redact(
                "Sep  3 asa-dc-01 : %ASA-5-111008: User 'x' executed the 'snmp-server community S3cret'")).Request!;

        await Saglayici(handler).CompleteAsync(istek, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("S3cret", handler.Govde, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, handler.Govde, StringComparison.Ordinal);
    }
}

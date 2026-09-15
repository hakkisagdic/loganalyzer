using System.Text.Json;
using Bizigo.Contracts.Security;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Schema;

namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// <c>bizigo://parser/{id}</c> — <b>yüklü</b> parser tanımı.
///
/// <para>
/// <c>catalog.parsers</c> aracı hangi parser'ların var olduğunu <b>sayarak</b>
/// söylüyor; bu kaynak birinin <b>tanımını</b> veriyor. Ayrım M07'nin tamamı:
/// bir tanımı okumak için araç çağrısı gerekmiyor, adres yetiyor — ve tanım
/// <c>tools/list</c> bütçesinden hiçbir şey yemiyor.
/// </para>
///
/// <h3>Kapsam uygulanmıyor — ve gerekçe ikinci kez yazılmadı</h3>
///
/// <para>
/// <see cref="ReadsScopedData"/> <see langword="false"/>. Gerekçe REST tarafında
/// zaten yazılı (<c>/v1/parsers</c>): <i>"katalog veri değil, yapılandırma:
/// kapsam filtresi uygulanmıyor. Bir ekibin hangi parser'ların var olduğunu
/// görmesi kimsenin logunu görmesi anlamına gelmiyor."</i> Aynı cümle
/// <c>CatalogParsersTool</c>'da da taşınıyor; burada üçüncü kez yazmak yerine
/// atıf yapılıyor.
/// </para>
///
/// <para>
/// <b>Kimlik yine şart</b> — <see cref="BizigoMcpResource.RequiresCallerIdentity"/>
/// varsayılanı değişmiyor. Muafiyet olan tek şey <i>boş kapsamın reddi</i>.
/// </para>
///
/// <h3>Diskteki bayt değil, KOŞAN tanım</h3>
///
/// <para>
/// Gövde <c>catalog/parsers/*.yaml</c>'ın baytları <b>değil</b>: yüklenmiş
/// <c>ParserDefinition</c>'ın JSON serileştirmesi. Ham YAML bellekte durmuyor ve
/// diskten okumak <b>ikinci bir gerçek kaynak</b> açardı — dosya değişip
/// katalog henüz yeniden yüklenmemişken kaynak, koşmayan bir tanımı
/// <i>koşuyor</i> gibi gösterirdi. Bir ajanın sorduğu soru <i>"bu satırı ne
/// ayrıştırıyor"</i>, ve cevabı bellekteki tanım.
/// </para>
///
/// <para>
/// Bu yüzden <c>mimeType</c> <see cref="McpResourceMimeTypes.Json"/>, YAML
/// değil: YAML demek dosyanın kendisini vaat etmek olurdu.
/// </para>
/// </summary>
public sealed class ParserDefinitionResource(ParserCatalog catalog) : ProductResource
{
    /// <summary>Adresin tür segmenti.</summary>
    public const string ResourceKind = "parser";

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <inheritdoc/>
    public override string Kind => ResourceKind;

    /// <inheritdoc/>
    public override string ResourceTitle => "Parser tanımı";

    /// <inheritdoc/>
    public override string ResourceDescription =>
        "Yüklü bir parser'ın tanımı: grok desenleri, alan eşlemeleri, tarih adımı, "
        + "testleri. Adres `bizigo://parser/{id}`; kimlikler `catalog.parsers` "
        + "aracında. Diskteki dosya değil, KOŞAN tanım.";

    /// <inheritdoc/>
    public override string BodyMimeType => McpResourceMimeTypes.Json;

    /// <summary>
    /// Katalog yapılandırma; kapsam filtresi uygulanmıyor. Gerekçe sınıf
    /// belgesinde ve REST ucunda.
    /// </summary>
    public override bool ReadsScopedData => false;

    /// <inheritdoc/>
    public override ValueTask<McpResourceBody> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek GERÇEK şekillendirme yolundan geçiyor: aynı `Body` metodu, aynı
        // serileştirici, aynı redaksiyon kapısı. Elle yazılmış bir JSON sabiti
        // döndürmek, kapının kendini doğrulaması olurdu.
        var snapshot = catalog.Current;
        var sample = snapshot.Parsers.Count > 0 ? snapshot.Parsers[0].Definition : null;

        return ValueTask.FromResult(Body(sample));
    }

    /// <inheritdoc/>
    protected internal override ValueTask<McpResourceBody?> ReadScopedAsync(
        McpResourceRead read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();

        // Anlık görüntü BİR KEZ okunuyor — `catalog.Current` her erişimde
        // değişebilir (yeniden yükleme atomik ama araya girebilir).
        var snapshot = catalog.Current;

        var parser = snapshot.Parsers.FirstOrDefault(p =>
            string.Equals(p.Definition.Metadata.Id, read.Uri.Id, StringComparison.Ordinal));

        // Bulunamadı = `null`. Taban onu `ResourceNotFound`'a çeviriyor; burada
        // bir mesaj yazmak, adres uzayı hakkında bilgi veren bir yankı olurdu.
        return ValueTask.FromResult(parser is null ? null : Body(parser.Definition));
    }

    /// <summary>
    /// Gövdeyi kurar — <b>redaksiyon kapısından</b>.
    ///
    /// <para>
    /// Parser tanımı bir sır taşımaması gereken bir belge, ama <b>taşıdığı
    /// ölçülmedi</b>: YAML'ı kurum içinden geliyor ve bir <c>Authorization</c>
    /// başlığı örneği ya da bir test satırında gerçek bir jeton bulunması
    /// mümkün. Kanalın kapısı tam olarak bu yüzden bir <i>imkân</i> değil bir
    /// <b>şart</b>: "bu belgede sır olmaz" bir varsayım, ve varsayımın bedeli
    /// modelin bağlamına inen bir sır.
    /// </para>
    /// </summary>
    private static McpResourceBody Body(ParserDefinition? definition) =>
        McpResourceBody.Of(
            RedactedPrompt.Redact(JsonSerializer.Serialize(definition, BodyOptions)),
            McpResourceMimeTypes.Json);
}

using Bizigo.Ingest.Discovery;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Sıcak yolun iki adımını testlerde de <b>aynı sırayla</b> koşturur:
/// önce imza (K35, her olayda), sonra etiketleme.
///
/// <para>
/// Ayrı bir yardımcı olmasının sebebi, testlerin imzayı elle uydurmasını
/// engellemek. <c>ParsingSink</c> imzayı <see cref="MaskCatalog.Compute"/>'dan
/// alıyor; test başka bir yerden alsaydı, keşif yolunun gerçekte gördüğünden
/// farklı bir girdiyi sınıyor olurduk.
/// </para>
/// </summary>
internal static class AnnotatorTestExtensions
{
    public static string Annotate(
        this ITemplateAnnotator annotator,
        MaskCatalog masks,
        string sourceClass,
        string body,
        bool parseFailed)
    {
        ArgumentNullException.ThrowIfNull(annotator);
        ArgumentNullException.ThrowIfNull(masks);

        return annotator.Annotate(sourceClass, body, masks.Compute(body), parseFailed);
    }
}

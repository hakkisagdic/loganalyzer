using System.Globalization;
using System.Text;

using Bizigo.Contracts.Security;
using Bizigo.Rca.Models;
using Bizigo.ScenarioPlugin;

namespace Bizigo.Rca.Reasoning;

/// <summary>
/// Bir adımın modele giden <b>hazır</b> prompt'u.
///
/// <para>
/// <b>Yapıcısı <c>private</c>, tek üreticisi <see cref="ScenarioPromptBuilder"/>.</b>
/// Zincirin üçüncü halkası bu: T41 redaksiyon kapısını <see cref="RedactedPrompt"/>
/// tipine bağladı, T42 <see cref="ModelRequest"/>'i o tipten girdi almaya
/// zorladı, T44 da prompt'u <b>kuran</b> tarafı aynı çizgiye çekti. Üçü
/// birlikte şunu veriyor: kanıt metnini modele taşıyan bir yol <b>ancak</b>
/// redaksiyon kapısından geçerek yazılabiliyor, ve bunu bir yorum değil
/// derleyici tutuyor.
/// </para>
///
/// <para>
/// Alternatifi kurucunun <c>string</c> alması ya da <c>string</c> döndürmesiydi.
/// O zaman kapı yine bir <b>çağrı alışkanlığı</b> olurdu — T41 ve T42 aynı
/// kararı aynı gerekçeyle vermişti ve gerekçe her seferinde aynı: unutulduğu
/// gün hiçbir şey kırılmaz.
/// </para>
/// </summary>
public sealed class ScenarioStepPrompt
{
    private ScenarioStepPrompt(string stepId, int attempt, RedactedPrompt system, RedactedPrompt user)
    {
        StepId = stepId;
        Attempt = attempt;
        System = system;
        User = user;
    }

    public string StepId { get; }

    /// <summary>Kaçıncı koşum — 1 ya da 2. Kayda giriyor: ikinci koşum belirteç harcadı.</summary>
    public int Attempt { get; }

    public RedactedPrompt System { get; }

    public RedactedPrompt User { get; }

    /// <summary>
    /// <b>Yalnızca kurucu üretebilir.</b> <c>internal</c> değil <c>private</c>
    /// olmasının bedeli fabrikanın aynı dosyada durması; kazancı, aynı
    /// derlemedeki bir başka sınıfın bile kapıyı atlayamaması.
    /// </summary>
    private static ScenarioStepPrompt Create(string stepId, int attempt, RedactedPrompt system, RedactedPrompt user) =>
        new(stepId, attempt, system, user);

    /// <summary>
    /// Adım prompt'unu kurar — <b>girdisi tipli, çıktısı redakte</b>.
    ///
    /// <para>
    /// Hiçbir parametresi prompt metni değil: adım YAML'dan, görüş alanı kanıt
    /// paketinden, şema motordan geliyor. Yani <i>"kapıyı atlayıp şu metni
    /// gönder"</i> diyen bir çağrı yazılamıyor — yazılabilseydi kapı bir
    /// alışkanlığa dönerdi.
    /// </para>
    /// </summary>
    public static ScenarioStepPrompt Build(
        ScenarioStep step,
        IScenarioOutputSchema schema,
        StepEvidenceView view,
        PromptContentLevel level)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(view);

        return Create(
            step.Id,
            attempt: 1,
            RedactedPrompt.Redact(ScenarioPromptBuilder.SystemText(step, schema)),
            RedactedPrompt.Redact(ScenarioPromptBuilder.UserText(step, schema, view, level, violations: [])));
    }

    /// <summary>
    /// Yeniden deneme prompt'u — <b>ihlali adlandırarak</b>.
    ///
    /// <para>
    /// Aynı adımı aynı metinle tekrar sormak bir yeniden deneme değil bir
    /// kumar: model neyi yanlış yaptığını bilmiyorsa aynı yanlışı yapması
    /// beklenen davranış. İhlal listesi prompt'a giriyor, ve o da
    /// <b>redaksiyon kapısından</b> geçiyor — model çıktısından türeyen bir
    /// metni kapısız geri göndermek, tabanın yarısını açık bırakmak olurdu.
    /// </para>
    /// </summary>
    public static ScenarioStepPrompt Retry(
        ScenarioStep step,
        IScenarioOutputSchema schema,
        StepEvidenceView view,
        PromptContentLevel level,
        IReadOnlyList<string> violations)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(violations);

        return Create(
            step.Id,
            attempt: 2,
            RedactedPrompt.Redact(ScenarioPromptBuilder.SystemText(step, schema)),
            RedactedPrompt.Redact(ScenarioPromptBuilder.UserText(step, schema, view, level, violations)));
    }
}

/// <summary>
/// Prompt metnini <b>kuran</b> taraf. Dışarı hiçbir zaman <c>string</c>
/// vermiyor: gövdeler <c>internal</c> ve tek tüketicileri
/// <see cref="ScenarioStepPrompt"/>'un iki fabrikası.
/// </summary>
public static class ScenarioPromptBuilder
{
    internal static string SystemText(ScenarioStep step, IScenarioOutputSchema schema)
    {
        var text = new StringBuilder();

        text.AppendLine("Ağ ve altyapı loglarından kök neden analizi yapan bir yardımcısın.");
        text.AppendLine();

        // Kapı prompt'ta RİCA EDİLMİYOR, motorda zorlanıyor (RCA §8). Buradaki
        // cümleler modelin işini kolaylaştırmak için; kapı onlara güvenmiyor.
        text.AppendLine("Kurallar:");
        text.AppendLine("1. Yalnızca aşağıda listelenen kanıt kimliklerine atıf yapabilirsin.");
        text.AppendLine("   Listede olmayan bir kimlik uydurmak bu adımın reddedilmesine yol açar.");
        text.AppendLine("2. Yazdığın her cümle, dayandığı kanıt kimliğini köşeli parantez içinde");
        text.AppendLine("   taşımalı — örn. `... [EV-11]`. Kimliği olmayan cümle rapora GİRMEZ.");
        text.AppendLine("3. Yalnızca JSON üret, açıklama yazma.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Beklenen çıktı şekli ({schema.Name}):");
        text.AppendLine(schema.Shape);

        if (step.Output.MaxItems is { } max)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"En fazla {max} kayıt üret.");
        }

        return text.ToString();
    }

    internal static string UserText(
        ScenarioStep step,
        IScenarioOutputSchema schema,
        StepEvidenceView view,
        PromptContentLevel level,
        IReadOnlyList<string> violations)
    {
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"Görev: {step.Task}");
        text.AppendLine();

        if (view.Summaries.Count > 0)
        {
            text.AppendLine("Kanıt özetleri (kimlik taşımıyorlar, atıf yapılamaz):");

            foreach (var summary in view.Summaries)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {summary}");
            }

            text.AppendLine();
        }

        if (view.Items.Count > 0)
        {
            text.AppendLine("Atıf yapabileceğin kanıt satırları:");

            foreach (var item in view.Items)
            {
                // `summary` düzeyinde satırın özeti gidiyor; `masked`/`raw`
                // düzeylerinde sağlayıcı ve zaman da ekleniyor. Üçünün de
                // ALTINDA aynı taban var: metin `RedactedPrompt.Redact`'ten
                // geçiyor ve düzey o tabanı değiştiremiyor (RCA §2.1).
                text.Append(CultureInfo.InvariantCulture, $"- {item.Id}: {item.Summary}");

                if (level != PromptContentLevel.Summary)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $" (kaynak: {item.ProviderId}, zaman: {item.Timestamp:O})");
                }

                text.AppendLine();
            }

            text.AppendLine();
        }

        if (view.Upstream.Count > 0)
        {
            text.AppendLine("Önceki adımın çıktısı:");

            foreach (var line in view.Upstream)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {line}");
            }

            text.AppendLine();
        }

        if (view.UnknownInputs.Count > 0)
        {
            // Modele de söyleniyor: bu yoldan kimlik gelmedi. Sessiz bırakmak,
            // modelin olmayan bir listeye atıf yapmasını davet ederdi.
            text.AppendLine(
                "Not: şu girdi yolları bu motorda karşılıksız kaldı ve kimlik getirmedi — " +
                string.Join(", ", view.UnknownInputs));
            text.AppendLine();
        }

        if (violations.Count > 0)
        {
            text.AppendLine("ÖNCEKİ DENEMEN REDDEDİLDİ. Sebepleri:");

            foreach (var violation in violations)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {violation}");
            }

            text.AppendLine();
            text.AppendLine("Aynı görevi, yalnızca yukarıda listelenen kimlikleri kullanarak tekrar yap.");
            text.AppendLine();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Yanıtı `{schema.Name}` şemasında JSON olarak ver.");

        return text.ToString();
    }
}

using Bizigo.Contracts.Security;

namespace Bizigo.Rca.Models;

/// <summary>
/// Prompt'a <b>ne kadar</b> bağlam girdiği (RCA §2.1).
///
/// <para>
/// <b>Bu bir sınır değil ayarlanabilir bir parametre</b> — kararı §2.1 verdi ve
/// gerekçesi ölçülebilirlik: içeriği kısmak halüsinasyonu azaltmıyor
/// <b>artırıyor</b>, ve hangisinin işe yaradığını ölçüm söylüyor (atılan cümle
/// sayılıyor, kanıt paketi saklanıyor, aynı paket üzerinde iki düzey
/// karşılaştırılabiliyor).
/// </para>
///
/// <para>
/// <b>Düzey sağlayıcı türüne bağlı DEĞİL</b> (T42 kararı). Bağlansaydı yerel
/// bir Ollama ile kurum içi bir GPU kümesi farklı düzeylere sahip olurdu —
/// oysa K6'nın baktığı eksende ikisi aynı yerde. Sağlayıcının sorduğu soru
/// <see cref="DataBoundary"/>; düzeyin sorduğu soru "ne kadar bağlam".
/// İki soru, iki eksen.
/// </para>
///
/// <para>
/// <b>Üçünün de altında aynı taban var ve o ayarlanabilir değil:</b> hangi
/// düzey seçilirse seçilsin metin T41'in redaksiyon kapısından geçiyor.
/// <see cref="ModelRequest"/> bunu tip düzeyinde tutuyor — düzey bir
/// <c>string</c> gönderemiyor.
/// </para>
/// </summary>
public enum PromptContentLevel
{
    /// <summary>Yalnızca kanıt özetleri — <i>"12 cihaz sustu, 3 yeni imza"</i>.</summary>
    Summary = 0,

    /// <summary>Kanıt özetleri + redakte edilmiş log satırları.</summary>
    Masked = 1,

    /// <summary>
    /// Kanıt özetleri + log satırlarının tamamı (yine redakte).
    ///
    /// <para>
    /// <b>Ayrı bir bilinçli hareket istiyor:</b> yapılandırmada
    /// <c>AllowRawContentLevel</c> açılmadan seçilemiyor. Gerekçe §2.1'in
    /// sıralaması — <c>raw</c> tabanı ölçülene kadar kapalıydı, T41 tabanı
    /// ölçtü, ve açılışın <b>görünür bir hareketle</b> olması bu sıralamanın
    /// kaydı. Bir bayrak değil bir imza.
    /// </para>
    /// </summary>
    Raw = 2,
}

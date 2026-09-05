namespace Bizigo.Mcp;

/// <summary>
/// <b>Uyduğumuz MCP spesifikasyon revizyonu.</b>
///
/// <para>
/// Planın §9'u <i>"MCP 2.0'ın hangi revizyonu"</i> diye soruyordu ve cevabı
/// ölçüldü: <b>spesifikasyonun "2.0" diye bir sürümü yok.</b> Yayınlanmış
/// revizyonların tamamı tarih damgalı — <c>2024-11-05</c>, <c>2025-03-26</c>,
/// <c>2025-06-18</c>, <c>2025-11-25</c>, <c>2026-07-28</c> — ve hiçbiri "2.0"
/// adını taşımıyor. "MCP 2.0" ifadesi büyük olasılıkla C# SDK'sının sürümüne
/// (<c>ModelContextProtocol</c> 2.x) bakıyordu; o ayrı bir şey ve
/// <c>Directory.Packages.props</c>'ta duruyor.
/// </para>
///
/// <para>
/// <b>Neden bir sabit, neden "en güncel" değil.</b> <i>"En güncel"</i> bir
/// hedef değil bir kaymadır: bugün neye uyduğumuzu söylemiyor, yarın haber
/// vermeden değişiyor ve sözleşme testinin neye karşı koştuğu belirsizleşiyor.
/// Sabit yazınca soru ölçülebilir hâle geliyor — <c>McpComplianceTests</c>
/// karşılığındaki bekçi, SDK'nın kendi varsayılanı bu sabitten ayrıştığı gün
/// <b>kırmızı yanıyor</b>. Yükseltmek serbest; <b>sessizce</b> yükselmek değil.
/// </para>
/// </summary>
public static class McpRevision
{
    /// <summary>
    /// Sunucunun ilan ettiği revizyon. <b>2026-07-28</b> — bugünün en yeni
    /// <i>stable</i> revizyonu (yayın: 2026-07-28).
    ///
    /// <para>
    /// Eski istemciyi kaybetmiyoruz: sürüm anlaşması (<c>initialize</c>)
    /// istemci daha eskisini isterse ortak bir revizyona iniyor. Yani sabit
    /// <b>tavanı</b> belirliyor, tabanı değil.
    /// </para>
    /// </summary>
    public const string Supported = "2026-07-28";

    /// <summary>
    /// Bir önceki stable revizyon. Burada durmasının tek sebebi anlaşma
    /// bekçisi: <b>indirebildiğimizi</b> ölçmek istiyoruz. Yalnızca tavanı
    /// sınayan bir test, anlaşmanın çalıştığını değil yalnızca tavanın
    /// doğru olduğunu gösterirdi.
    /// </summary>
    public const string PreviousStable = "2025-11-25";
}

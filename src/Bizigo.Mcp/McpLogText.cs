namespace Bizigo.Mcp;

/// <summary>
/// <b>Log metninin MCP'ye girebildiği tek taşıyıcı — ve bugün onu üreten
/// hiçbir ürün kodu yok.</b>
///
/// <para>
/// <b>Bu boşluk bir eksik değil, bir menteşe.</b> Sonraki kişi buraya bakıp
/// "unutulmuş" sanmasın diye gerekçe uzun yazıldı.
/// </para>
///
/// <para>
/// MCP bir <b>model yüzeyi</b>: bir araç çağrısının dönüş değeri doğrudan
/// modelin bağlamına giriyor. Bu, depodaki iki kapıyı dolaşabilecek bir yol
/// açıyor — T41'in <c>RedactedPrompt</c>'u (yapıcısı <c>private</c>, yalnızca
/// redaksiyon kapısından çıkıyor) ve T42'nin <c>ModelEndpoint</c>'i (K6'nın ağ
/// sınırı). Araç arayüzü <c>string</c> döndürseydi kapı bir <i>çağrı
/// alışkanlığına</i> inerdi: "buraya redaksiyon uygulamayı unutma". Bu depoda
/// hatırlamaya dayanan mekanizmanın kaç kez kaybettiği ölçüldü.
/// </para>
///
/// <para>
/// <b>Neden kapının kendisi burada değil.</b> Planın §8'i tersini de yasaklıyor:
/// <i>tüketicisi olmayan bir tip tahmindir</i>. Redaksiyon kapısı (M06) M04/M05
/// araçları yazıldıktan <b>sonra</b> takılıyor — bugün onu tüketen tek bir araç
/// yok, dolayısıyla <c>RedactedPrompt</c> bağını şimdi yazmak, hangi şeklin
/// gerekeceğini bilmeden bir sözleşme uydurmak olurdu.
/// </para>
///
/// <para>
/// <b>M01'in bıraktığı şey ikisinin arası:</b> serbest <c>string</c> dönüşü
/// <b>derlenmiyor</b> — <c>McpToolResult</c> yalnızca yapısal yükü ve bu tipi
/// kabul ediyor — ama bu tipin üretim yolu <b>boş</b>. M06 geldiğinde
/// değiştirilecek olan tek şey aşağıdaki fabrikanın <b>parametre tipi</b>:
/// <c>string</c> yerine <c>RedactedPrompt</c>. O gün her araca tek tek bakmak
/// gerekmiyor; tek bir imza değişiyor ve kapıyı atlayan her çağrı yeri
/// <b>derleme hatası</b> veriyor.
/// </para>
///
/// <para>
/// <b>Menteşenin bugünkü zayıflığı, dürüstlük gereği yazılı:</b> fabrika
/// <c>internal</c>, yani <c>Bizigo.Mcp</c> derlemesinin içinden herhangi bir
/// <c>string</c> ile çağrılabilir. Araçlar bu derlemenin dışında yaşadığı için
/// bugün ulaşamıyorlar, ama bu bir <b>yerleşim</b> güvencesi, tip güvencesi
/// değil. M06'nın ikinci işi bunu bir mimari bekçiye bağlamak: <i>bu fabrikanın
/// tek çağıranı redaksiyon kapısı olmalı.</i>
/// </para>
/// </summary>
public sealed class McpLogText
{
    private McpLogText(string text) => Text = text;

    /// <summary>Modelin bağlamına girecek metin.</summary>
    public string Text { get; }

    /// <summary>
    /// <b>M06'nın takılacağı yer.</b> Bugün ürün tarafında çağıranı yok;
    /// uyum kapısı menteşenin çalıştığını sınıyor (bkz. sınıf belgesi).
    ///
    /// <para>
    /// M06 bu imzayı <c>FromRedacted(RedactedPrompt redacted)</c> hâline
    /// getirecek. Parametre tipini değiştirmek, log içeriği döndüren her yolu
    /// derleyicide redaksiyon kapısına bağlamak demek.
    /// </para>
    /// </summary>
    internal static McpLogText FromRedacted(string redacted) =>
        new(redacted ?? throw new ArgumentNullException(nameof(redacted)));
}

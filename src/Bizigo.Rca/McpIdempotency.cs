using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Rca;

/// <summary>
/// <b>Ajan tetiklemesinin idempotency anahtarı — sunucu türetiyor, çağıran
/// vermiyor.</b>
///
/// <para>
/// <b>Neden çağıran vermiyor.</b> REST tarafında anahtar istemciden geliyor
/// (<c>POST /v1/rca</c>, <c>Idempotency-Key</c> başlığı) ve orada bu doğru:
/// çağıran bir dış sistem, kendi tekrar denemesini kendi tanıyor. MCP'de
/// çağıran bir <b>model</b>, ve anahtarı ona sordurmak idempotency'yi tam da
/// korunmak istenen tarafa vermek olurdu:
/// </para>
///
/// <list type="bullet">
/// <item>Her denemede <b>yeni</b> anahtar üreten model kotayı defalarca yer.</item>
/// <item>Aynı anahtarı <b>ısrarla</b> üreten model farklı bir tetiklemeyi
/// yanlışlıkla bastırır.</item>
/// </list>
///
/// <para>
/// İkisi de sessiz — hata yok, sayaç yok, belirti yok (§7).
/// </para>
///
/// <para>
/// <b>Neden oturumdan türemiyor — ÖLÇÜLDÜ.</b> İlk öneri
/// <c>McpSession.SessionId</c>'ydi. Akışlanabilir HTTP'nin kipi bizde
/// <b>durumsuz</b> ve bu bir tercih değil, çivilediğimiz revizyonun
/// varsayılanı: SDK durumlu kipi <i>"geriye dönük uyum kaçış kapısı"</i> diye
/// işaretliyor ve kullanımdan kaldırma tanılamasıyla birlikte veriyor.
/// Durumsuz kipte her istek <b>kendi oturumu</b>, yani <c>SessionId</c> her
/// yeniden denemede değişiyor — eksik değil, <b>yanlış</b>. Oturumdan türeyen
/// bir anahtar, kaçınmak istediğimiz "her deneme yeni" davranışını kaza eseri
/// üretirdi.
/// </para>
///
/// <para>
/// <b>Neden yalnızca kimlik taşıyan alanlar.</b> Argüman torbasının tamamı
/// hash'e girseydi model anlamsız bir alan ekleyip anahtarı değiştirebilirdi —
/// kotayı yeme yolunun ta kendisi. Hash'e giren küme <b>dar ve yazılı</b>:
/// çağıran, kapsam, pencere. Yeni bir argüman alanı eklendiğinde onun hash'e
/// girip girmeyeceği <b>bilinçli bir karar</b> olmalı, varsayılan değil;
/// <c>McpIdempotencyTests</c> bunu tutuyor.
/// </para>
///
/// <para>
/// <b>Kısıtın harfi değil ruhu.</b> Koordinatörün çividiği kısıt
/// <i>"anahtar modelin uydurabileceği bir yerden gelmemeli"</i>ydi. Model bu
/// anahtarı doğrudan uyduramıyor ama <b>girdilerini</b> seçiyor — farklı bir
/// pencere isteyerek farklı bir anahtar üretebilir. Bu bir kaçak değil: o
/// zaman <b>farklı bir RCA</b> istemiş oluyor, aynı RCA'yı iki kez değil.
/// Engellenmek istenen şey buydu.
/// </para>
/// </summary>
public static class McpIdempotency
{
    /// <summary>
    /// Anahtarın öneki. <c>rca_runs</c>'a bakan biri anahtarın nereden geldiğini
    /// görebilsin diye — <c>Source</c> zaten <c>agent</c> diyor, ama anahtarın
    /// kendisi de kendi kaynağını taşıyor.
    /// </summary>
    public const string Prefix = "mcp";

    /// <summary>
    /// Ayırıcı. Parçalar zaten karma içine giriyor, ama önekle karma arasında
    /// okunabilir bir sınır kalsın diye.
    /// </summary>
    private const char Separator = ':';

    /// <summary>
    /// <b>Kimlik taşıyan alanlardan</b> anahtar üretir.
    /// </summary>
    /// <param name="subject">Çağıranın Keycloak öznesi.</param>
    /// <param name="ownerGroups">Talebin kapsamı.</param>
    /// <param name="from">Pencere başlangıcı.</param>
    /// <param name="to">Pencere sonu.</param>
    public static string KeyFor(
        string subject,
        IEnumerable<string> ownerGroups,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(ownerGroups);

        // Kapsam `RcaTriggerKey.Scope`'tan geçiyor: sıralama ve tekilleştirme
        // orada yazılı ve ikinci bir kanonikleştirme yazmak, iki gösterimin
        // sessizce ayrışabileceği bir yer daha açardı (§9).
        var scope = RcaTriggerKey.Scope(ownerGroups);

        // Zaman `O` biçiminde: dilim ve kesir korunuyor. Yerelleştirilmiş bir
        // biçim iki makinede iki anahtar üretirdi.
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{subject}{Separator}{scope}{Separator}{from:O}{Separator}{to:O}");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{Separator}{subject}{Separator}{Convert.ToHexStringLower(digest)}");
    }
}

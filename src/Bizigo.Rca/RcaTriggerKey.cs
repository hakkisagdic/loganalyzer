using System.Globalization;
using Bizigo.ControlPlane;

namespace Bizigo.Rca;

/// <summary>
/// Tetikleyici anahtarları — <b>saf fonksiyonlar</b> (T45, RCA §5).
///
/// <para>
/// <b>İki anahtar var ve aynı şeyi sormuyorlar.</b> Bu ayrım ticket'ın taşıyıcı
/// kararı: aynı üçlüye iki farklı soru sormak, bu depoda defalarca bedeli
/// ödenmiş bir hata sınıfı.
/// </para>
///
/// <list type="table">
///   <item>
///     <term><see cref="Debounce"/></term>
///     <description>"Bu tetikleyici <b>son on dakikada</b> zaten koştu mu?"
///     Pencere kovası anahtarın parçası, çünkü debounce zaten zaman hakkında.</description>
///   </item>
///   <item>
///     <term><see cref="Lineage"/></term>
///     <description>"Bu tetikleyici <b>kendi atalarımın arasında</b> var mı?"
///     Pencere kovası <b>yok</b>.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>Soyağacı anahtarında pencerenin olmaması bir sapma ve bilinçli.</b> RCA §5
/// ata kontrolünü <c>(rule_id, scope, pencere)</c> üçlüsüyle yazıyor. Pencere
/// dahil edilirse A → B → A zinciri kova sınırını aştığı anda kontrolden kaçar —
/// ve zincirler tam da <b>zaman aldıkları için</b> kova sınırını aşarlar: B'nin
/// koşup bulgu üretmesi dakikalar sürüyor. Yani belgenin yazdığı hâliyle döngü
/// koruması, korumak istediği durumun çoğunda çalışmazdı.
/// </para>
/// </summary>
public static class RcaTriggerKey
{
    /// <summary>Debounce penceresi (RCA §5: 10 dk).</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ayırıcı olarak dikey çizgi. Kimlik ve kapsam adlarında geçmesi beklenmiyor
    /// ama geçerse anahtar sessizce başka bir anahtara eşit olurdu; bu yüzden
    /// parçalar <see cref="Escape"/>'ten geçiyor.
    /// </summary>
    private const char Separator = '|';

    /// <summary>
    /// Kaynak + kimlik + kapsam. İki anahtarın da ortak gövdesi.
    /// </summary>
    private static string Body(RcaTriggerSource source, string identity, string ownerGroup) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{source.ToString().ToLowerInvariant()}{Separator}{Escape(identity)}{Separator}{Escape(ownerGroup)}");

    /// <summary>
    /// Debounce anahtarı: gövde + <b>pencere kovası</b>.
    ///
    /// <para>
    /// Kova, epoch'tan itibaren sabit uzunlukta dilimler. Kayan pencere değil:
    /// kayan olsaydı "son 10 dakika" her talepte farklı bir aralık olurdu ve iki
    /// eşzamanlı talep birbirini görmeden geçebilirdi. Sabit kova, aynı dilime
    /// düşen her talebin aynı anahtarı üretmesini garanti ediyor.
    /// </para>
    ///
    /// <para>
    /// Bedeli açık: kova sınırının hemen iki yanına düşen iki talep debounce
    /// edilmiyor. Alarm fırtınası senaryosunda bu, 500 alarmın 1 yerine en fazla
    /// 2 RCA üretmesi demek — kabul edilebilir ve <b>öngörülebilir</b>.
    /// </para>
    /// </summary>
    public static string Debounce(
        RcaTriggerSource source,
        string identity,
        string ownerGroup,
        DateTimeOffset at,
        TimeSpan? window = null)
    {
        var size = (long)(window ?? DebounceWindow).TotalSeconds;

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Debounce penceresi sıfırdan büyük olmalı.");
        }

        var seconds = at.ToUnixTimeSeconds();
        var bucket = seconds - (seconds % size);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Body(source, identity, ownerGroup)}{Separator}{bucket}");
    }

    /// <summary>
    /// Soyağacı anahtarı: gövde, <b>pencere yok</b>.
    ///
    /// <para>
    /// Aynı tetikleyicinin zincirde ikinci kez görünmesi, aradan ne kadar zaman
    /// geçtiğinden bağımsız olarak bir döngüdür.
    /// </para>
    /// </summary>
    public static string Lineage(RcaTriggerSource source, string identity, string ownerGroup) =>
        Body(source, identity, ownerGroup);

    /// <summary>
    /// Kapsamın kanonik hâli — anahtarın "scope" parçası.
    ///
    /// <para>
    /// Sıralı ve tekilleştirilmiş: aynı grup kümesi farklı sırada geldiğinde
    /// farklı anahtar üretseydi debounce sessizce kaçar, ata kontrolü sessizce
    /// bir döngüyü görmezdi. İkisi de hata vermeden yanlış davranırdı.
    /// </para>
    /// </summary>
    public static string Scope(IEnumerable<string> ownerGroups)
    {
        ArgumentNullException.ThrowIfNull(ownerGroups);

        var groups = ownerGroups
            .Select(static g => g.Trim())
            .Where(static g => g.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Boş kapsam "her şey" DEĞİL; ayrı bir anahtar değeri alıyor ki kapsamsız
        // bir talep kapsamlı bir talebi debounce edemesin (K17).
        return groups.Length == 0 ? "*" : string.Join(",", groups);
    }

    /// <summary>
    /// Ayırıcıyı kaçırır. Kaçırılmasaydı <c>a|b</c> kimliği ile <c>a</c> kimliği +
    /// <c>b</c> kapsamı aynı anahtarı üretirdi — ve o çakışma bir döngüyü
    /// sessizce görünmez ya da meşru bir koşumu sessizce reddedilmiş yapardı.
    /// </summary>
    private static string Escape(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Replace("|", "%7C", StringComparison.Ordinal);
}

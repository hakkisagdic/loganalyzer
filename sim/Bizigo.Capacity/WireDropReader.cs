using System.Globalization;
using System.Runtime.InteropServices;

namespace Bizigo.Capacity;

/// <summary>
/// <b>Tel katmanı — OS soket sayaçları.</b> Ham soket <b>yok</b>: paket yakalama
/// bu düzeneği Linux çekirdek yüzeyine (<c>AF_PACKET</c>, <c>CAP_NET_RAW</c>)
/// çivilerdi, oysa kurulum hedefi Linux <b>ve</b> Windows.
///
/// <h3>Platform okuyucusu yoksa uydurma sayı YOK</h3>
///
/// <para>
/// Bu sınıfın en önemli davranışı: tanımadığı bir platformda <b>sıfır dönmüyor</b>,
/// <see cref="LedgerReading.Limited"/> dönüyor. Gerekçe bu depoda ölçülmüş bir
/// olay — <c>tools/machine-resources.sh</c> her okuyucusunu BSD/macOS'a özgü
/// yazmıştı ve Linux'ta bellek okuyucusu <c>100</c>, disk okuyucusu <c>0</c>
/// basıyordu: <b>ikisi sessiz, ikisinin yönü zıt</b>, ve ikisi de bir ölçüm
/// değil (<c>tools/README.md</c>). Sıfır dönen bir düşürme sayacı da tam olarak
/// aynı şey olurdu — ve daha kötüsü, <i>"tel temiz"</i> diye okunurdu.
/// </para>
///
/// <para>
/// <b>macOS bilerek yok.</b> Kapasite belgesi hedefi Linux + Windows olarak
/// çiziyor; macOS okuyucusu yazmak, çıktı biçimini <b>ölçmeden</b> tahmin etmek
/// olurdu. Geliştirme makinesi macOS olduğu için bu, kısıt yolunun <b>gerçekten
/// koştuğu</b> tek yol — ve koştuğu ölçüldü.
/// </para>
/// </summary>
public static class WireDropReader
{
    /// <summary>
    /// Kaynak adları <b>public</b>: rapora yazılıyor ve testler onları
    /// dizge sabiti olarak tekrar YAZMAMALI — tekrarlanan bir ad, kaynağı
    /// değiştiği gün sessizce ayrışır.
    /// </summary>
    public const string ProcNetUdp = "/proc/net/udp";

    /// <inheritdoc cref="ProcNetUdp"/>
    public const string NetstatSummary = "netstat -s";

    /// <summary>
    /// Bu makinede tel katmanı okunabilir mi — <b>okumadan</b> söylüyor.
    /// Koşum başlamadan önce sorulması gereken soru: sonunda öğrenilirse
    /// koşum boşa gitmiş olur.
    /// </summary>
    public static bool Supported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Platforma göre okuma. <paramref name="linuxProcNetUdp"/> ve
    /// <paramref name="windowsNetstat"/> <b>metin olarak</b> veriliyor: kaynağı
    /// okumak (dosya, süreç) ile ayrıştırmak ayrı işler, ve ayrıştırma
    /// konteynersiz <b>ve platformsuz</b> sınanabilmeli. Bu ayrım olmasa Linux
    /// ayrıştırıcısı yalnızca Linux'ta sınanabilirdi — yani bu depoda hiç.
    /// </summary>
    public static LedgerReading Read(
        int udpPort,
        Func<string?> linuxProcNetUdp,
        Func<string?> windowsNetstat)
    {
        ArgumentNullException.ThrowIfNull(linuxProcNetUdp);
        ArgumentNullException.ThrowIfNull(windowsNetstat);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var text = linuxProcNetUdp();

            return text is null
                ? LedgerReading.Limited(LedgerLayer.Wire, ProcNetUdp, $"{ProcNetUdp} okunamadı")
                : ParseProcNetUdp(text, udpPort);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var text = windowsNetstat();

            return text is null
                ? LedgerReading.Limited(LedgerLayer.Wire, NetstatSummary, "`netstat -s` çalıştırılamadı")
                : ParseNetstatUdp(text);
        }

        return LedgerReading.Limited(
            LedgerLayer.Wire,
            RuntimeInformation.OSDescription,
            "bu platform için düşürme sayacı okuyucusu YOK (hedef: Linux + Windows). " +
            "Uydurma sıfır basmak `tel temiz` diye okunurdu.");
    }

    /// <summary>
    /// <c>/proc/net/udp</c> ayrıştırıcısı.
    ///
    /// <para>
    /// <b>Biçim çekirdeğin kendi başlığından doğrulandı</b>
    /// (<c>net/ipv4/udp.c</c>, <c>udp4_seq_show</c>):
    /// <c>sl local_address rem_address st tx_queue rx_queue tr tm-&gt;when
    /// retrnsmt uid timeout inode ref pointer drops</c> — yani <b><c>drops</c>
    /// SON alan</b>. Kolon indeksi yerine "son belirteç" okunuyor: aynı okuyucu
    /// <c>/proc/net/udp6</c> için de doğru kalıyor ve kolon eklenmesine
    /// dayanıklı.
    /// </para>
    ///
    /// <para>
    /// <b>Yerel port onaltılık.</b> <c>local_address</c> alanı
    /// <c>HEXIP:HEXPORT</c>; <c>232A</c> = 9002. Dinleyici hangi adrese
    /// bağlanmış olursa olsun (<c>0.0.0.0</c> ya da tek adres) port eşleşmesi
    /// yeterli.
    /// </para>
    ///
    /// <para>
    /// <b>Negatif <c>drops</c> gerçekten görülüyor</b> ve sayaç işaretli
    /// basıldığı için taşabiliyor. O hâlde defter sıfır <b>demiyor</b>:
    /// <c>LEDGER-LIMITED</c>. Negatifi <c>0</c>'a kırpmak *"düşürme yok"*
    /// demek olurdu ve gerçek muhtemelen tam tersi.
    /// </para>
    ///
    /// <para>
    /// <b>Port bulunamazsa da kısıt, sıfır değil:</b> soket kapanmışsa ya da
    /// başka bir ağ ad alanındaysa (container!) satır hiç yok. Sıfır dönmek
    /// *"düşürme olmadı"* iddiası olurdu; oysa gözlemci yanlış yere bakıyor —
    /// collector container'da koşarken <b>host'un</b> <c>/proc/net/udp</c>'si
    /// o soketi göstermiyor ve bu, bu düzenekte varsayılan hâl.
    /// </para>
    /// </summary>
    public static LedgerReading ParseProcNetUdp(string text, int udpPort)
    {
        ArgumentNullException.ThrowIfNull(text);

        var port = udpPort.ToString("X4", CultureInfo.InvariantCulture);
        var matched = 0;
        long total = 0;

        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Başlık satırı ve kırpılmış satırlar: `sl` alanı `123:` biçiminde.
            if (fields.Length < 13 || !fields[0].EndsWith(':'))
            {
                continue;
            }

            var local = fields[1];
            var colon = local.LastIndexOf(':');

            if (colon < 0
                || !local[(colon + 1)..].Equals(port, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(fields[^1], CultureInfo.InvariantCulture, out var drops))
            {
                return LedgerReading.Limited(
                    LedgerLayer.Wire,
                    ProcNetUdp,
                    $"`drops` alanı sayı değil: '{fields[^1]}'");
            }

            if (drops < 0)
            {
                return LedgerReading.Limited(
                    LedgerLayer.Wire,
                    ProcNetUdp,
                    $"`drops` NEGATİF ({drops}) — işaretli sayaç taşmış. " +
                    "Sıfıra kırpmak `düşürme yok` demek olurdu.");
            }

            matched++;
            total += drops;
        }

        return matched == 0
            ? LedgerReading.Limited(
                LedgerLayer.Wire,
                ProcNetUdp,
                $"UDP {udpPort} için satır yok — soket kapalı ya da başka bir ağ ad " +
                "alanında (collector container'da koşuyorsa host'un tablosunda GÖRÜNMEZ).")
            : LedgerReading.Measured(LedgerLayer.Wire, ProcNetUdp, total);
    }

    /// <summary>
    /// Windows <c>netstat -s</c> ayrıştırıcısı — UDP <i>receive errors</i>.
    ///
    /// <para>
    /// <b>Bu çıktı YERELLEŞTİRİLMİŞ</b> ve okuyucu bunu bir varsayım olarak
    /// bırakmıyor: etiket bulunamazsa <c>LEDGER-LIMITED</c>, sıfır değil.
    /// Türkçe bir Windows'ta <c>Receive Errors</c> yazmıyor, ve sıfır dönen bir
    /// okuyucu o makinede *"tel temiz"* diye okunurdu — bu deponun
    /// <c>tr-TR</c> tuzağının ağ katmanındaki karşılığı.
    /// </para>
    ///
    /// <para>
    /// IPv4 ve IPv6 bölümleri ayrı basılıyor; ikisi de <b>toplanıyor</b>, çünkü
    /// dinleyici çift yığında olabilir ve hangi yığından düştüğü bu ölçümün
    /// sorusu değil.
    /// </para>
    /// </summary>
    public static LedgerReading ParseNetstatUdp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = false;
        long total = 0;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith("Receive Errors", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0
                || !long.TryParse(
                    trimmed[(equals + 1)..].Trim(),
                    CultureInfo.InvariantCulture,
                    out var errors))
            {
                return LedgerReading.Limited(
                    LedgerLayer.Wire,
                    NetstatSummary,
                    $"`Receive Errors` satırı sayı taşımıyor: '{trimmed}'");
            }

            found = true;
            total += errors;
        }

        return found
            ? LedgerReading.Measured(LedgerLayer.Wire, NetstatSummary, total)
            : LedgerReading.Limited(
                LedgerLayer.Wire,
                NetstatSummary,
                "`Receive Errors` etiketi bulunamadı — çıktı yerelleştirilmiş olabilir. " +
                "Sıfır dönmek `tel temiz` iddiası olurdu.");
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bizigo.Devices;

/// <param name="Section">
/// Satırın ait olduğu config bölümü. Fark raporunun "hangi bölüm" cevabı bu.
/// </param>
public readonly record struct ConfigLine(string Section, string Text);

/// <summary>
/// Ham cihaz çıktısını <b>karşılaştırılabilir</b> hâle getirir (T26).
///
/// <para>
/// <b>Bu sınıf olmadan tablo işe yaramaz gürültüyle dolar.</b> Cihazlar her
/// çekimde değişen satırlar basıyor: FortiGate config dosyası sürümünü, ASA
/// <c>Cryptochecksum</c>'ı, MikroTik export başlığına o anın tarihini yazıyor.
/// Bunlar elenmezse her çekim bir "değişiklik" üretir ve RCA'nın F3'te arayacağı
/// sinyal kendi gürültüsünde kaybolur. Ticket'ın kabul kriteri bunu açıkça
/// sınıyor: yalnızca zaman damgası değişen iki çekim değişiklik üretmemeli.
/// </para>
///
/// <para>
/// <b>Gizli değerler siliniyor değil MASKELENİYOR.</b> Silmek, dönen bir
/// ön-paylaşımlı anahtarı görünmez yapardı — oysa anahtar rotasyonu gerçek ve
/// RCA açısından değerli bir değişiklik. Değerin yerine kısa bir özet konuyor:
/// değer değişince özet de değişiyor, yani <b>değişiklik yakalanıyor ama sır
/// hiçbir yere yazılmıyor</b>. Saklanan anlık görüntü de bu maskelenmiş metin;
/// cihazın ham çıktısı hiç kalıcı hâle gelmiyor.
/// </para>
/// </summary>
public static partial class ConfigNormalizer
{
    public const string FortiGate = "fortinet.fortigate";
    public const string CiscoAsa = "cisco.asa";
    public const string MikroTik = "mikrotik.routeros";

    public static readonly string[] SupportedVendors = [FortiGate, CiscoAsa, MikroTik];

    /// <summary>Bölüm atanamayan satırlar. Genelde dosya başı/sonu.</summary>
    public const string RootSection = "(kök)";

    // ------------------------------------------------------------- gürültü

    /// <summary>
    /// Her vendor'da elenen satırlar. Desenler <b>satır başına sabit maliyetli</b>:
    /// iç içe niceleyici yok, geri izlemeye düşecek yapı yok — F1'in ReDoS
    /// dersinin config tarafındaki karşılığı.
    /// </summary>
    private static readonly Dictionary<string, Regex[]> Noise = new(StringComparer.Ordinal)
    {
        [FortiGate] =
        [
            // Config dosyası sürüm başlığı her yazımda değişiyor.
            ConfFileVer(), FortiBuild(), FortiVdom(),
        ],
        [CiscoAsa] =
        [
            // "Written by admin at 10:32:11 UTC Tue Aug 18 2026" — her çekimde farklı.
            AsaWrittenBy(), AsaSaved(), AsaCryptochecksum(), AsaClockPeriod(), AsaHardware(),
        ],
        [MikroTik] =
        [
            // "# aug/18/2026 10:32:11 by RouterOS 7.14.3" — export başlığı.
            MikroTikHeader(), MikroTikSoftwareId(), MikroTikModel(),
        ],
    };

    [GeneratedRegex(@"^#conf_file_ver=", RegexOptions.NonBacktracking)]
    private static partial Regex ConfFileVer();

    [GeneratedRegex(@"^#buildno=", RegexOptions.NonBacktracking)]
    private static partial Regex FortiBuild();

    [GeneratedRegex(@"^#global_vdom=", RegexOptions.NonBacktracking)]
    private static partial Regex FortiVdom();

    [GeneratedRegex(@"^:\s*Written by ", RegexOptions.NonBacktracking)]
    private static partial Regex AsaWrittenBy();

    [GeneratedRegex(@"^:\s*Saved", RegexOptions.NonBacktracking)]
    private static partial Regex AsaSaved();

    [GeneratedRegex(@"^Cryptochecksum:", RegexOptions.NonBacktracking)]
    private static partial Regex AsaCryptochecksum();

    [GeneratedRegex(@"^ntp clock-period ", RegexOptions.NonBacktracking)]
    private static partial Regex AsaClockPeriod();

    [GeneratedRegex(@"^:\s*(Hardware|Serial Number|Device Manager Version):", RegexOptions.NonBacktracking)]
    private static partial Regex AsaHardware();

    [GeneratedRegex(@"^#\s+\w{3}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2}\s+by\s+RouterOS", RegexOptions.NonBacktracking)]
    private static partial Regex MikroTikHeader();

    [GeneratedRegex(@"^#\s+software id\s*=", RegexOptions.NonBacktracking)]
    private static partial Regex MikroTikSoftwareId();

    [GeneratedRegex(@"^#\s+(model|serial number)\s*=", RegexOptions.NonBacktracking)]
    private static partial Regex MikroTikModel();

    // -------------------------------------------------------------- gizli

    /// <summary>
    /// Değeri maskelenen ayarlar. Anahtar adı korunuyor, değeri özete
    /// çevriliyor — "hangi sır değişti" görünür, "sır ne" görünmez.
    ///
    /// <para>
    /// Anahtar kelimenin önünde <b>en çok dört</b> belirtece izin veriliyor
    /// (<c>snmp-server community …</c>, <c>set psksecret ENC …</c>,
    /// <c>ikev2 remote-authentication pre-shared-key …</c>) ve ayırıcı hem
    /// boşluk hem <c>=</c> olabiliyor (MikroTik <c>password=…</c> yazıyor).
    /// Serbest bir <c>.*?</c> öneki yerine <b>sınırlı</b> tekrar: desen geri
    /// izlemeye düşmüyor ve maliyeti satır uzunluğunda doğrusal kalıyor.
    /// </para>
    ///
    /// <para>
    /// <b>İki kez kırıldı, ikisi de aynı sınıf.</b> Önce desen anahtar
    /// kelimeyi satır başına bağlıyordu ve ASA'nın <c>snmp-server community …</c>
    /// satırı maskelenmeden kalıyordu; bir belirteç eklendi. Sonra ASA'nın
    /// gerçek IKEv2 söz dizimi <b>iki</b> belirteç taşıdığı için
    /// (<c>ikev2 remote-authentication pre-shared-key</c>) ham anahtar yine
    /// normalize edilmiş metinde kalıyordu — bunu simülatör fixture'ı ilk
    /// koşumunda yakaladı (FS · S01).
    /// </para>
    ///
    /// <para>
    /// Sınır <b>dört</b> — ASA'nın yaygın <c>snmp-server host &lt;arayüz&gt; &lt;ip&gt; community &lt;anahtar&gt;</c> biçimi dört belirteç taşıyor. Asıl kısıt "kaç kelime" değil "serbest joker
    /// yok". Fazla maskelemek sızdırmaktan ucuz; bu yüzden sınır cömert
    /// tutuldu ama sonsuz değil.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"^(?<prefix>\s*(?:[\w./-]+[\s=]+){0,4}(?:password|passwd|psksecret|secret|pre-shared-key|wpa2-pre-shared-key|snmp-community|community|auth-key|key-string)[\s=]+(?:ENC[\s=]+|encrypted[\s=]+|[78][\s=]+)?)(?<value>\S.*)$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex SecretAssignment();

    // ------------------------------------------------------------- bölüm

    [GeneratedRegex(@"^config\s+(?<name>.+?)\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex FortiSection();

    [GeneratedRegex(@"^\s*edit\s+(?<name>.+?)\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex FortiEdit();

    [GeneratedRegex(@"^/(?<name>\S.*?)\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex MikroTikPath();

    /// <summary>
    /// Ham metni normalize satırlara çevirir.
    ///
    /// <para>
    /// Maliyet girdi uzunluğunda <b>doğrusal</b>: satır başına sabit sayıda
    /// desen, hiçbiri geri izlemeli değil. Bir cihazın 50 bin satırlık
    /// config'i çekim turunu kilitleyemiyor.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ConfigLine> Normalize(string vendor, string? text)
    {
        var noise = Noise.TryGetValue(vendor, out var rules) ? rules : [];
        var lines = new List<ConfigLine>();

        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var section = RootSection;
        var forti = new Stack<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');

            if (line.Length == 0)
            {
                continue;
            }

            // Maskeleme bölüm izlemeden ÖNCE: ASA'da bölüm adı satırın
            // kendisi ve `snmp-server community …` bir bölüm başlığı. Sırayı
            // ters kurunca sır, maskelenmiş metinden çıkıyor ama bölüm adının
            // içinde saklanmaya devam ediyordu — bir birim testi yakaladı.
            var masked = Mask(line.Trim());

            section = TrackSection(vendor, line, masked, section, forti);

            if (IsNoise(line, noise))
            {
                continue;
            }

            lines.Add(new ConfigLine(section, masked));
        }

        return lines;
    }

    private static bool IsNoise(string line, Regex[] noise)
    {
        foreach (var rule in noise)
        {
            if (rule.IsMatch(line))
            {
                return true;
            }
        }

        // Yalnızca yorum işareti taşıyan satırlar her vendor'da gürültü.
        var trimmed = line.TrimStart();
        return trimmed is "#" or "!" or ":";
    }

    /// <summary>
    /// Bölüm izleme. Tam bir config ayrıştırıcısı DEĞİL — bilinçli.
    ///
    /// <para>
    /// Vendor başına gerçek bir sözdizimi ağacı kurmak üç ayrı ayrıştırıcı ve
    /// üç ayrı bakım yükü demekti. Buradaki iş yalnızca "bu satır hangi başlığın
    /// altında" sorusunu cevaplamak, ve fark raporunun ihtiyacı bu kadar.
    /// </para>
    /// </summary>
    /// <param name="line">Ham satır — girinti kararı buna bakıyor.</param>
    /// <param name="masked">Gizli değeri maskelenmiş hâli — bölüm ADI bundan.</param>
    private static string TrackSection(
        string vendor,
        string line,
        string masked,
        string current,
        Stack<string> forti)
    {
        switch (vendor)
        {
            case FortiGate:
            {
                if (FortiSection().Match(line) is { Success: true } config)
                {
                    forti.Push(config.Groups["name"].Value);
                    return string.Join(" / ", forti.Reverse());
                }

                if (FortiEdit().Match(line) is { Success: true } edit && forti.Count > 0)
                {
                    return $"{string.Join(" / ", forti.Reverse())} / {edit.Groups["name"].Value.Trim('"')}";
                }

                if (line.Trim() == "end" && forti.Count > 0)
                {
                    forti.Pop();
                    return forti.Count > 0 ? string.Join(" / ", forti.Reverse()) : RootSection;
                }

                return current;
            }

            case MikroTik:
                // Export'ta yol satırı (`/ip firewall filter`) bölümü belirliyor.
                return MikroTikPath().Match(line) is { Success: true } path
                    ? path.Groups["name"].Value
                    : current;

            case CiscoAsa:
                // ASA'da girintisiz satır yeni bölüm, girintili satır ona ait.
                // Bölüm adı MASKELENMİŞ metinden: aksi hâlde `snmp-server
                // community <sır>` satırı bir bölüm başlığı olarak sırrı
                // saklamaya devam ederdi.
                return line[0] is ' ' or '\t' ? current : masked;

            default:
                return current;
        }
    }

    /// <summary>
    /// Gizli değerin yerine kısa bir özet. Aynı sır → aynı özet, farklı sır →
    /// farklı özet: rotasyon fark olarak görünüyor, değer hiçbir yere yazılmıyor.
    /// </summary>
    /// <summary>
    /// Serbest metin alanları — burada geçen anahtar kelime bir <b>ayar adı
    /// değil</b>, açıklamanın içindeki bir sözcük.
    ///
    /// <para>
    /// Önek sınırı genişletildiğinde doğan kusur: <c>description "shared secret
    /// for site B"</c> satırında <c>secret</c> üçüncü sözcük, desen tutuyor ve
    /// açıklamanın geri kalanı maskeleniyordu. Sır sızıntısı değil ama
    /// <b>sessiz veri kaybı</b>: fark raporu operatörün yazdığı açıklamayı bir
    /// özete çeviriyor ve kimse silindiğini görmüyor.
    /// </para>
    ///
    /// <para>
    /// Cihazların hepsinde bu alanlar var ve hepsinde serbest metin:
    /// FortiGate <c>set comments</c>, ASA <c>description</c>/<c>remark</c>,
    /// RouterOS <c>comment=</c>.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?:^|[\s=])(?:description|descr|remark|comment|comments|banner|message)(?:[\s=]|$)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FreeTextField();

    internal static string Mask(string line)
    {
        var match = SecretAssignment().Match(line);

        if (!match.Success)
        {
            return line;
        }

        // Anahtar kelime bir açıklamanın İÇİNDE geçiyorsa dokunma.
        if (FreeTextField().IsMatch(match.Groups["prefix"].Value))
        {
            return line;
        }

        var value = match.Groups["value"].Value.Trim().Trim('"');
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{match.Groups["prefix"].Value}<gizli:{digest}>");
    }
}

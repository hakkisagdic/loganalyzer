using System.Text.RegularExpressions;

namespace Bizigo.Contracts.Security;

/// <summary>
/// Üretici söz diziminde <b>sır ataması</b> nasıl görünür — tek kaynak (T41).
///
/// <para>
/// <b>Neden burada:</b> aynı anahtar kelime listesi iki yerde okunuyor —
/// <c>ConfigNormalizer</c> (cihaz config'i) ve <c>PromptRedactor</c> (prompt'a
/// giden log metni). İki kopya yazılsaydı biri sertleştiğinde diğeri eski
/// hâliyle kalırdı ve <b>ayrışma sessiz olurdu</b>: config tarafı maskeler,
/// log tarafı sızdırır, hiçbir sayaç bunu göstermez.
/// </para>
///
/// <para>
/// <b>Ortak olan LİSTE, çapa değil.</b> Bu ayrım bu sınıfın var oluş sebebi.
/// Config satırında anahtar kelime satır başından itibaren en çok dört
/// belirteç içinde geliyor; syslog satırında aynı ifadenin önünde öncelik,
/// tarih, host, uygulama etiketi ve <c>%ASA-6-113012:</c> gibi üretici kodları
/// var — sonuncusu <c>[\w./-]</c> sınıfına hiç uymuyor. Config deseni log
/// metnine olduğu gibi uygulansaydı <b>hiçbir şey bulmazdı ve bulmadığı için
/// yeşil görünürdü</b>; altın korpusta yanlış pozitif zaten 0 olduğu için de
/// kimse fark etmezdi. O yüzden iki çapa var, tek liste.
/// </para>
///
/// <para>
/// <b>Her iki çapada da serbest joker yok.</b> Log tarafındaki önek de config
/// tarafındaki gibi <c>{0,4}</c> ile sınırlı; <c>.*?</c> öneki geri izlemeye
/// kapı açardı (F1'in ReDoS dersi).
/// </para>
/// </summary>
public static partial class SecretPatterns
{
    /// <summary>
    /// Değeri sır sayılan ayar adları. <b>Bu listeyi genişleten iki tarafı da
    /// genişletir</b> — kasıtlı.
    /// </summary>
    public const string Keywords =
        "password|passwd|psksecret|secret|pre-shared-key|wpa2-pre-shared-key|" +
        "snmp-community|community|auth-key|key-string";

    /// <summary>
    /// Config satırındaki sır ataması. Anahtar adı korunuyor, değeri
    /// maskeleniyor — "hangi sır değişti" görünür, "sır ne" görünmez.
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
    ///
    /// <para>
    /// <b>T41'de buraya taşındı</b>, davranışı değişmeden: config kolunun
    /// çıktısı bugünkü hâliyle kalmalı ve <c>SimulatorFixtureChainTests</c>
    /// bunu tutuyor.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"^(?<prefix>\s*(?:[\w./-]+[\s=]+){0,4}(?:" + Keywords + @")[\s=]+(?:ENC[\s=]+|encrypted[\s=]+|[78][\s=]+)?)(?<value>\S.*)$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    public static partial Regex ConfigAssignment();

    /// <summary>
    /// Log satırındaki sır ataması — <b>satır başına bağlı değil, satır içine
    /// bakıyor</b>.
    ///
    /// <para>
    /// Önek yine <c>{0,4}</c> ile sınırlı ama <c>^</c> yok: syslog satırında
    /// anahtar kelimenin önünde kaç belirteç olduğu bilinemez, önemli olan
    /// <b>hemen öncesindeki</b> dört belirteç — serbest metin muafiyeti (<see
    /// cref="FreeTextField"/>) tam olarak onlara bakıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Değerin sınırı iki kuralla belirleniyor:</b> değer tırnaklıysa
    /// kapanış tırnağına kadar (FortiGate kv logları alanlarını tırnaklıyor),
    /// tırnaksızsa <b>satır sonuna kadar</b>. İkincisi bir tahmin değil bir
    /// karar: tırnaksız bir değerin nerede bittiği log satırında bilinmiyor ve
    /// tahmin etmek sızdırmak demek. Fazla maskelemek ölçülüyor (§4), kaçırmak
    /// ölçülmüyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bilinen fazla maskeleme:</b> boşluk ayırıcı düz İngilizce anlatımı da
    /// tutuyor — <c>Failed password for admin from 10.1.2.3</c> satırında
    /// <c>for admin from 10.1.2.3</c> maskelenir. Depodaki 87 satırlık altın
    /// korpusta bu **0** kez oluyor (ölçüldü), ama sshd biçimli bir kaynak
    /// eklenirse olacaktır. Görünür yön bilinçli tercih; sayacı
    /// <c>redaction_masked_values</c>.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>(?:[\w./-]+[\s=]+){0,4})(?:" + Keywords + @")(?:\s*[=:]\s*|\s+)(?<value>""[^""\n]*""|'[^'\n]*'|[^\s""'][^\n]*$)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Multiline)]
    public static partial Regex LogAssignment();

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
    /// RouterOS <c>comment=</c>. Log tarafında da aynı muafiyet geçerli, ve
    /// aynı sebeple <b>sınırlı öneke</b> uygulanıyor: bütün satıra bakılsaydı
    /// FortiGate'in <c>logdesc="…"</c> alanı satırın geri kalanındaki gerçek
    /// atamaları da muaf tutardı.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"(?:^|[\s=])(?:description|descr|remark|comment|comments|banner|message)(?:[\s=]|$)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    public static partial Regex FreeTextField();
}

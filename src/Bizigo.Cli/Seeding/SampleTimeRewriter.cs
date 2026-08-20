using System.Globalization;
using System.Text.RegularExpressions;
using Bizigo.Parsing.Schema;

namespace Bizigo.Cli.Seeding;

/// <param name="Text">Yeniden yazılmış satır.</param>
/// <param name="Rewritten">
/// Satırda değiştirilebilen bir zaman damgası bulundu mu. <c>false</c> olması
/// hata değil: Cisco ASA'nın <c>&lt;166&gt;10.1.1.1 %ASA-6-302020: …</c> biçimi
/// gerçekten zaman damgası taşımıyor ve o satır <c>ObservedAt</c>'e düşüyor —
/// üretimde de öyle oluyor.
/// </param>
public readonly record struct RewrittenLine(string Text, bool Rewritten);

/// <summary>
/// Altın örnek satırındaki <b>yalnızca zaman damgasını</b> hedef ana taşır.
///
/// <para>
/// <b>Neden gerekiyor:</b> normalizasyon olay zamanını önce parser'ın çözdüğü
/// damgadan alıyor (<c>EventNormalizer.ResolveTimestamp</c>). Örnek dosyalar
/// 2015–2024 arası tarihler taşıdığı için satırlar olduğu gibi yüklenirse
/// olaylar oraya düşer ve baseline ölçümü — tabanı 1 saatten 30 güne süpüren
/// ölçüm — hiçbir şey göremez. Zamanı boru hattında zorla değiştirmek ise
/// <c>time_source</c>'u üretimdekinden farklı kılardı.
/// </para>
///
/// <para>
/// <b>Neden meşru:</b> aynı olayı yarın basan bir cihaz aynı satırı yarının
/// damgasıyla yazar. Burada yapılan şey uydurma değil, cihazın tekrarı — ve
/// satırın damga dışındaki her baytı aynen kalıyor.
/// </para>
///
/// <para>
/// <b>Bilinen bedeli:</b> damga biçimi bilgisi burada <b>ikinci kez</b>
/// yazılıyor; birincisi parser YAML'ının <c>date</c> adımı. İkisi ayrışırsa
/// satır sessizce yanlış zamana düşerdi — bu yüzden yükleyici her olayda
/// normalize edilmiş <c>ts</c>'nin ektiği ana <b>eşit olduğunu doğruluyor</b>
/// (<see cref="GoldenSampleSeeder"/>). Ayrışma sessiz değil, gürültülü.
/// </para>
/// </summary>
public static class SampleTimeRewriter
{
    /// <summary>
    /// Yılsız/ofsetsiz syslog damgalarının yorumlandığı dilim.
    ///
    /// <para>
    /// <b>Bu değer parser YAML'ından geliyor:</b> <c>cisco.asa/*.yaml</c> ve
    /// <c>mikrotik.routeros/*.yaml</c> <c>default_timezone: Europe/Istanbul</c>
    /// yazıyor. Buraya UTC yazmak satırları sessizce üç saat kaydırırdı; o kayma
    /// hiçbir yerde hata üretmez, yalnızca ölçümü bozar. Doğrulama ektiği ana
    /// eşitlik aradığı için ayrışma derhal patlar.
    /// </para>
    /// </summary>
    private const string SyslogZoneId = "Europe/Istanbul";

    private static readonly TimeZoneInfo SyslogZone =
        TimeZoneResolver.Resolve(SyslogZoneId)
        ?? throw new InvalidOperationException($"Saat dilimi çözülemedi: {SyslogZoneId}");

    private static readonly RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>FortiOS'un otoritesi: <c>eventtime</c>. <c>date</c>/<c>time</c> kozmetik.</summary>
    private static readonly Regex FortiEventTime = new(@"(?<=\beventtime=)\d+", Options);
    private static readonly Regex FortiDate = new(@"(?<=\bdate=)\d{4}-\d{2}-\d{2}", Options);
    private static readonly Regex FortiTime = new(@"(?<=\btime=)\d{2}:\d{2}:\d{2}", Options);
    private static readonly Regex FortiTz = new(@"\btz=""(?<off>[+-]\d{2}:?\d{2})""", Options);

    /// <summary>nginx: hem <c>combined</c> köşeli parantezi hem JSON'daki <c>time</c> alanı.</summary>
    private static readonly Regex HttpStamp =
        new(@"\d{2}/[A-Z][a-z]{2}/\d{4}:\d{2}:\d{2}:\d{2} (?<off>[+-]\d{4})", Options);

    /// <summary>RouterOS'un RFC5424 biçimi.</summary>
    private static readonly Regex IsoStamp = new(
        @"^(?<pri><\d{1,3}>)?(?<date>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?<frac>\.\d+)?(?<off>Z|[+-]\d{2}:\d{2})",
        Options);

    /// <summary>
    /// RFC3164 / CISCOTIMESTAMP. Yıl isteğe bağlı ve gün önündeki boşluk
    /// sayısı korunuyor — üst grok setinin <c>MONTH +MONTHDAY</c> tanımı ikisini
    /// de kabul ediyor ama satırı olduğundan fazla değiştirmemek tercih.
    /// </summary>
    private static readonly Regex SyslogStamp = new(
        @"^(?<pri><\d{1,3}>)?(?<mon>[A-Z][a-z]{2})(?<gap>\s{1,2})(?<day>\d{1,2})(?<year>\s\d{4})?\s(?<time>\d{2}:\d{2}:\d{2})",
        Options);

    /// <summary>
    /// Satırın damgasını <paramref name="target"/>'a taşır.
    ///
    /// <para>
    /// Sıra önemli: FortiOS satırı <c>&lt;189&gt;date=…</c> ile başlayabiliyor ve
    /// başka hiçbir kural ona vurmuyor, ama önce denenmesi niyeti açık tutuyor.
    /// </para>
    /// </summary>
    public static RewrittenLine Rewrite(string line, DateTimeOffset target)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (FortiEventTime.IsMatch(line))
        {
            return new RewrittenLine(RewriteFortinet(line, target), Rewritten: true);
        }

        if (HttpStamp.Match(line) is { Success: true } http)
        {
            return new RewrittenLine(Replace(line, http, RenderHttp(http, target)), Rewritten: true);
        }

        if (IsoStamp.Match(line) is { Success: true } iso)
        {
            return new RewrittenLine(Replace(line, iso, RenderIso(iso, target)), Rewritten: true);
        }

        if (SyslogStamp.Match(line) is { Success: true } syslog)
        {
            return new RewrittenLine(Replace(line, syslog, RenderSyslog(syslog, target)), Rewritten: true);
        }

        return new RewrittenLine(line, Rewritten: false);
    }

    private static string Replace(string line, Match match, string replacement) =>
        string.Concat(line.AsSpan(0, match.Index), replacement, line.AsSpan(match.Index + match.Length));

    /// <summary>
    /// <c>eventtime</c> nanosaniye epoch olarak yazılıyor: <c>UNIX_AUTO</c>
    /// ölçeği basamak sayısından çıkarıyor ve 19 basamak nanosaniye demek.
    /// <c>date</c>/<c>time</c> parser tarafından okunmuyor ama <c>attrs</c>'a
    /// giriyor; eski tarihte bırakmak olay detayında kendisiyle çelişen bir
    /// kayıt üretirdi.
    /// </summary>
    private static string RewriteFortinet(string line, DateTimeOffset target)
    {
        var nanos = target.ToUnixTimeMilliseconds() * 1_000_000L;
        var rewritten = FortiEventTime.Replace(
            line,
            nanos.ToString(CultureInfo.InvariantCulture));

        var zone = FortiTz.Match(rewritten) is { Success: true } tz
            ? TimeZoneResolver.Resolve(tz.Groups["off"].Value) ?? TimeZoneInfo.Utc
            : TimeZoneInfo.Utc;

        var local = TimeZoneInfo.ConvertTime(target, zone);

        rewritten = FortiDate.Replace(rewritten, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return FortiTime.Replace(rewritten, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Ofset metni <b>korunuyor</b>, yalnızca an değişiyor: satırdaki
    /// <c>+0200</c>'yi <c>+0000</c> yapmak damga dışında bir baytı da
    /// değiştirmek olurdu.
    /// </summary>
    private static string RenderHttp(Match match, DateTimeOffset target)
    {
        var offset = match.Groups["off"].Value;
        var local = TimeZoneInfo.ConvertTime(target, TimeZoneResolver.Resolve(offset) ?? TimeZoneInfo.Utc);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{local:dd/MMM/yyyy:HH:mm:ss} {offset}");
    }

    private static string RenderIso(Match match, DateTimeOffset target)
    {
        var offset = match.Groups["off"].Value;
        var local = TimeZoneInfo.ConvertTime(target, TimeZoneResolver.Resolve(offset) ?? TimeZoneInfo.Utc);

        // Kesirli kısım korunuyor ama sıfırlanıyor: ekilen anlar tam saniye
        // (aşağıdaki biçimlerin çoğu saniyenin altını taşımıyor), yani
        // sıfırdan başka bir şey yazmak damgayı satırın kendisiyle çelişik
        // yapardı.
        var fraction = match.Groups["frac"].Success
            ? "." + new string('0', match.Groups["frac"].Value.Length - 1)
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{match.Groups["pri"].Value}{local:yyyy-MM-ddTHH:mm:ss}{fraction}{offset}");
    }

    private static string RenderSyslog(Match match, DateTimeOffset target)
    {
        var local = TimeZoneInfo.ConvertTime(target, SyslogZone);

        // İki boşluklu yazım yalnızca tek haneli günlerde anlamlı; yeni gün iki
        // haneliyse tek boşluğa iniyor, çünkü `MMM  d` biçimi "Jul  15" yazımını
        // kabul etmez.
        var gap = match.Groups["gap"].Value.Length == 2 && local.Day < 10 ? "  " : " ";
        var year = match.Groups["year"].Success
            ? string.Create(CultureInfo.InvariantCulture, $" {local.Year}")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{match.Groups["pri"].Value}{local:MMM}{gap}{local.Day}{year} {local:HH:mm:ss}");
    }
}

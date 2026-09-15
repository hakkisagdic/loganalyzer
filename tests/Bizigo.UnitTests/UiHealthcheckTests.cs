using System.Text.RegularExpressions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Arayüzün sağlık kontrolü hazırlık ucunu yokluyor</b> (T62).
///
/// <para>
/// Uç <c>ui/</c> tarafında yazılı ve bekçileri de orada (<c>ui/tests/health-ready.test.ts</c>).
/// Buradaki test başka bir şeyi tutuyor: <b>compose'un o ucu gerçekten
/// yokladığını</b>. İkisi ayrı sorular ve ayrılmaları T50'nin ölçtüğü ayrım —
/// <i>var olmak ile bağlı olmak</i>: uç kusursuz çalışırken sağlık kontrolü
/// eski hâline dönmüş olabilir ve <c>ui/</c> tarafındaki testlerin hiçbiri bunu
/// göremez.
/// </para>
///
/// <para>
/// <b>Neden C# tarafında:</b> ölçülen dosya <c>deploy/docker-compose.yml</c> ve
/// compose bekçileri bu pakette duruyor (<c>RepositoryLayout.ComposeFile</c>,
/// realm bekçileri, M11'in <c>Dockerfile</c> bekçisi). İkinci bir yerde ikinci
/// bir compose okuyucusu açmak §9'un yasakladığı kopya olurdu.
/// </para>
///
/// <para>
/// <b>Bu bekçinin TUTAMADIĞI:</b> ucun gerçekten cevap verdiğini görmüyor —
/// metin okuyor, istek atmıyor. Konteynerli koşum koordinatörde (§2) ve kabul
/// kriteri 5 orada ölçülüyor.
/// </para>
/// </summary>
public sealed class UiHealthcheckTests
{
    private static string Compose { get; } = File.ReadAllText(RepositoryLayout.ComposeFile);

    /// <summary>
    /// <c>ui</c> servisinin sağlık kontrolü <b>hazırlık ucunu</b> yokluyor.
    ///
    /// <para>
    /// Eski hâli kök sayfayı yokluyordu ve ölçütü <c>&lt; 500</c>'dü; sonda çerez
    /// taşımadığı için istek oturum deposuna hiç uğramıyor, kök sayfa 307 ile
    /// girişe yönlendiriyor ve 307 &lt; 500 olduğu için kontrol <b>yeşil</b>
    /// yanıyordu — <c>redis-session</c> tamamen kırıkken bile.
    /// </para>
    /// </summary>
    [Fact]
    public void Arayuz_saglik_kontrolu_hazirlik_ucunu_yokluyor()
    {
        var probe = UiProbeCommand();

        Assert.Contains("/api/health/ready", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ölçüt <c>r.ok</c>, <c>&lt; 500</c> DEĞİL.</b>
    ///
    /// <para>
    /// Bu ayrım ucun tamamını taşıyor: hazırlık ucu hazır değilken <b>503</b>
    /// dönüyor ve <c>&lt; 500</c> ölçütü 503'ü de geçerdi. Yani uç yazılmış ama
    /// <b>okunmamış</b> olurdu — bu deponun <i>"kapı eklemek işin yarısı;
    /// okunmayan kapı, olmayan kapıyla aynı sonucu veriyor"</i> kuralı.
    /// </para>
    /// </summary>
    [Fact]
    public void Olcut_yanit_kodunun_basarisi()
    {
        var probe = UiProbeCommand();

        Assert.Contains("r.ok", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("r.status < 500", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ui</c> servisinin sağlık kontrolünün <b>komutu</b> — yorumlar
    /// ELENEREK.
    ///
    /// <para>
    /// <b>Bu ayrım ölçülerek doğdu.</b> İlk hâli servis bloğunun tamamını
    /// okuyordu ve kırmızı ölçümü onu yakaladı: compose kasten eski sondaya
    /// döndürüldüğünde bekçi <b>yeşil kaldı</b>, çünkü hemen yukarıdaki yorum
    /// bloğu <c>/api/health/ready</c> dizgisini ve <c>r.ok</c>'u zaten
    /// içeriyordu. Yani bekçi <i>ucun yoklandığını</i> değil <i>ucun adının
    /// dosyada geçtiğini</i> ölçüyordu.
    /// </para>
    ///
    /// <para>
    /// Sınıf bu depoda tanıdık: <b>bir adı metinde bulmak, o şeye dokunmak
    /// değil.</b> Buradaki hâli özellikle sinsi, çünkü yanlış eşleşmeyi üreten
    /// şey kapının kendi <b>gerekçe yorumu</b> — yani bekçiyi körleştiren şey
    /// onu açıklayan metindi.
    /// </para>
    /// </summary>
    private static string UiProbeCommand()
    {
        var body = UiService();

        var lines = body
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => !line.TrimStart().StartsWith('#'));

        return string.Join('\n', lines);
    }

    /// <summary>
    /// <c>ui</c> servisinin compose bloğu. Yalnızca <b>o servisin</b> metni
    /// okunuyor: dosyanın tamamında aramak, başka bir servisin sağlık
    /// kontrolünü bu servisin kanıtı sayardı.
    /// </summary>
    private static string UiService()
    {
        var match = Regex.Match(
            Compose,
            @"^  ui:\r?\n(?<body>(?:^(?:    |\r?\n).*\r?\n?)*)",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        Assert.True(match.Success, "`docker-compose.yml` içinde `ui:` servisi bulunamadı.");

        var body = match.Groups["body"].Value;

        Assert.Contains("healthcheck:", body, StringComparison.Ordinal);

        return body;
    }
}

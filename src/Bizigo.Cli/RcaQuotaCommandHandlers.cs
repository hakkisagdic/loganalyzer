using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Cli;

/// <summary>
/// <c>bizigo rca quota</c> — bir grubun RCA kotasını ve <b>kaynak başına</b>
/// dağılımını basar (M16).
///
/// <h3>Neden CLI — üç aday ölçüldü, ikisinin okuyucusu YOK</h3>
///
/// <para>
/// M15 bir boşluk ölçtü: <see cref="RcaQuotaUsage.BySource"/> <b>hesaplanıyor ve
/// atılıyor</b> — <c>UsageAsync</c>'in tek çağıranı kendi <c>CheckAsync</c>'i ve
/// üretimde kırılımı hiç kimse okumuyordu. Sonucu, rezerv yüzdesinin gerçek
/// kullanımdan gelmesinin <b>imkânsız</b> olması: karar verecek insan veriyi
/// göremiyor.
/// </para>
///
/// <para>
/// M16 üç yüzey adayını ölçtü ve <b>ikisinin bugün okuyucusu yok</b>:
/// </para>
///
/// <list type="table">
///   <item>
///     <term><c>GET /v1/rca/quota</c></term>
///     <description>
///       <b>Okuyucusu yok, ve bilerek.</b> RCA ekranı kotayı <i>kapsam dışında
///       bırakmış</i>: <c>ui/src/app/rca/page.tsx</c> — <i>"Elle tetikleme dar
///       tutuldu: yalnızca kullanıcı tetikleyicisi, kuyruk/kota/debounce yok —
///       onlar dört tetikleyiciyle birlikte F4'te."</i> Tüketicisi açıkça
///       ertelenmiş bir uç yazmak §8'in yasağı.
///     </description>
///   </item>
///   <item>
///     <term><c>rca.quota</c> MCP aracı</term>
///     <description>
///       <b>Modelin cevabı ZATEN var.</b> <c>rca.trigger</c> kota dolduğunda
///       <c>state=rejected</c> + <c>reason</c> döndürüyor
///       (<i>"Kota dolduğu için RCA hiç çalıştırılmadı"</i>) ve <c>rca.runs</c>
///       her satırda <c>counts_against_quota</c> taşıyor. Reaktif yol
///       <b>bedava</b>: reddedilen koşum kotadan düşülmüyor
///       (<c>CountsAgainstQuota=false</c>). Yani ikinci bir yol yeni bir yetenek
///       vermezdi, yalnızca araç başına bağlam bütçesi eklerdi (§9).
///     </description>
///   </item>
///   <item>
///     <term><c>bizigo rca quota</c></term>
///     <description>
///       <b>Okuyucusu VAR ve M15 onu yarattı:</b> rezerv yüzdesine karar verecek
///       operatör. Kararı veren o, ve elinde hiçbir yol yoktu.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>Emsal <c>bizigo fields coverage</c></b>: bu depoda "ölç ve bas" komutunun
/// okuyucusu bir ekran değil, <b>karar veren bir insan</b>. Aynı şekil.
/// </para>
///
/// <h3>İKİNCİ BİR TOPLAMA YAZILMADI</h3>
///
/// <para>
/// Kırılım <see cref="RcaQuotaGate.UsageAsync"/>'ten geliyor —
/// <see cref="RcaQuotaUsage.BySource"/> olduğu gibi basılıyor. Burada bir
/// <c>GroupBy</c> yazmak, M15'te bir kat aşağıda düzeltilen şeyin aynısı
/// olurdu: aynı sayının iki gösterimi, ve ayrıştığı gün hiçbir belirti yok (§9).
/// </para>
///
/// <para>
/// <b>Etkin sınır da basılıyor</b>, ve kaynak başına: rezerv açıkken
/// <c>Alert</c> ile diğerlerinin <b>farklı</b> sınır gördüğü tablodan
/// okunabiliyor. Tek bir "limit" sayısı basmak rezervin varlığını görünmez
/// kılardı — yani M15'in düzelttiği şeyi rapor katmanında geri getirirdi.
/// </para>
/// </summary>
/// <remarks>
/// <b><c>public</c>, ve sebebi ölçülmüş bir kısıt:</b> <c>Bizigo.Cli</c>'ye
/// <c>InternalsVisibleTo</c> eklemek üst-düzey ifadelerin ürettiği
/// <c>Program</c> tipini test derlemesine görünür yapıyor ve
/// <c>Bizigo.Api</c>'nin <c>Program</c>'ıyla çakışıyor (M12'de ölçüldü, yedi
/// test dosyası CS0433 ile düştü). Emsal <c>McpCommandHandlers.BuildServices</c>
/// ve <c>McpEndpoints.ToolAssemblies</c>: bir bekçinin okuyabilmesi için
/// <c>public</c>.
/// </remarks>
public static class RcaQuotaCommandHandlers
{
    /// <summary>
    /// Kotayı basar. Çıkış kodu: <c>0</c> başarılı, <c>2</c> eksik ayar.
    /// </summary>
    /// <param name="ownerGroup">Hangi grubun kotası.</param>
    /// <param name="connectionString">
    /// Kontrol düzlemi adresi; yoksa <c>BIZIGO_CONTROLPLANE</c>. Emsal
    /// <c>sigma sync</c>: adres yoksa <b>reddediyor</b> ve değişkenin adını
    /// yazıyor — <c>localhost</c>'a düşmüyor, çünkü yanlış ama ulaşılabilir bir
    /// adres <b>başka bir kurulumun</b> kotasını raporlardı.
    /// </param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task<int> ShowAsync(
        string ownerGroup,
        string? connectionString,
        CancellationToken cancellationToken)
    {
        var connection = connectionString
            ?? Environment.GetEnvironmentVariable(McpCommandHandlers.ControlPlaneVariable);

        if (string.IsNullOrWhiteSpace(connection))
        {
            await Console.Error.WriteLineAsync(
                $"Veritabanı adresi yok: `--connection` verin ya da "
                + $"`{McpCommandHandlers.ControlPlaneVariable}` ortam değişkenini kurun.")
                .ConfigureAwait(false);

            return 2;
        }

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connection)
            .Options;

        // Tek koşumluk bir komut: havuz gerekmiyor, basit bir fabrika yetiyor.
        // Emsal `sigma sync`'in `SingleContextFactory`'si.
        var quotaOptions = new RcaQuotaOptions();
        var gate = new RcaQuotaGate(new SingleContextFactory(options), quotaOptions);

        RcaQuotaUsage usage;

        try
        {
            usage = await gate.UsageAsync(ownerGroup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Bağlantı hatası bir operatör hatası: yığın izi yerine ne olduğunu
            // söyleyip çıkış kodu veriyoruz.
            await Console.Error.WriteLineAsync($"Kota okunamadı: {error.Message}").ConfigureAwait(false);

            return 3;
        }

        Console.WriteLine(Format(usage, quotaOptions));

        return 0;
    }

    /// <summary>
    /// Raporun metni — <b>saf fonksiyon</b>, çünkü bekçinin ölçtüğü şey bu.
    ///
    /// <para>
    /// Ayrı ve <c>internal static</c> olması bilinçli: veritabanına bağlanmadan
    /// sınanabiliyor. Gövdeye gömülü olsaydı raporun kırılımı gerçekten bastığı
    /// ancak canlı Postgres'le ölçülebilirdi — yani §2 gereği bu turda hiç
    /// ölçülemezdi.
    /// </para>
    /// </summary>
    public static string Format(RcaQuotaUsage usage, RcaQuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>
        {
            $"grup            : {usage.OwnerGroup}",
            $"pencere         : {usage.WindowStart.ToString("u", CultureInfo.InvariantCulture)} →",
            $"günlük tavan    : {Describe(usage.Limit)}",
            $"kullanılan      : {usage.Used.ToString(CultureInfo.InvariantCulture)}",
            $"rezerv          : %{options.EventReservePercent.ToString(CultureInfo.InvariantCulture)}"
                + (options.EventReservePercent == 0 ? "  (kapalı)" : string.Empty),
            string.Empty,
            "kaynak başına tüketim ve ETKİN sınır:",
        };

        // KAYNAK KÜMESİ ENUM'DAN — `BySource` yalnızca tüketimi OLAN kaynakları
        // taşıyor (`GroupBy` boş grup üretmiyor). Yalnızca onu basmak, hiç
        // koşmamış bir kaynağı raporda GÖRÜNMEZ yapardı — ve "ajan hiç
        // koşmamış" ile "ajan raporda yok" aynı şeye inerdi.
        foreach (var source in Enum.GetValues<RcaTriggerSource>().Order())
        {
            var used = usage.BySource.TryGetValue(source, out var count) ? count : 0;

            var effective = RcaQuotaGate.EffectiveLimit(
                options.DailyPerGroup, options.EventReservePercent, source);

            // ETKİN SINIR KAYNAK BAŞINA basılıyor: rezerv açıkken `Alert` ile
            // diğerleri FARKLI sayı görüyor ve tek bir "limit" satırı bunu
            // görünmez kılardı.
            lines.Add(
                $"  {source.ToString().ToLowerInvariant(),-10} "
                + $"{used.ToString(CultureInfo.InvariantCulture),5} / {Describe(effective)}");
        }

        lines.Add(string.Empty);
        lines.Add(
            usage.BySource.Count == 0
                ? "Bu pencerede hiç koşum yok — rezerv kararı için veri henüz birikmedi."
                : "Rezerv yüzdesi bu dağılımdan seçilir; varsayılan 0 bir tercih değil, "
                    + "ölçülene kadar beklemek (T46 §5).");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// <c>0</c> sınırsız demek, sıfır değil — ve ikisini aynı basmak raporun
    /// yapabileceği en pahalı hata olurdu: <i>"tavan sıfır"</i> ile
    /// <i>"tavan yok"</i> zıt iş emri veriyor.
    /// </summary>
    private static string Describe(int limit) =>
        limit <= 0 ? "sınırsız" : limit.ToString(CultureInfo.InvariantCulture);
}

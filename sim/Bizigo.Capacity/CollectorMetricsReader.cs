using System.Globalization;

namespace Bizigo.Capacity;

/// <summary>
/// <b>Collector katmanı</b> — OTel Collector'ın <b>kendi</b> metrikleri.
/// Prometheus sergileme (exposition) metnini ayrıştırıyor; HTTP burada yok,
/// çağıran metni veriyor (aynı gerekçe: ayrıştırma konteynersiz sınanabilmeli).
///
/// <h3>Sayaçlar KÜMÜLATİF — tek okuma bir koşumu ölçmüyor</h3>
///
/// <para>
/// <c>otelcol_receiver_accepted_log_records</c> süreç ömrü boyunca artan bir
/// sayaç. Koşuma ait sayı iki okumanın <b>farkı</b>; tek okuma, collector'ın
/// açılıştan beri gördüğü her şeyi koşuma yazardı. Bu yüzden
/// <see cref="Delta"/> iki anlık görüntü istiyor.
/// </para>
///
/// <h3>Sayaç sıfırlanması bir kayıp gibi görünür — görünmemesi gerekiyor</h3>
///
/// <para>
/// Collector koşum ortasında yeniden başlarsa sayaç sıfırlanır ve fark
/// <b>negatif</b> çıkar. Negatifi sıfıra kırpmak, *"koşumda hiç kayıt
/// kabul edilmedi"* demek olurdu — yani bir <b>ölçüm arızası</b>, ürünün
/// kaybı olarak raporlanırdı. Bu tam olarak <c>LEDGER-LIMITED</c>'ın var olma
/// sebebi.
/// </para>
/// </summary>
public static class CollectorMetricsReader
{
    /// <summary>Kabul edilen log kaydı sayacı.</summary>
    public const string AcceptedMetric = "otelcol_receiver_accepted_log_records";

    /// <summary>
    /// Reddedilen log kaydı sayacı. <b>Ayrı okunuyor</b>: reddetmek düşürmekten
    /// farklı bir arıza — collector kaydı gördü ve <i>bilerek</i> almadı.
    /// </summary>
    public const string RefusedMetric = "otelcol_receiver_refused_log_records";

    /// <summary>
    /// Prometheus'un toplam (counter) metriklerine eklediği son ek. Uç elle
    /// yapılandırıldığında ortaya çıkıyor; gerekçesi <see cref="Parse"/>'ta.
    /// </summary>
    private const string TotalSuffix = "_total";

    /// <summary>
    /// Metrik ailesinin bütün etiketli serilerini <b>toplar</b>.
    ///
    /// <para>
    /// Toplama bilinçli: sayaç <c>receiver</c> ve <c>transport</c> etiketleriyle
    /// bölünüyor (<c>syslog/tcp</c>, <c>syslog/udp</c>, <c>otlp</c>) ve tek bir
    /// seriyi okumak, yükün başka bir alıcıdan girdiği koşumda <b>sessizce</b>
    /// eksik sayardı. Alıcı başına ayırmak ayrı bir soru ve bu ticket'ın
    /// sorusu değil.
    /// </para>
    ///
    /// <para>
    /// <b><c>_total</c> ekli ad da kabul ediliyor — ve bu ölçülmüş bir tuzak.</b>
    /// Collector Prometheus uçunu <b>kendi</b> kurduğunda
    /// <c>without_type_suffix</c> ve <c>without_units</c> <c>true</c> geliyor,
    /// yani ad <c>otelcol_receiver_accepted_log_records</c>. Ama uç
    /// <c>service::telemetry::metrics::readers</c> ile <b>elle</b>
    /// yapılandırıldığında o iki bayrak varsayılan olarak <b>ayarlanmıyor</b> ve
    /// aynı sayaç <c>..._log_records_total</c> adıyla yayılıyor. Yalnızca kısa
    /// adı arayan bir okuyucu o kurulumda <b>hiçbir seri bulmaz</b> ve
    /// <c>LEDGER-LIMITED</c> der — teşhisi zor, çünkü uç ayakta ve metrik
    /// gerçekten orada. İki ad da kabul ediliyor, ama <b>aynı koşumda ikisi
    /// birden toplanmıyor</b> (aşağıdaki sınır kontrolü).
    /// </para>
    ///
    /// <para>
    /// <b>Metrik hiç yoksa sıfır DEĞİL.</b> Collector metrik uçunu
    /// yayınlamıyorsa ya da kazıma penceresi boşsa cevap *"kabul edilmedi"*
    /// değil *"ölçemedim"*.
    /// </para>
    /// </summary>
    public static LedgerReading Parse(string? exposition, string metric)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        if (string.IsNullOrWhiteSpace(exposition))
        {
            return LedgerReading.Limited(
                LedgerLayer.Collector,
                metric,
                "metrik uçundan yanıt yok (uç yayınlanmıyor ya da erişilemiyor)");
        }

        var found = false;
        var total = 0d;

        foreach (var raw in exposition.Split('\n'))
        {
            var line = raw.Trim();

            // Yorum ve tip satırları (`# HELP`, `# TYPE`) atlanıyor: `# TYPE`
            // satırı metrik adını TAŞIYOR, yani atlanmazsa ad eşleşmesi
            // yanlış pozitif üretir.
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (!line.StartsWith(metric, StringComparison.Ordinal))
            {
                continue;
            }

            // Ad sınırı: `metric{...} 12`, `metric 12` ya da `metric_total …`.
            // Sınır kontrolü olmadan `..._log_records` deseni
            // `..._log_records_bytes` gibi BAŞKA bir sayacı da yakalar ve sayı
            // sessizce şişer.
            var rest = line[metric.Length..];

            if (rest.StartsWith(TotalSuffix, StringComparison.Ordinal))
            {
                rest = rest[TotalSuffix.Length..];
            }

            if (rest.Length == 0 || (rest[0] != ' ' && rest[0] != '{' && rest[0] != '\t'))
            {
                continue;
            }

            var value = rest[0] == '{'
                ? rest[(rest.IndexOf('}', StringComparison.Ordinal) + 1)..]
                : rest;

            var token = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (token.Length == 0
                || !double.TryParse(token[0], CultureInfo.InvariantCulture, out var parsed))
            {
                return LedgerReading.Limited(
                    LedgerLayer.Collector,
                    metric,
                    $"seri değeri okunamadı: '{line}'");
            }

            found = true;
            total += parsed;
        }

        return found
            ? LedgerReading.Measured(LedgerLayer.Collector, metric, (long)total)
            : LedgerReading.Limited(
                LedgerLayer.Collector,
                metric,
                "metrik sergilemede YOK — collector bu alıcı için hiç kayıt görmemiş olabilir, " +
                "ama sayacın hiç doğmaması ile sıfır olması ayırt edilemiyor.");
    }

    /// <summary>
    /// Koşuma ait sayı: <b>iki anlık görüntünün farkı</b>.
    ///
    /// <para>
    /// Uçlardan biri ölçülemediyse fark de ölçülemez — ve <b>gerekçe
    /// taşınıyor</b>, yeni bir cümle uydurulmuyor: kaybolan gerekçe, bir sonraki
    /// koşumda aynı kısıtı yeniden teşhis etmek demek.
    /// </para>
    /// </summary>
    public static LedgerReading Delta(LedgerReading before, LedgerReading after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!before.IsMeasured)
        {
            return LedgerReading.Limited(before.Layer, before.Source, "koşum ÖNCESİ: " + before.LimitReason);
        }

        if (!after.IsMeasured)
        {
            return LedgerReading.Limited(after.Layer, after.Source, "koşum SONRASI: " + after.LimitReason);
        }

        var delta = after.Value!.Value - before.Value!.Value;

        return delta < 0
            ? LedgerReading.Limited(
                after.Layer,
                after.Source,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"sayaç GERİ gitti ({before.Value} → {after.Value}): collector koşum ortasında " +
                    $"yeniden başlamış. Farkı sıfıra kırpmak, ölçüm arızasını ürünün kaybı olarak " +
                    $"raporlamak olurdu."))
            : LedgerReading.Measured(after.Layer, after.Source, delta);
    }
}

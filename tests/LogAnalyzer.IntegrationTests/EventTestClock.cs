using System.Globalization;
using LogAnalyzer.Commands.Seeding;
using LogAnalyzer.Parsing.Samples;

namespace LogAnalyzer.IntegrationTests;

/// <summary>
/// <b><c>events</c>'e satır yazan entegrasyon testlerinin sabit anlarının tek
/// kaynağı.</b>
///
/// <para>
/// <c>events</c> tablosunda <c>TTL toDateTime(ts) + INTERVAL 90 DAY</c> var ve
/// ClickHouse onu <b>kendi saatiyle</b> uyguluyor. Takvime çakılmış bir test
/// anı (eskiden 2026-08-16 … 2026-09-05) bu yüzden bir saatli bombaydı: yazıldığı
/// gün geçiyor, doksan gün sonra ClickHouse satırı hata vermeden kabul edip
/// siliyor ve test "0 satır" gibi sebebini söylemeyen bir sayıyla düşüyor.
/// Ölçüldü (clickhouse-server 26.7): süresi dolmuş tek satır insert'ten hemen
/// sonra görünüyor, iki saniye içinde kayboluyor — yani tek satırlık testler
/// önce kararsızlaşıyor, sonra kesin kırmızı yanıyor.
/// <c>GoldenSeedClickHouseTests</c> aynı kusurla "29 gün" diye düşmüştü.
/// </para>
///
/// <para>
/// <b>Testler duvar saatine bağlanmıyor.</b> Her test hâlâ tek bir sabit ana
/// göre konuşuyor ve sahte saatleri o anda duruyor; değişen yalnızca o anın
/// <i>nereden geldiği</i>: süreç başına bir kez, bugünden türetiliyor. Bir
/// koşum içinde değer değişmiyor, gece yarısını aşan bir koşum da aynı günü
/// görüyor.
/// </para>
///
/// <list type="bullet">
/// <item><b>Gün sınırına iniyor (UTC gece yarısı).</b> Bölüm adları
/// (<c>toYYYYMMDD</c>), ham arşiv yolları (<c>yyyy/MM/dd/HH</c>) ve saat dilimi
/// iddiaları takvime bağlı. Testler <see cref="Day"/>'e eski saatlerini ekliyor
/// ("17 Ağustos 10:00" → <c>Day.AddHours(10)</c>); günün içindeki konumlar ve
/// aralarındaki farklar aynen korunuyor.</item>
/// <item><b>Bir hafta geride.</b> Sabit anlar yazıldıkları gün duvar saatinin
/// gerisindeydi ve bazı testlerin anlatısı buna dayanıyor: sahte saat ile
/// <c>LogEvent.IngestedAt</c>'in varsayılanı (<c>UtcNow</c>) arasındaki fark
/// (<c>AlertChainTests</c>, <c>SigmaRuleChainTests</c>), günlere yayılan
/// tohumların geleceğe taşmaması (<c>McpQueryCancellationTests</c>: 400 bin
/// saniye ≈ 4,6 gün). Hafta, en uzun ileri yayılımı içine alıyor; hiçbir test
/// satırı duvar saatinin önüne düşmüyor.</item>
/// </list>
///
/// <para>
/// <b>Saklama süresi şemadan okunuyor</b> (<see cref="EventRetention"/>),
/// buraya ikinci kez yazılmıyor. <see cref="Day"/>'den <see cref="MaxLookBack"/>
/// kadar geriye giden bir an TTL dışına düşüyorsa sınıf ilk kullanımda
/// <b>sebebini söyleyerek</b> düşüyor: şema saklamayı kısalttığı gün testler
/// "0 satır" yerine bunu görür. Testlerin bugünkü en derin geri bakışı on gün
/// (<c>ScopeNegativeTests</c>'in korelasyon tabanı).
/// </para>
///
/// <para>
/// <b>Kapsam dışı:</b> <c>EventRetentionChainTests</c> buradan almıyor — onun
/// iddiası sink'in saati ile ClickHouse'un saatinin <b>aynı</b> kararı vermesi,
/// dolayısıyla sink'in sahte saati şimdiye (<see cref="SampleClock.Anchor"/>)
/// kuruluyor; bir hafta geride bir saat orada ölçümün kendisine karışırdı.
/// <c>change_events</c>'te TTL yok, ona yazan testler sabit anlarını koruyor.
/// Birim paketindeki <c>IntegrationEventClockTests</c>, <c>events</c>'e yazan bir
/// test dosyasına takvime çakılmış bir an geri girerse kırmızı yanıyor.
/// </para>
/// </summary>
internal static class EventTestClock
{
    /// <summary>Test gününün bugünden ne kadar geride durduğu.</summary>
    public static TimeSpan Lead => TimeSpan.FromDays(7);

    /// <summary>
    /// Testlerin <see cref="Day"/>'den en fazla ne kadar geriye yazabileceği —
    /// TTL kapısının sınadığı pay. Bugünkü en derin geri bakış on gün.
    /// </summary>
    public static TimeSpan MaxLookBack => TimeSpan.FromDays(30);

    /// <summary>
    /// Test günü: UTC gece yarısı, sürecin ilk kullanımından <see cref="Lead"/>
    /// kadar önce.
    /// </summary>
    public static DateTimeOffset Day { get; } = ComputeDay(TimeProvider.System.GetUtcNow());

    /// <summary><c>toYYYYMMDD(ts)</c> bölüm adı — <c>events</c>'in bölümleme anahtarı.</summary>
    public static string Partition(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Ham arşiv anahtarının saat öneki: <c>yyyy/MM/dd/HH</c>.</summary>
    public static string RawHour(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy/MM/dd/HH", CultureInfo.InvariantCulture);

    private static DateTimeOffset ComputeDay(DateTimeOffset now)
    {
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) - Lead;
        var retention = EventRetention.Read(DevStackSetup.RepoPath("db/clickhouse"));
        var deepest = day - MaxLookBack;

        if (!SampleClock.Survives(deepest, now, retention))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Test günü {day:yyyy-MM-dd} ve {MaxLookBack.TotalDays:0} günlük geri bakış, " +
                $"events saklamasının ({retention.TotalDays:0} gün) dışına düşüyor: en eski yazılabilir an " +
                $"{SampleClock.EarliestWritable(now, retention):yyyy-MM-dd HH:mm}Z, testlerin en eskisi " +
                $"{deepest:yyyy-MM-dd HH:mm}Z. ClickHouse bu satırları hata vermeden siler; " +
                $"{nameof(Lead)} ya da {nameof(MaxLookBack)} saklamaya göre yeniden seçilmeli."));
        }

        return day;
    }
}

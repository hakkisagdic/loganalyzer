using System.Text;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Samples;
using Bizigo.Simulators;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Saklama penceresi dışında kalan olayın kaybı (S02a).</b>
///
/// <para>
/// Bu dosyanın tamamı tek bir ölçülmüş olaydan doğdu. Simülatör collector'a 100
/// satır bastı; <c>/internal/ingest/stats</c> <c>accepted 100</c> ve
/// <c>processed 100</c> dedi, ClickHouse <c>query_log</c>'unda INSERT başarılı
/// göründü ve <c>written_rows</c> doluydu — <b>tabloda sıfır satır vardı.</b>
/// Hiçbir hata, hiçbir sayaç, hiçbir belirti yoktu; §7'nin tarif ettiği sınıfın
/// tam örneği.
/// </para>
///
/// <para>
/// Sebep: <c>events</c> tablosunda <c>TTL toDateTime(ts) + INTERVAL 90 DAY</c>
/// var, parser <c>ts</c>'yi satırdan çıkarıyor ve vendor örnekleri 2015–2022
/// tarihleri taşıyor. ClickHouse süresi dolmuş satırı parçayı oluştururken
/// atıyor <b>ama istemciye yazdım diyor</b>. Kanıt ölçüldü: tek bir INSERT
/// içinde 2020 ve 2026 tarihli iki satırdan yalnızca ikincisi tabloya girdi,
/// dönen sayı ikisini de saydı.
/// </para>
///
/// <para>
/// Sahadaki tetikleyicisi test verisi <b>değil</b>: saati yanlış bir cihaz,
/// retention'dan eski bir arşivin replay'i, ya da <c>ts</c> alanını yanlış
/// seçen bir parser. Sonuncusunda parser hatası görünür yanlış değer yerine
/// görünmez veri kaybına dönüşüyor — o yüzden düzeltme test verisiyle
/// bitmiyor, sayaç ürüne giriyor.
/// </para>
/// </summary>
public sealed class EventRetentionTests
{
    // SABİT bir an — duvar saati değil. §6: bir testin geçme sebebinin duvar
    // saatiyle ilgisi olmamalı. Kesimi buradan türetiyoruz, `DateTimeOffset.UtcNow`'dan değil.
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly EventSinkOptions Options = new();

    /// <summary>
    /// Yazımı her zaman başaran yazıcı: gönderilen satır sayısını olduğu gibi
    /// döndürüyor — <b>gerçek ClickHouse istemcisinin yaptığı da bu.</b> Sayaç
    /// hatasının kaynağı zaten burası: dönen sayı hayatta kalanı değil,
    /// gönderileni bildiriyor.
    /// </summary>
    private sealed class KabulEdenYazici : IEventWriter
    {
        public long SonGonderilen { get; private set; }

        public Task<WriteResult> WriteEventsAsync(
            IReadOnlyCollection<LogEvent> events,
            CancellationToken cancellationToken = default)
        {
            SonGonderilen = events.Count;
            return Task.FromResult(new WriteResult(events.Count, TimeSpan.Zero));
        }
    }

    private static (ClickHouseEventSink Sink, KabulEdenYazici Yazici) Kur()
    {
        var time = new FakeTimeProvider(Now);
        var yazici = new KabulEdenYazici();

        var sink = new ClickHouseEventSink(
            yazici,
            new EventNormalizer(time),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ClickHouseEventSink>.Instance,
            time);

        return (sink, yazici);
    }

    private static ParsedEvent Olay(DateTimeOffset timestamp) => new(
        new RawRecord
        {
            EventId = Guid.CreateVersion7(Now),
            ReceivedAt = Now,
            SourceKey = "10.1.1.1",
            OwnerGroup = OwnerGroups.Unassigned,
            SourceId = "s",
            Body = Encoding.UTF8.GetBytes("gövde"),
        },
        "gövde",
        "utf-8",
        new ResolvedSource("s", OwnerGroups.Unassigned, "firewall", "auto", string.Empty, IsKnown: false),
        new ParseResult
        {
            ParserId = "fortinet.fortigate.traffic",
            ParserVersion = "1",
            Status = ParseStatus.Ok,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal),
            Timestamp = timestamp,
        },
        DispatchTier.InventoryBound);

    /// <summary>
    /// <b>Pencere dışındaki satır sayılıyor ve yazılanlardan düşülüyor.</b>
    ///
    /// <para>
    /// Kırmızı yanabildiği ölçüldü: <c>Expired</c> artışı kaldırıldığında test
    /// <c>0 ≠ 3</c> ile düşüyor, düşme yalnızca <c>Written</c> iddiasıyla
    /// bırakıldığında ise <c>Written</c> 2 yerine 5 çıkıyor. İkisi ayrı ayrı
    /// yanıyor, yani tek bir iddia diğerini gizlemiyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pencere_disindaki_satir_yazildi_sayilmiyor()
    {
        var (sink, yazici) = Kur();

        var eski = Now - Options.EventRetention - TimeSpan.FromDays(1);
        var yeni = Now - TimeSpan.FromHours(1);

        await sink.HandleAsync(
            [Olay(eski), Olay(eski), Olay(yeni), Olay(eski), Olay(yeni)],
            TestContext.Current.CancellationToken);

        await sink.FlushAsync(TestContext.Current.CancellationToken);

        // Beşi de ClickHouse'a GÖNDERİLDİ — seçim buydu: ham arşiv duruyor,
        // parser ya da saat düzeldikten sonra K12 replay geri kazanabilsin.
        Assert.Equal(5, yazici.SonGonderilen);

        Assert.Equal(3, sink.Expired);

        // Kritik iddia: tabloya gerçekten giren satır sayısı. Düşülmeseydi
        // `Written` 5 derdi ve sayacın tamamı anlamsız olurdu.
        Assert.Equal(2, sink.Written);
        Assert.Equal(0, sink.Dropped);
    }

    /// <summary>
    /// Pencerenin içindeki satırlar hiçbir şeyi kırmızı yakmıyor.
    ///
    /// <para>
    /// Bu testin işi yanlış pozitifi engellemek: sağlıklı bir boru hattı
    /// <c>expired</c> raporlarsa sayaç ilk gerçek arızada güvenilmez olur.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pencere_icindeki_satir_temiz_geciyor()
    {
        var (sink, _) = Kur();

        // Tam sınırın hemen içi: kesim noktası kapsayıcı olmalı, yoksa
        // retention'ın son gününe düşen olaylar sebepsiz kayıp raporlanır.
        await sink.HandleAsync(
            [Olay(Now - Options.EventRetention + TimeSpan.FromMinutes(1)), Olay(Now)],
            TestContext.Current.CancellationToken);

        await sink.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, sink.Expired);
        Assert.Equal(2, sink.Written);
    }

    /// <summary>
    /// Yazım hiç olmadıysa satırlar <c>dropped</c>, <c>expired</c> değil.
    ///
    /// <para>
    /// İkisini birden saymak aynı satırı iki kez kaybetmiş göstermek olurdu ve
    /// uçtaki <c>unaccounted</c> farkı negatife düşerdi — yani fark sayacının
    /// kendisi bozulurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yazim_basarisizsa_ayni_satir_iki_kez_kaybedilmiyor()
    {
        var time = new FakeTimeProvider(Now);

        var sink = new ClickHouseEventSink(
            new PatlayanYazici(),
            new EventNormalizer(time),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ClickHouseEventSink>.Instance,
            time);

        var eski = Now - Options.EventRetention - TimeSpan.FromDays(1);

        await sink.HandleAsync([Olay(eski), Olay(eski)], TestContext.Current.CancellationToken);
        await sink.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sink.Dropped);
        Assert.Equal(0, sink.Expired);
        Assert.Equal(0, sink.Written);
    }

    private sealed class PatlayanYazici : IEventWriter
    {
        public Task<WriteResult> WriteEventsAsync(
            IReadOnlyCollection<LogEvent> events,
            CancellationToken cancellationToken = default) =>
            Task.FromException<WriteResult>(new InvalidOperationException("ClickHouse yok."));
    }

    /// <summary>
    /// <b>Sink'in penceresi ile tablonun TTL'i aynı sayıyı söylüyor.</b>
    ///
    /// <para>
    /// Ayrıştıkları gün sayaç ya olmayan bir kaybı raporlar ya da gerçek kaybı
    /// göremez; ikisi de sayacı işe yaramaz kılar ve <b>hiçbiri hata
    /// üretmez</b>. Bu deponun en pahalı hata sınıfı tam olarak bu.
    /// </para>
    ///
    /// <para>
    /// DDL kaynak, seçenek türev: TTL değiştiğinde burası kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sinkin_penceresi_tablonun_ttl_i_ile_ayni()
    {
        var ddl = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "db", "clickhouse", "0001_events.sql"));

        var match = Regex.Match(ddl, @"TTL\s+toDateTime\(ts\)\s*\+\s*INTERVAL\s+(?<gun>\d+)\s+DAY");

        Assert.True(
            match.Success,
            "0001_events.sql'de `TTL toDateTime(ts) + INTERVAL <n> DAY` bulunamadı. " +
            "TTL kaldırıldıysa EventSinkOptions.EventRetention da anlamını kaybetti; " +
            "silinmediyse sink olmayan bir kaybı raporlamaya devam eder.");

        Assert.Equal(
            double.Parse(match.Groups["gun"].Value, System.Globalization.CultureInfo.InvariantCulture),
            new EventSinkOptions().EventRetention.TotalDays);
    }

    /// <summary>
    /// <b>Simülatörün basacağı hiçbir satır eski tarih taşımıyor.</b>
    ///
    /// <para>
    /// Kaybın simülatör kaynaklı olanını kapatan bekçi. Basıcı örnek satırı
    /// olduğu gibi tele verdiğinde 2020 tarihli bir satır basıyordu ve o satır
    /// tabloya hiç ulaşmıyordu — yani S02'nin ölçmek istediği şeyi ölçmesi
    /// <b>imkânsızdı</b> ve bunu hiçbir şey söylemiyordu.
    /// </para>
    ///
    /// <para>
    /// <b>Sabit bir an kullanılıyor, <c>UtcNow</c> değil.</b> "Örnek dosyaların
    /// tarihi son 90 gün içinde olsun" biçiminde bir bekçi kendiliğinden, hiçbir
    /// kod değişmeden bir gün kırmızı yanardı — §6'nın yasakladığı şey tam
    /// olarak bu. Sınanan şey dosyanın tarihi değil, <b>kaydırmanın çalıştığı</b>.
    /// </para>
    ///
    /// <para>
    /// Kırmızı yanabildiği ölçüldü: <see cref="SyslogEmitter.WireLine"/>
    /// kaydırmayı bırakıp satırı olduğu gibi döndürdüğünde bu test düşüyor.
    /// İlk hâlinde bekçi kaydırıcıyı doğrudan çağırıyordu ve aynı ölçüm
    /// <b>yeşil</b> kalmıştı — bekçinin kendisi de ölçülmek zorunda.
    /// </para>
    /// </summary>
    [Fact]
    public void Simulatorun_bastigi_satirlar_eski_tarih_tasimiyor()
    {
        var results = SimulatorProfileStore.LoadAll(
            Path.Combine(RepositoryLayout.Root, "catalog", "simulators"),
            RepositoryLayout.Root);

        Assert.NotEmpty(results);

        // Çıplak yıl DEĞİL, tarih bağlamındaki yıl aranıyor. İlk hâli çıplak yıl
        // arıyordu ve dört yanlış pozitif verdi — ölçüldü: `10.10.10.10/1985`
        // bir UDP portu, `2001:db8::` ve `2001:470::` IPv6 önekleri. Üçü de
        // tarih değil ve satırların damgaları doğru kaydırılmıştı.
        //
        // Yanlış pozitif veren bir bekçi, gerçek kırmızıyı da gürültüye çevirir.
        var tarihler = new[]
        {
            new Regex(@"(?<yil>\d{4})-\d{2}-\d{2}"),          // 2020-04-23 / 2022-04-12T
            new Regex(@"/(?<yil>\d{4}):\d{2}:\d{2}:\d{2}"),   // 25/Oct/2016:14:49:33
            new Regex(@"[A-Za-z]{3} {1,2}\d{1,2} (?<yil>\d{4}) \d{2}:"), // Oct 10 2018 12:34:56
        };

        var ihlal = new List<string>();

        foreach (var profile in results.Select(r => r.Profile).Where(p => p.Syslog is not null))
        {
            foreach (var sample in profile.Syslog!.Samples)
            {
                var path = Path.Combine(RepositoryLayout.Root, sample);

                foreach (var line in File.ReadAllLines(path).Where(l => l.Trim().Length > 0))
                {
                    // Kaydırıcı DEĞİL, BASICI çağrılıyor. İlk hâli kaydırıcıyı
                    // doğrudan çağırıyordu ve basıcıdan kaydırma kaldırıldığında
                    // yeşil kalıyordu — ölçüldü.
                    var wire = SyslogEmitter.WireLine(line, Now);

                    ihlal.AddRange(tarihler
                        .SelectMany(r => r.Matches(wire).Select(m => m.Groups["yil"].Value))
                        .Where(y => int.Parse(y, System.Globalization.CultureInfo.InvariantCulture) < Now.Year)
                        .Select(y => $"{profile.Id} · {Path.GetFileName(path)} · '{y}' → {wire[..Math.Min(90, wire.Length)]}"));
                }
            }
        }

        Assert.True(
            ihlal.Count == 0,
            $"{ihlal.Count} satır kaydırılmamış eski tarih taşıyor; tabloya hiç ulaşmazlar:\n  " +
            string.Join("\n  ", ihlal.Take(10)));
    }
}

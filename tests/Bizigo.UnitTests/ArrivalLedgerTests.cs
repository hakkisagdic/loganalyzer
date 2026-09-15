using System.Runtime.InteropServices;
using Bizigo.Capacity;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>B02 — üç katmanlı varış defteri.</b>
///
/// <para>
/// İki iddia sınanıyor ve ikincisi asıl olan (kapasite belgesi §5): (1) üç sayı
/// tek raporda ve <b>ayrıştıklarında hangi katmanın suçlandığı yazılı</b>;
/// (2) sayaçlardan biri güvenilmezse rapor <b>kayıp demiyor,
/// <c>LEDGER-LIMITED</c> diyor</b>.
/// </para>
///
/// <para>
/// <b>Konteyner yok</b> (§2): defterin hiçbir proje referansı yok ve ürün
/// katmanı bir <i>sonda</i> sözleşmesiyle temsil ediliyor, yani ayrıştırıcılar
/// ve hüküm saf fonksiyon olarak sınanıyor. Gerçek ClickHouse/S3/collector
/// bağlantısı <c>ArrivalLedgerIntegrationTests</c>'te — <b>yazıldı,
/// koşturulmadı</b>.
/// </para>
/// </summary>
public sealed class ArrivalLedgerTests
{
    // Çekirdeğin kendi başlığı (`net/ipv4/udp.c` · `udp4_seq_show`). Başlık
    // satırı da fixture'ın parçası: ayrıştırıcı onu ATLAMAK zorunda ve
    // atlamadığı gün `drops` yerine `pointer` okurdu.
    private const string ProcNetUdpFixture = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
         3175: 00000000:1415 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 41120 2 ffff8ec 17
         3176: 0100007F:1415 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 41121 2 ffff8ed 5
         3177: 00000000:0035 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 41122 2 ffff8ee 900
        """;

    // Gerçekte görülen hâl: sayaç işaretli basılıyor ve taşabiliyor.
    private const string ProcNetUdpNegative = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
         51808: 00000000:1415 00000000:0000 07 00000000:04FFFEC0 00:00000000 00000000     0        0 3815546609 2 ffff88103f9fd500 -1437668106
        """;

    private const string NetstatFixture = """
        UDP Statistics for IPv4

          Datagrams Received                  = 4126
          No Ports                            = 12
          Receive Errors                      = 7
          Datagrams Sent                      = 3011

        UDP Statistics for IPv6

          Datagrams Received                  = 90
          No Ports                            = 0
          Receive Errors                      = 3
          Datagrams Sent                      = 88
        """;

    private const string ExpositionFixture = """
        # HELP otelcol_receiver_accepted_log_records Number of log records successfully pushed into the pipeline.
        # TYPE otelcol_receiver_accepted_log_records counter
        otelcol_receiver_accepted_log_records{receiver="syslog/udp",transport="udp",service_name="otelcol-contrib"} 4000
        otelcol_receiver_accepted_log_records{receiver="syslog/tcp",transport="tcp",service_name="otelcol-contrib"} 1000
        # TYPE otelcol_receiver_refused_log_records counter
        otelcol_receiver_refused_log_records{receiver="syslog/udp",transport="udp"} 0
        """;

    // ---------------------------------------------------------------- tel

    /// <summary>
    /// <c>drops</c> <b>son alan</b> — kolon indeksiyle değil son belirteçle
    /// okunuyor, yani <c>/proc/net/udp6</c> ve kolon eklenmesine dayanıklı.
    /// Aynı porta bağlı iki satır (joker + loopback) <b>toplanıyor</b>.
    /// </summary>
    [Fact]
    public void Proc_net_udp_drops_son_alandan_okunuyor()
    {
        var reading = WireDropReader.ParseProcNetUdp(ProcNetUdpFixture, 5141);

        Assert.True(reading.IsMeasured, reading.LimitReason);

        // 0x1415 = 5141: iki satır (17 + 5). 0x0035 = 53 satırı BAŞKA bir port
        // ve karışmıyor — karışsaydı DNS'in düşürmeleri bizim koşumumuza
        // yazılırdı.
        Assert.Equal(22, reading.Value);
    }

    /// <summary>
    /// <b>Port bulunamazsa sıfır DEĞİL, kısıt.</b> Bu düzenekte varsayılan hâl:
    /// collector container'da koşuyorsa host'un tablosunda o soket <b>yok</b>.
    /// Sıfır dönmek "düşürme olmadı" iddiası olurdu — oysa gözlemci yanlış yere
    /// bakıyor.
    /// </summary>
    [Fact]
    public void Port_bulunamazsa_sifir_degil_kisit()
    {
        var reading = WireDropReader.ParseProcNetUdp(ProcNetUdpFixture, 9999);

        Assert.False(reading.IsMeasured);
        Assert.Contains("ağ ad", reading.LimitReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Negatif <c>drops</c> kırpılmıyor.</b> Sıfıra kırpmak "düşürme yok"
    /// demek olurdu ve gerçek muhtemelen tam tersi.
    /// </summary>
    [Fact]
    public void Negatif_drops_kirpilmiyor_kisit_oluyor()
    {
        var reading = WireDropReader.ParseProcNetUdp(ProcNetUdpNegative, 5141);

        Assert.False(reading.IsMeasured);
        Assert.Contains("NEGATİF", reading.LimitReason, StringComparison.Ordinal);
    }

    /// <summary>IPv4 ve IPv6 bölümleri toplanıyor (çift yığın dinleyici).</summary>
    [Fact]
    public void Netstat_receive_errors_iki_yigindan_toplaniyor()
    {
        var reading = WireDropReader.ParseNetstatUdp(NetstatFixture);

        Assert.True(reading.IsMeasured, reading.LimitReason);
        Assert.Equal(10, reading.Value);
    }

    /// <summary>
    /// <b>Yerelleştirilmiş <c>netstat</c> çıktısı sıfır üretmiyor.</b> Bu
    /// deponun <c>tr-TR</c> tuzağının ağ katmanındaki karşılığı: Türkçe bir
    /// Windows'ta etiket yok, ve sıfır dönen bir okuyucu o makinede *"tel
    /// temiz"* diye okunurdu.
    /// </summary>
    [Fact]
    public void Yerellestirilmis_netstat_ciktisi_kisit_uretiyor()
    {
        var reading = WireDropReader.ParseNetstatUdp("""
            IPv4 için UDP İstatistikleri

              Alınan Veri Birimleri               = 4126
              Alma Hataları                       = 7
            """);

        Assert.False(reading.IsMeasured);
        Assert.Contains("yerelleştirilmiş", reading.LimitReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Bu makinede tel katmanı ölçülemiyor ve defter bunu söylüyor.</b>
    ///
    /// <para>
    /// Hedef Linux + Windows; geliştirme makinesi macOS. Yani kısıt yolu bu
    /// depoda <b>gerçekten koşan</b> tek yol ve koştuğu burada ölçülüyor —
    /// <c>machine-resources.sh</c>'in Linux'ta <c>100</c>/<c>0</c> basmasının
    /// panzehiri bir yorum değil bu test.
    /// </para>
    ///
    /// <para>
    /// Test platforma göre iki iddiadan birini kuruyor; ikisini birden
    /// yazmak, koştuğu platformda hangisinin sınandığını belirsiz bırakırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Desteklenmeyen_platformda_uydurma_sayi_yok()
    {
        var reading = WireDropReader.Read(5141, () => ProcNetUdpFixture, () => NetstatFixture);

        if (WireDropReader.Supported)
        {
            Assert.True(reading.IsMeasured, reading.LimitReason);
            return;
        }

        Assert.False(reading.IsMeasured);
        Assert.Contains("okuyucusu YOK", reading.LimitReason, StringComparison.Ordinal);

        // Ve kısıt hükme kadar taşınıyor: ürün katmanı temiz olsa bile
        // yerleştirilecek bir boşluk varsa hüküm LEDGER-LIMITED.
        Assert.Equal(
            LedgerVerdict.Limited,
            Ledger(expected: 100, wire: reading, accepted: 100, refused: 0, archived: 99, searchable: 99).Verdict);
    }

    // ------------------------------------------------------------ collector

    /// <summary>
    /// Bir metrik ailesinin <b>bütün etiketli serileri</b> toplanıyor: yük
    /// UDP'den de TCP'den de girebilir ve tek seriyi okumak sessizce eksik
    /// sayardı.
    /// </summary>
    [Fact]
    public void Metrik_ailesinin_butun_serileri_toplaniyor()
    {
        var accepted = CollectorMetricsReader.Parse(
            ExpositionFixture, CollectorMetricsReader.AcceptedMetric);

        Assert.True(accepted.IsMeasured, accepted.LimitReason);
        Assert.Equal(5000, accepted.Value);

        // Sıfır bir DEĞER: serisi var, sayısı sıfır. "Metrik yok" ile aynı şey
        // değil ve bu ayrım alttaki testin konusu.
        var refused = CollectorMetricsReader.Parse(
            ExpositionFixture, CollectorMetricsReader.RefusedMetric);

        Assert.True(refused.IsMeasured);
        Assert.Equal(0, refused.Value);
    }

    /// <summary>
    /// <b><c># TYPE</c> satırı metrik adını taşıyor</b> ve atlanmazsa ad
    /// eşleşmesi yanlış pozitif üretir. Ayrıca ad sınırı kontrol ediliyor:
    /// önek eşleşmesi <b>başka</b> bir sayacı (<c>..._bytes</c>) yakalarsa sayı
    /// sessizce şişer.
    /// </summary>
    [Fact]
    public void Yorum_satiri_ve_ad_siniri_yanlis_toplama_uretmiyor()
    {
        var reading = CollectorMetricsReader.Parse("""
            # TYPE otelcol_receiver_accepted_log_records counter
            otelcol_receiver_accepted_log_records_bytes{receiver="syslog/udp"} 777
            otelcol_receiver_accepted_log_records{receiver="syslog/udp"} 12
            """,
            CollectorMetricsReader.AcceptedMetric);

        Assert.True(reading.IsMeasured, reading.LimitReason);
        Assert.Equal(12, reading.Value);
    }

    /// <summary>
    /// <b><c>_total</c> ekli ad da okunuyor — ölçülmüş bir tuzak.</b>
    ///
    /// <para>
    /// Collector metrik uçunu <b>kendi</b> kurduğunda ad kısa geliyor
    /// (<c>without_type_suffix</c> varsayılan <c>true</c>). Uç
    /// <c>service::telemetry::metrics::readers</c> ile <b>elle</b>
    /// yapılandırıldığında — yani bizim <c>deploy/otel/collector.yaml</c>'da
    /// yaptığımız şey — o bayrak varsayılan olarak ayarlanmıyor ve aynı sayaç
    /// <c>..._total</c> adıyla yayılıyor.
    /// </para>
    ///
    /// <para>
    /// Yalnızca kısa adı arayan bir okuyucu o kurulumda <b>hiçbir seri bulmaz</b>
    /// ve <c>LEDGER-LIMITED</c> der: uç ayakta, metrik orada, defter kör.
    /// Yapılandırma iki bayrağı <b>ayarlıyor</b>, ama okuyucu yine de iki adı da
    /// kabul ediyor — yapılandırmanın bir gün değişmesi defterin körleşmesi
    /// olmamalı.
    /// </para>
    /// </summary>
    [Fact]
    public void Total_ekli_ad_da_okunuyor()
    {
        var reading = CollectorMetricsReader.Parse(
            """
            # TYPE otelcol_receiver_accepted_log_records_total counter
            otelcol_receiver_accepted_log_records_total{receiver="syslog/udp"} 4000
            otelcol_receiver_accepted_log_records_total{receiver="syslog/tcp"} 1000
            """,
            CollectorMetricsReader.AcceptedMetric);

        Assert.True(reading.IsMeasured, reading.LimitReason);
        Assert.Equal(5000, reading.Value);
    }

    /// <summary>
    /// Metrik uçtan yanıt gelmezse ya da metrik sergilemede yoksa <b>sıfır
    /// değil kısıt</b>: sayacın hiç doğmaması ile sıfır olması ayırt
    /// edilemiyor.
    /// </summary>
    [Fact]
    public void Metrik_ucu_erisilemezse_kisit()
    {
        Assert.False(CollectorMetricsReader
            .Parse(null, CollectorMetricsReader.AcceptedMetric).IsMeasured);

        var eksik = CollectorMetricsReader.Parse(
            "# TYPE up gauge\nup 1\n", CollectorMetricsReader.AcceptedMetric);

        Assert.False(eksik.IsMeasured);
        Assert.Contains("ayırt edilemiyor", eksik.LimitReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sayaç sıfırlanması kayıp gibi görünmüyor.</b> Collector koşum
    /// ortasında yeniden başlarsa fark negatif çıkıyor; sıfıra kırpmak *"hiç
    /// kayıt kabul edilmedi"* demek, yani <b>ölçüm arızasını ürünün kaybı
    /// olarak</b> raporlamak olurdu.
    /// </summary>
    [Fact]
    public void Sayac_sifirlanmasi_kayip_olarak_raporlanmiyor()
    {
        var delta = CollectorMetricsReader.Delta(
            LedgerReading.Measured(LedgerLayer.Collector, CollectorMetricsReader.AcceptedMetric, 9000),
            LedgerReading.Measured(LedgerLayer.Collector, CollectorMetricsReader.AcceptedMetric, 120));

        Assert.False(delta.IsMeasured);
        Assert.Contains("yeniden başlamış", delta.LimitReason, StringComparison.Ordinal);

        // Ve normal hâl fark alıyor: kümülatif sayaç tek okumayla koşumu
        // ölçmüyor.
        var normal = CollectorMetricsReader.Delta(
            LedgerReading.Measured(LedgerLayer.Collector, CollectorMetricsReader.AcceptedMetric, 9000),
            LedgerReading.Measured(LedgerLayer.Collector, CollectorMetricsReader.AcceptedMetric, 14000));

        Assert.Equal(5000, normal.Value);
    }

    /// <summary>
    /// Kısıt gerekçesi <b>taşınıyor</b>, yeni bir cümle uydurulmuyor: kaybolan
    /// gerekçe, bir sonraki koşumda aynı kısıtı yeniden teşhis etmek demek.
    /// </summary>
    [Fact]
    public void Kisit_gerekcesi_farkta_kayboluyor_degil()
    {
        var delta = CollectorMetricsReader.Delta(
            LedgerReading.Limited(LedgerLayer.Collector, "m", "kazıma penceresi boş"),
            LedgerReading.Measured(LedgerLayer.Collector, "m", 5));

        Assert.False(delta.IsMeasured);
        Assert.Contains("koşum ÖNCESİ", delta.LimitReason, StringComparison.Ordinal);
        Assert.Contains("kazıma penceresi boş", delta.LimitReason, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- hüküm

    /// <summary>Beklenen her satır eşleşti — üç katman da temiz.</summary>
    [Fact]
    public void Tutarli_kosum()
    {
        var ledger = Ledger(1000, Drops(0), accepted: 1000, refused: 0, archived: 1000, searchable: 1000);

        Assert.Equal(LedgerVerdict.Consistent, ledger.Verdict);
        Assert.Empty(ledger.Attributions);
    }

    /// <summary>
    /// <b>Kayıp bir katmana yerleşiyor ve suçlanan katman rapora YAZILIYOR</b> —
    /// bitti ölçütünün birinci yarısı. Üç ayrı arıza bugün aynı görünüyor;
    /// bu üç test onları ayırıyor.
    /// </summary>
    [Theory]
    // tel: collector 40 satırı HİÇ görmedi ve sayaç 40 düşürme bildiriyor.
    [InlineData(40, 960, 0, 960, 960, LedgerLayer.Wire, "düşürme")]
    // collector: kayıtları gördü ve 40'ını BİLEREK almadı.
    [InlineData(0, 960, 40, 960, 960, LedgerLayer.Collector, "ret")]
    // ürün: collector hepsini kabul etti, arşivde 40 eksik.
    [InlineData(0, 1000, 0, 960, 960, LedgerLayer.Product, "boru hattında")]
    // ürün: arşivde hepsi var, `events`'te 40 eksik — DAYANIKLI ama ARANAMAZ.
    [InlineData(0, 1000, 0, 1000, 960, LedgerLayer.Product, "ARANAMIYOR")]
    public void Kayip_bir_katmana_yerlesiyor(
        long drops,
        long accepted,
        long refused,
        long archived,
        long searchable,
        LedgerLayer beklenen,
        string gerekceParcasi)
    {
        var ledger = Ledger(1000, Drops(drops), accepted, refused, archived, searchable);

        Assert.Equal(LedgerVerdict.LossLocated, ledger.Verdict);

        var attribution = Assert.Single(ledger.Attributions);

        Assert.Equal(beklenen, attribution.Layer);
        Assert.Equal(40, attribution.Count);
        Assert.Contains(gerekceParcasi, attribution.Reason, StringComparison.Ordinal);

        // Rapor tek metin ve suçlanan katmanı YAZIYOR.
        Assert.Contains("suçlanan katman: " + beklenen, ledger.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Defterin yazılı kör noktası:</b> collector beklenenden az kayıt gördü
    /// ve OS sayacı düşürme bildirmiyor. Kayıp çekirdek ile collector arasında
    /// ve defter burayı <b>kapatamıyor</b> — paket yakalama kapatırdı.
    ///
    /// <para>
    /// Bunu ürüne yıkmak en kolay ve en yanlış hamle olurdu: ürün gördüğü her
    /// şeyi yazmış.
    /// </para>
    /// </summary>
    [Fact]
    public void Cekirdek_collector_arasindaki_kayip_ucuncu_hal()
    {
        var ledger = Ledger(1000, Drops(0), accepted: 960, refused: 0, archived: 960, searchable: 960);

        Assert.Equal(LedgerVerdict.Uncertain, ledger.Verdict);
        Assert.Empty(ledger.Attributions);
        Assert.Contains("KAPATAMIYOR", ledger.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("suçlanan katman", ledger.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Fazla satır da üçüncü hâl.</b> Çift yazma ile aynı pencereye düşen
    /// yabancı trafik defterde <b>aynı</b> görünüyor; kayba saymak sayıyı
    /// bozardı, tutarlı saymak arızayı gizlerdi.
    /// </summary>
    [Fact]
    public void Beklenenden_fazla_satir_kayip_degil_belirsiz()
    {
        var ledger = Ledger(1000, Drops(0), accepted: 1000, refused: 0, archived: 1000, searchable: 1040);

        Assert.Equal(LedgerVerdict.Uncertain, ledger.Verdict);
        Assert.Contains("FAZLA", ledger.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Tel sayacı düşürme bildiriyor ama boşluğu AÇIKLAMIYOR.</b>
    ///
    /// <para>
    /// Bu test bir kusurdan doğdu ve kusur ilk koşumda yakalandı: defterin ilk
    /// hâli düşürme sayacı sıfırdan büyük olduğu için kaybı <b>tele</b>
    /// yazıyordu. Oysa collector beklenen kadar kayıt görmüşse o satırlar
    /// tele varmış demektir; sayaçtaki düşürmeler <b>aynı porta gelen başka
    /// trafiğe</b> ait olabilir — <c>/proc/net/udp</c> sayacı sokete ait, bizim
    /// koşumumuza değil.
    /// </para>
    ///
    /// <para>
    /// <b>Yanlış katmanı suçlamak hiç suçlamamaktan kötü:</b> arama yanlış
    /// yerde başlar ve gerçek arıza (burada: dayanıklı yazılmış ama aranamayan
    /// satır) gözden kaçar. O yüzden düşürme <b>not olarak</b> raporda duruyor,
    /// ama yerleştirmeye girmiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Boslugu_aciklamayan_dusurme_tele_yazilmiyor()
    {
        var ledger = Ledger(1000, Drops(10), accepted: 1000, refused: 0, archived: 1000, searchable: 960);

        Assert.Equal(LedgerVerdict.LossLocated, ledger.Verdict);

        var attribution = Assert.Single(ledger.Attributions);

        Assert.Equal(LedgerLayer.Product, attribution.Layer);
        Assert.Equal(40, attribution.Count);
        Assert.Contains("ARANAMIYOR", attribution.Reason, StringComparison.Ordinal);

        // Düşürme yok sayılmıyor: rapor onu NOT olarak taşıyor.
        Assert.Contains("bu koşumun satırları değil", ledger.Rationale, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ LEDGER-LIMITED

    /// <summary>
    /// <b>Boşluk varken sayaç okunamıyorsa rapor kayıp DEMİYOR.</b> Dış aracın
    /// <c>OBSERVER-LIMITED</c>'ının karşılığı: suçu ürüne yıkmak, ölçüm
    /// aracının kendi körlüğünü hedefin kaybı olarak raporlamak olurdu.
    /// </summary>
    [Fact]
    public void Sayac_okunamazsa_kayip_degil_LEDGER_LIMITED()
    {
        var ledger = Ledger(
            1000,
            LedgerReading.Limited(LedgerLayer.Wire, WireDropReader.ProcNetUdp, "başka ağ ad alanı"),
            accepted: 1000,
            refused: 0,
            archived: 960,
            searchable: 960);

        Assert.Equal(LedgerVerdict.Limited, ledger.Verdict);
        Assert.Empty(ledger.Attributions);
        Assert.Contains("LEDGER-LIMITED", ledger.Report(), StringComparison.Ordinal);
        Assert.Contains("başka ağ ad alanı", ledger.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Boşluk YOKKEN okunamayan sayaç hükmü düşürmüyor</b> — ama rapora
    /// <b>uyarı olarak yazılıyor</b>.
    ///
    /// <para>
    /// Yargı çağrısı ve gerekçesi: yerleştirilecek bir kayıp yok, o yüzden
    /// *"ölçemedim"* demek koşumu boşa çıkarırdı. Yazılmazsa bir sonraki koşum
    /// aynı körlükle koşar ve kimse bilmez — o yüzden hüküm
    /// <see cref="LedgerVerdict.Consistent"/> olsa bile sayaç raporda görünüyor.
    /// </para>
    ///
    /// <para>
    /// İddianın dayanağı <c>ProductArchived</c>'in bir <b>eşleşme</b> olması:
    /// düz bir sayım olsaydı "biri kayıp, biri iki kez" hâli buradan sessizce
    /// geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Bosluk_yokken_okunamayan_sayac_uyari_olarak_yaziliyor()
    {
        var ledger = Ledger(
            1000,
            LedgerReading.Limited(LedgerLayer.Wire, WireDropReader.ProcNetUdp, "başka ağ ad alanı"),
            accepted: 1000,
            refused: 0,
            archived: 1000,
            searchable: 1000);

        Assert.Equal(LedgerVerdict.Consistent, ledger.Verdict);
        Assert.Contains("UYARI · okunamayan sayaç", ledger.Report(), StringComparison.Ordinal);
        Assert.Contains(WireDropReader.ProcNetUdp, ledger.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ürün katmanı okunamıyorsa defterin söyleyecek hiçbir şeyi yok — sonuç
    /// kolonu o.
    /// </summary>
    [Fact]
    public void Urun_katmani_okunamazsa_hukum_verilmiyor()
    {
        var ledger = new ArrivalLedger(
            "run-1",
            1000,
            Drops(0),
            LedgerReading.Measured(LedgerLayer.Collector, "accepted", 1000),
            LedgerReading.Measured(LedgerLayer.Collector, "refused", 0),
            LedgerReading.Measured(LedgerLayer.Product, "raw_manifest", 1000),
            LedgerReading.Limited(LedgerLayer.Product, "events", "ClickHouse sorgusu zaman aşımına uğradı"));

        Assert.Equal(LedgerVerdict.Limited, ledger.Verdict);
        Assert.Contains("zaman aşımına", ledger.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gerekçesiz bir kısıt, kısıtın kendisinden kötü: rapor *"ölçemedim"* der
    /// ve okuyan <b>neyi</b> düzelteceğini bilemez. O yüzden fabrika boş
    /// gerekçeyi reddediyor.
    /// </summary>
    [Fact]
    public void Gerekcesiz_kisit_yazilamiyor()
    {
        Assert.Throws<ArgumentException>(
            static () => LedgerReading.Limited(LedgerLayer.Wire, "kaynak", "   "));
    }

    /// <summary>
    /// Rapor <b>tek metin</b> ve üç katmanın beşi de okumasıyla içinde —
    /// bitti ölçütü "üç sayı tek raporda" diyor ve bu onun sınavı.
    /// </summary>
    [Fact]
    public void Rapor_bes_okumanin_hepsini_tasiyor()
    {
        var report = Ledger(1000, Drops(0), 1000, 0, 1000, 1000).Report();

        Assert.Contains("koşum=run-1", report, StringComparison.Ordinal);
        Assert.Contains(WireDropReader.ProcNetUdp, report, StringComparison.Ordinal);
        Assert.Contains("accepted", report, StringComparison.Ordinal);
        Assert.Contains("refused", report, StringComparison.Ordinal);
        Assert.Contains("raw_manifest", report, StringComparison.Ordinal);
        Assert.Contains("events", report, StringComparison.Ordinal);
        Assert.Contains("hüküm: CONSISTENT", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Platform beyanı ile okuyucunun davranışı <b>ayrışamıyor</b>:
    /// <see cref="WireDropReader.Supported"/> "okunabilir" diyorsa okuma
    /// gerçekten ölçüm dönmeli. Ayrışırsa koşum tel katmanının ölçülebildiğini
    /// sanarak başlar ve sonunda öğrenir.
    /// </summary>
    [Fact]
    public void Platform_beyani_okuyucunun_davranisiyla_ortusuyor()
    {
        Assert.Equal(
            WireDropReader.Supported,
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        Assert.Equal(
            WireDropReader.Supported,
            WireDropReader.Read(5141, () => ProcNetUdpFixture, () => NetstatFixture).IsMeasured);
    }

    // ---------------------------------------------------------- yapılandırma

    /// <summary>
    /// <b>Metrik ucu İKİ değişiklik istiyor ve biri eksikse sessizce
    /// çalışmıyor</b> — o yüzden ikisi tek bekçide.
    ///
    /// <para>
    /// Ölçüldü: defterin ikinci katmanı yazılmadan önce collector'ın kendi
    /// metrikleri makineden <b>erişilemiyordu</b>, ve sebebi iki bağımsız
    /// eksiklikti. (1) Varsayılan uç <c>127.0.0.1:8888</c> — container'ın kendi
    /// loopback'i, yani port yayınlansa bile host ulaşamıyor. (2) Compose
    /// 8888'i hiç yayınlamıyordu.
    /// </para>
    ///
    /// <para>
    /// <b>Neden bir birim testi:</b> bu depoda yapılandırma dosyaları
    /// birleştirmenin kurbanı oldu ve kırığı yalnızca yığını gerçekten kaldıran
    /// gördü (<c>CLAUDE.md</c> §5 — iki ajan compose'a ayrı ayrı <c>redis</c>
    /// ekledi, YAML ayrıştırılamaz hâle geldi, derleme ve testler yeşil kaldı).
    /// <c>docker compose config --quiet</c> söz dizimini doğruluyor, <b>bu
    /// bağı</b> doğrulamıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Eski biçim ayrıca reddediliyor:</b>
    /// <c>service::telemetry::metrics::address</c> collector v0.123.0'dan beri
    /// <b>sessizce yok sayılıyor</b> ve bu imaj 0.159.0. Yani o satırı yazmak
    /// "yapılandırdım" sanmak ve hiçbir şey değiştirmemek olurdu — tam olarak
    /// §7'nin sınıfı, ve bir sonraki kişi doğal olarak onu deneyecek.
    /// </para>
    /// </summary>
    [Fact]
    public void Collector_metrik_ucu_iki_parcali_ve_ikisi_de_yerinde()
    {
        var collector = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "deploy", "otel", "collector.yaml"));

        var compose = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "deploy", "docker-compose.yml"));

        Assert.Contains("prometheus:", collector, StringComparison.Ordinal);
        Assert.Contains("host: '0.0.0.0'", collector, StringComparison.Ordinal);
        Assert.Contains("port: 8888", collector, StringComparison.Ordinal);

        // Ad ekleri: uç elle yapılandırıldığında sayaç `..._total` adıyla
        // yayılıyor. Okuyucu iki adı da kabul ediyor, ama yapılandırma da adı
        // sabitliyor — iki savunma, biri kodda biri yapılandırmada.
        Assert.Contains("without_type_suffix: true", collector, StringComparison.Ordinal);
        Assert.Contains("without_units: true", collector, StringComparison.Ordinal);

        // Yayınlanan port olmadan yukarıdakinin hiçbir anlamı yok.
        Assert.Contains(":8888", compose, StringComparison.Ordinal);

        // Ve v0.123.0'dan beri yok sayılan eski biçim kullanılmıyor.
        Assert.DoesNotContain("address: 0.0.0.0:8888", collector, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- yardımcılar

    private static LedgerReading Drops(long value) =>
        LedgerReading.Measured(LedgerLayer.Wire, WireDropReader.ProcNetUdp, value);

    private static ArrivalLedger Ledger(
        long expected,
        LedgerReading wire,
        long accepted,
        long refused,
        long archived,
        long searchable) =>
        new(
            "run-1",
            expected,
            wire,
            LedgerReading.Measured(LedgerLayer.Collector, "accepted", accepted),
            LedgerReading.Measured(LedgerLayer.Collector, "refused", refused),
            LedgerReading.Measured(LedgerLayer.Product, "raw_manifest", archived),
            LedgerReading.Measured(LedgerLayer.Product, "events", searchable));
}

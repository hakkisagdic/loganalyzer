using System.Text;
using Bizigo.Capacity;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>B01'in manifesti — şekli tüketicisinden okunmuş bir sözleşme.</b>
///
/// <para>
/// <see cref="ArrivalLedger"/> (B02) bugün ağaçta ve testli; manifest yoktu.
/// §8'in yönü gereği şekil <b>var olan tüketiciden</b> türedi, tersi değil —
/// yeni bir şekil seçilseydi iki temsil doğar ve biri diğerine uymaya
/// çalışırdı.
/// </para>
///
/// <para>
/// Bu sınıfın en önemli kapısı <see cref="Manifest_defterin_bekledigi_sekle_oturuyor"/>:
/// sözleşmeyi <b>iddia etmiyor, koşturuyor</b> — manifest gerçek deftere
/// veriliyor ve defterin hükmü okunuyor. Alan adlarını karşılaştıran bir test,
/// iki tarafın aynı <i>anlamı</i> taşıdığını göstermezdi.
/// </para>
/// </summary>
public sealed class CapacityManifestTests
{
    private static readonly DateTimeOffset Basla = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Hedefine ulaşmış, dolayısıyla kaybı yorumlanabilir bir koşum.</summary>
    private static GeneratorAttainment UlasmisKosum(int eps, int saniye)
    {
        var saat = new FakeTimeProvider(Basla);
        var pacer = new TokenBucketPacer(
            new PaceProfile.Fixed(eps, TimeSpan.FromSeconds(saniye)),
            saat);

        for (var s = 0; s < saniye; s++)
        {
            saat.Advance(TimeSpan.FromSeconds(1));

            for (var i = 0; i < eps; i++)
            {
                pacer.TryAcquire();
            }
        }

        return GeneratorAttainment.From(pacer);
    }

    private static GeneratorAttainment GerideKalmisKosum()
    {
        var saat = new FakeTimeProvider(Basla);
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(1_000, TimeSpan.FromSeconds(1)), saat);

        saat.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 824; i++)
        {
            pacer.TryAcquire();
        }

        return GeneratorAttainment.From(pacer);
    }

    private static CapacityRunManifest Manifest(
        GeneratorAttainment hukum,
        int satir,
        bool? ayniMakine = false) =>
        new(
            "run-1",
            "fixed",
            hukum,
            [.. Enumerable.Range(0, satir).Select(i => CapacityRunManifest.Digest($"<13>satır-{i}"))],
            ayniMakine);

    // ---------------------------------------------------------------------
    // Sözleşme — iddia değil koşum
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Manifest gerçek deftere veriliyor ve defter onu okuyabiliyor.</b>
    ///
    /// <para>
    /// <c>Expected</c> manifestin satır sayısından geliyor; defter beklenen
    /// kadar arşiv ve <c>events</c> görürse hüküm <c>Consistent</c>. Bu, iki
    /// dalın aynı sözleşmenin iki ucunu tuttuğunun <b>koşturulmuş</b> kanıtı.
    /// </para>
    /// </summary>
    [Fact]
    public void Manifest_defterin_bekledigi_sekle_oturuyor()
    {
        var manifest = Manifest(UlasmisKosum(100, 10), satir: 1_000);

        Assert.Equal(1_000, manifest.Expected);

        var defter = new ArrivalLedger(
            manifest.RunId,
            manifest.Expected,
            LedgerReading.Measured(LedgerLayer.Wire, "/proc/net/udp", 0),
            LedgerReading.Measured(LedgerLayer.Collector, "accepted", 1_000),
            LedgerReading.Measured(LedgerLayer.Collector, "refused", 0),
            LedgerReading.Measured(LedgerLayer.Product, "raw_manifest", 1_000),
            LedgerReading.Measured(LedgerLayer.Product, "events", 1_000));

        Assert.Equal(LedgerVerdict.Consistent, defter.Verdict);
    }

    /// <summary>
    /// <c>Expected</c> özet <b>sayısından</b> türüyor, ayrı bir alan değil.
    ///
    /// <para>
    /// Ayrı bir alan olsaydı özet listesi ile sayı ayrışabilirdi ve
    /// ayrışmayı hiçbir şey yakalamazdı — bu deponun *"iki gösterim"* sınıfı.
    /// </para>
    /// </summary>
    [Fact]
    public void Beklenen_sayi_ozet_listesinden_turuyor()
    {
        var manifest = Manifest(UlasmisKosum(10, 3), satir: 30);

        Assert.Equal(manifest.Digests.Count, manifest.Expected);
        Assert.Equal(30, manifest.Expected);
    }

    // ---------------------------------------------------------------------
    // Özet — Latin1, UTF-8 DEĞİL
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Özet tel baytlarının özeti</b> — <c>Latin1</c>, <c>UTF-8</c> değil.
    ///
    /// <para>
    /// Bu depoda tel kodlaması bayt ↔ kod noktası eşlemesini birebir ve
    /// tersinir tutmak için <c>iso-8859-1</c> üzerinden taşınıyor (collector
    /// <c>encoding: iso-8859-1</c>, ürün tarafı <c>Latin1.GetBytes</c>). Özeti
    /// UTF-8 ile almak, ham arşivdeki baytlarla <b>eşleşmeyen</b> bir özet
    /// üretirdi ve <c>ProductArchived</c> sessizce sıfır çıkardı — kayıp yokken
    /// *"her satır kayıp"* denirdi.
    /// </para>
    ///
    /// <para>
    /// Fark yalnızca <c>0x7F</c> üstü baytlarda görünüyor, yani ASCII bir
    /// örnekle sınanan bir test bu kusuru <b>hiç görmez</b>. Örnek bilerek
    /// Türkçe.
    /// </para>
    /// </summary>
    [Fact]
    public void Ozet_latin1_baytlarindan_aliniyor()
    {
        const string satir = "<13>Oct  1 12:00:00 fw01 kullanıcı oturumu açıldı";

        var latin1 = CapacityRunManifest.Digest(satir);
        var utf8 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(satir)));

        Assert.NotEqual(utf8, latin1);

        Assert.Equal(
            Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(satir))),
            latin1);
    }

    // ---------------------------------------------------------------------
    // Sayı hükmünden ayrılamıyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Geride kalmış bir koşumun sayısı kayıp hesabının paydası olamıyor.</b>
    ///
    /// <para>
    /// Bugünün kuralı: *"yanlış katmanı suçlamak hiç suçlamamaktan kötüdür —
    /// arama yanlış yerde başlar."* Üreteç hedefine ulaşamadıysa eksik satırlar
    /// <b>hiç basılmamıştır</b>; o sayıyı bir kayıp hesabına payda yapmak
    /// aramayı ürünün içinde başlatırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Geride_kalmis_kosum_kayip_hesabina_payda_olamiyor()
    {
        var manifest = Manifest(GerideKalmisKosum(), satir: 824);

        Assert.Equal(GeneratorVerdict.GeneratorLimited, manifest.Attainment.Verdict);
        Assert.False(manifest.CanAnchorLossAccounting);
    }

    /// <summary>
    /// <b>Üretecin nerede koştuğu söylenmediyse sayı yine payda olamıyor</b> —
    /// hüküm <c>Attained</c> olsa bile.
    ///
    /// <para>
    /// İki şart <b>birlikte</b> gerekiyor ve bu ayrı bir kapı: aynı makinede
    /// koşan bir üreteç ölçtüğü sistemin CPU'sunu yiyor, yani sayı iki yükün
    /// toplamının tavanı olur. Kapasite belgesi §6'nın açık sorusu
    /// <b>varsayılmıyor</b>, ve cevaplanmadığı hâl <see langword="null"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void Uretecin_nerede_kostugu_soylenmediyse_payda_olamiyor()
    {
        var soylenmemis = Manifest(UlasmisKosum(100, 10), satir: 1_000, ayniMakine: null);

        Assert.Equal(GeneratorVerdict.Attained, soylenmemis.Attainment.Verdict);
        Assert.True(soylenmemis.Attainment.LossIsInterpretable);
        Assert.False(
            soylenmemis.CanAnchorLossAccounting,
            "Hüküm `Attained` olduğu için sayı payda sayılıyor, ama üretecin nerede koştuğu " +
            "SÖYLENMEDİ — neyin tavanı olduğu bilinmeyen bir sayı.");

        Assert.Contains("SÖYLENMEDİ", soylenmemis.Describe(), StringComparison.Ordinal);

        var soylenmis = Manifest(UlasmisKosum(100, 10), satir: 1_000, ayniMakine: true);

        Assert.True(soylenmis.CanAnchorLossAccounting);
        Assert.Contains("AYNI makinede", soylenmis.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sayı hükümsüz basılamıyor.</b> <c>Describe()</c> her hâlde hükmü
    /// taşıyor — bir <c>Expected</c> değerini hükümsüz gören okuyucu onu bir
    /// olgu sanar.
    /// </summary>
    [Fact]
    public void Sayi_hukumsuz_basilmiyor()
    {
        foreach (var manifest in new[]
        {
            Manifest(UlasmisKosum(100, 10), satir: 1_000),
            Manifest(GerideKalmisKosum(), satir: 824),
        })
        {
            var satir = manifest.Describe();

            Assert.Contains(manifest.Expected.ToString(System.Globalization.CultureInfo.InvariantCulture), satir, StringComparison.Ordinal);
            Assert.Contains(manifest.Attainment.Describe(), satir, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>Manifest ürün sayaçlarını taşımıyor</b> ve taşımaması sözleşmenin
    /// kendisi.
    ///
    /// <para>
    /// Manifest yalnızca üretecin yaptığını biliyor; ham arşiv ve
    /// <c>events</c> sayıları deftere ürün tarafından veriliyor. İkisini tek
    /// yere yazmak <c>GENERATOR-LIMITED</c> ile <c>LEDGER-LIMITED</c> ayrımını
    /// yok ederdi — B02'nin <c>ProcessedRecords</c>/<c>AcceptedRecords</c>'ı
    /// bilerek dışarıda bırakmasının aynı gerekçesi.
    /// </para>
    /// </summary>
    [Fact]
    public void Manifest_urun_sayaclarini_tasimiyor()
    {
        var adlar = typeof(CapacityRunManifest)
            .GetProperties()
            .Select(static p => p.Name)
            .ToArray();

        foreach (var yasak in new[] { "Archived", "Searchable", "Accepted", "Refused", "Drops" })
        {
            Assert.DoesNotContain(
                yasak,
                string.Join('|', adlar),
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Expected", adlar);
        Assert.Contains("Attainment", adlar);
    }
}

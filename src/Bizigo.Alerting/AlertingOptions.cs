namespace Bizigo.Alerting;

/// <summary>
/// Alarm motorunun sınırları (T21, K16).
///
/// <para>
/// <b>Bu sınıfın varlık sebebi K16'nın uyarısı:</b> 50 kişilik bir kurumda
/// herkes kural yazabiliyorsa, tek kötü kural ClickHouse'u doyurur ve bunun
/// bedelini alarm yazan kişi değil, o sırada arama yapan herkes öder. Sınırlar
/// bu yüzden sonradan eklenen bir emniyet supabı değil, motorun ilk gününden
/// itibaren yapılandırmasının parçası.
/// </para>
/// </summary>
public sealed class AlertingOptions
{
    public const string SectionName = "Alerting";

    /// <summary>Motor açık mı. Kapalıyken zamanlayıcı hiç dönmüyor.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Aynı anda değerlendirilebilecek kural sayısı.
    ///
    /// <para>
    /// Dört, ClickHouse'un kapasitesinden değil <b>API'nin kendi hız sınırından</b>
    /// türetildi: kullanıcı başına eşzamanlılık sınırı da dört. Alarm motoru
    /// arka planda tek bir "kullanıcı" gibi davranmalı, çünkü kimse onun
    /// yüzünden kendi sorgusunun yavaşladığını göremez.
    /// </para>
    /// </summary>
    public int MaxConcurrentEvaluations { get; set; } = 4;

    /// <summary>
    /// Tek bir kural değerlendirmesinin üst sınırı.
    ///
    /// <para>
    /// ⚠️ Bu bir <b>kaynak koruması</b>, ölçüm değil. Zaman aşımına uğrayan kural
    /// "sessiz" sayılmıyor — <c>TimedOut</c> durumuna düşüyor ve sayaç artıyor.
    /// F1'in en pahalı dersi tam olarak bu ayrımın kaybolmasıydı: duvar saati
    /// bütçesi ölçmek istediğin şeyi ölçmez, dolayısıyla bütçeyi aşmak
    /// "eşik aşılmadı" ile aynı sonuca <b>asla</b> bağlanmamalı.
    /// </para>
    /// </summary>
    public int EvaluationTimeoutSeconds { get; set; } = 20;

    /// <summary>Bir turda en fazla kaç kural ele alınacak. Kuyruk uzarsa sonraki tura kalıyor.</summary>
    public int MaxRulesPerTurn { get; set; } = 50;

    /// <summary>
    /// Bir kuralın bakabileceği en geniş pencere.
    ///
    /// <para>
    /// K16'nın "tek kötü kural" senaryosunun en ucuz kapısı bu ve <b>kural
    /// yazılırken</b> uygulanıyor: 30 günlük pencereye her dakika bakan bir kural
    /// tek başına ClickHouse'u meşgul eder, üstelik yazan kişi bunun farkında
    /// bile olmaz. Reddi çalışma anında değil kaydetme anında vermek, hatayı
    /// üretime hiç sokmuyor.
    /// </para>
    /// </summary>
    public int MaxWindowSeconds { get; set; } = 86_400;

    /// <summary>
    /// Bir kuralın koşabileceği en sık aralık. Saniyede bir koşan bir kural,
    /// hız sınırının arka kapıdan delinmesi demek.
    /// </summary>
    public int MinIntervalSeconds { get; set; } = 30;

    /// <summary>Zamanlayıcı turları arası bekleme.</summary>
    public TimeSpan TurnInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Sessizlik değerlendirmesinde olay etkinliğine ne kadar geriye bakılacağı.
    ///
    /// <para>
    /// Kuralın kendi eşiğinden bağımsız ve ondan büyük olmalı: 15 dakikalık bir
    /// susma eşiği için 15 dakikalık pencereye bakmak, tam sınırdaki bir kaynağı
    /// "hiç görülmemiş" gösterirdi. Değerlendirici yine de kural eşiğinin iki
    /// katıyla bu değerin büyüğünü alıyor.
    /// </para>
    /// </summary>
    public TimeSpan SilenceLookback { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Bildirim mesajındaki bağlantının kökü. Boşsa bağlantı üretilmiyor —
    /// yanlış bir kök, kullanıcıyı hiçbir yere götürmeyen bir alarm demek.
    /// </summary>
    public string ProductBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Arama ekranının yolu. Ekranın kendisi T15'in; burada yapılandırılabilir
    /// olması, rota adı değiştiğinde tek bir ayarın değişmesi için — bağlantı
    /// üretimini o ticket'a bağımlı kılmadan doğru zaman aralığını taşıyabiliyoruz.
    /// </summary>
    public string SearchPath { get; set; } = "/olaylar";

    // Gizli bilgi anahtarı BURADA DEĞİL. T22'de `Alerting:SecretKey` olarak
    // duruyordu; T25 connector kimlik bilgileri için aynı şeye ihtiyaç duyunca
    // `Security:SecretKey`e taşındı (bkz. SecretProtectionOptions). İki ayrı
    // anahtar, iki ayrı rotasyon hikâyesi ve altı ay sonra birinin döndürülüp
    // diğerinin unutulması demekti.

    /// <summary>Bir teslimin toplam deneme hakkı.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>Kanal isteğinin zaman aşımı.</summary>
    public TimeSpan ChannelTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Bir gönderim turunda en fazla kaç teslim ele alınacak.</summary>
    public int MaxDeliveriesPerTurn { get; set; } = 100;
}

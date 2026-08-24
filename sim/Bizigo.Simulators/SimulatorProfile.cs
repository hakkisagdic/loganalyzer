namespace Bizigo.Simulators;

/// <summary>
/// Simüle edilen tek bir cihaz.
///
/// <para>
/// Üç sadakat seviyesi de (N1 süreç içi sahte, N2 gerçek SSH sunucusu, N3 CLI
/// öykünmesi) <b>bu tipi</b> okuyor. Her seviyenin kendi tanımını taşıması,
/// ayrıştıkları gün hangisinin doğru olduğunu bilinemez kılardı — ve ayrışma
/// sessiz olurdu: SSH tarafı <c>fw-ankara-01</c> derken syslog tarafı
/// <c>fw-ankara-1</c> basar, envanterde iki yarım cihaz görünür.
/// </para>
/// </summary>
public sealed class SimulatorProfile
{
    /// <summary>Dosya adıyla aynı olmak zorunda; bekçi bunu sınıyor.</summary>
    public string Id { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public string Product { get; set; } = string.Empty;

    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Kapsam. Filonun birden çok gruba yayılması bilinçli: tek gruba toplanmış
    /// bir filo, K17'nin ekranda görünmesini imkânsız kılar.
    /// </summary>
    public string OwnerGroup { get; set; } = string.Empty;

    /// <summary>
    /// Envanter bağı (dispatcher kademe 1). Boş olabilir — <c>lb-web-01</c>
    /// gibi bağsız bir kaynak da geçerli bir durum.
    /// </summary>
    public string? ParserId { get; set; }

    public string Encoding { get; set; } = "auto";

    /// <summary>
    /// SSH yüzeyi. <b>Null olabilir</b> ve bu şemanın bir özelliği: nginx bir
    /// ağ cihazı değil, config'ini bu ürün çekmiyor. Her profilin her yüzeyi
    /// taklit etmek zorunda olmadığını gösteren yer burası.
    /// </summary>
    public SimulatorSsh? Ssh { get; set; }

    public SimulatorConfigSet? Config { get; set; }

    public SimulatorSyslog? Syslog { get; set; }
}

/// <summary>N2/N3'ün açacağı sunucunun kimliği.</summary>
public sealed class SimulatorSsh
{
    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Parolanın kendisi DEĞİL, hangi ortam değişkeninden okunacağı.
    ///
    /// <para>
    /// Profil depoya giriyor; gizli bilgi girmiyor. Alanın adı da bunu
    /// söylüyor — <c>credential</c> deseydik bir gün birinin oraya gerçek bir
    /// parola yazması an meselesiydi.
    /// </para>
    /// </summary>
    public string CredentialEnv { get; set; } = string.Empty;

    public string Auth { get; set; } = "password";
}

/// <summary>Taban config ve adlandırılmış senaryo geçişleri.</summary>
public sealed class SimulatorConfigSet
{
    public string Baseline { get; set; } = string.Empty;

    /// <summary>
    /// Senaryo adı → config dosyası. Varsayılan koşum <b>daima</b> baseline'ı
    /// döndürüyor; senaryo açıkça seçiliyor. Tekrarlanabilirlik testin şartı.
    /// </summary>
    public Dictionary<string, string> Scenarios { get; set; } = [];
}

/// <summary>Syslog basımı — S02'nin girdisi.</summary>
public sealed class SimulatorSyslog
{
    /// <summary>
    /// Örnek dosya yolları, depo köküne göre. Satırlar <b>kopyalanmıyor</b>,
    /// işaret ediliyor: bir örnek düzeltildiğinde simülatör de düzelmiş oluyor.
    /// </summary>
    public List<string> Samples { get; set; } = [];

    public int RatePerMinute { get; set; } = 60;

    /// <summary><c>tcp</c> ya da <c>udp</c>.</summary>
    public string Transport { get; set; } = "tcp";
}

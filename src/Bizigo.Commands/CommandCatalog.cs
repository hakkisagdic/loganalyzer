namespace Bizigo.Commands;

/// <summary>
/// Bir komutun <b>iki sunumda da</b> aynı olan kimliği.
///
/// <para>
/// <b>Kaydın kendisi paritenin tanımı.</b> CLI ağacı bu listeden kuruluyor,
/// MCP araçları bu listeye karşı sınanıyor. İkisi ayrı ayrı yazılsaydı
/// "parite" bir iddia olurdu; buradan türediklerinde bir <b>olgu</b>.
/// </para>
/// </summary>
/// <param name="Name">
/// Çekirdek adı — noktalı ve <b>yüzeysiz</b>: <c>parser.lint</c>. MCP araç adı
/// bununla aynı; CLI yolu <paramref name="CliPath"/>'te.
/// </param>
/// <param name="CliPath">CLI'daki yolu — <c>parser lint</c>.</param>
/// <param name="Title">İnsana gösterilen başlık.</param>
/// <param name="Summary">
/// Tek cümlelik özet. MCP tarafında <b>bağlam bütçesinin kalemi</b>: bu metin
/// her <c>tools/list</c> yanıtında taşınıyor.
/// </param>
/// <param name="Exposure">Araç mı, muaf mı — ve muafsa neden.</param>
public sealed record CommandDescriptor(
    string Name,
    string CliPath,
    string Title,
    string Summary,
    CommandExposure Exposure);

/// <summary>
/// Bir komutun MCP yüzeyindeki hâli.
///
/// <para>
/// <b>İki değer, üç değil.</b> M02'nin bitti tanımı <i>"her komut ya MCP aracı
/// ya gerekçeli muafiyet"</i> diyor ve üçüncü bir <c>Pending</c> hâli o cümleyi
/// boşa çıkarırdı: <i>"bir gün kapanacak"</i> ile <i>"hiç kapanmayacak"</i> aynı
/// listede duramaz, yoksa <i>"liste boşaldı mı"</i> sorusunun cevabı asla evet
/// olamaz (§8).
/// </para>
/// </summary>
public abstract record CommandExposure
{
    private CommandExposure()
    {
    }

    /// <summary>Bu komut bir MCP aracı olarak ilan ediliyor.</summary>
    public sealed record Tool : CommandExposure
    {
        public static Tool Instance { get; } = new();
    }

    /// <summary>
    /// Bu komut <b>bilerek</b> araç değil.
    /// </summary>
    /// <param name="Reason">
    /// Gerekçe — <b>boş olamaz</b>. Muafiyet <i>"araç yapmaya üşendik"</i>
    /// değil <b>"araç olması yanlış olur"</b> demek, ve gerekçesiz bir muafiyet
    /// ikisini ayırt edilemez yapardı.
    /// </param>
    public sealed record Exempt(string Reason) : CommandExposure;
}

/// <summary>
/// Komut çekirdeğinin <b>tek</b> kataloğu — iki sunumun da okuduğu yer.
///
/// <para>
/// <b>Ölçüt:</b> <c>bizigo</c> CLI'sinin çalıştırılabilir her komutu burada bir
/// satır. Grup düğümleri (<c>parser</c>, <c>fields</c>, …) kendi başlarına iş
/// yapmıyor ve sayıma girmiyor.
/// </para>
///
/// <para>
/// <b>Muaf sayısı sabitle çivili</b> (<c>CommandCatalogTests</c>): muafiyet
/// eklemek <b>iki ayrı bilinçli hareket</b> — buraya gerekçeyi yazmak ve testin
/// sabitini değiştirmek. Tek başına birincisi bir kaçış kapısı, tek başına
/// ikincisi gerekçesiz bir sayı. Kalıp T43'ün <c>constraints_waived</c>'ı ve
/// T44'ün 2→1 düşüşüyle aynı.
/// </para>
/// </summary>
public static class CommandCatalog
{
    /// <summary>
    /// <b>Sıra alfabetik ve anlamı yok</b> — okunabilirlik için. Sıraya anlam
    /// yüklenirse bir gün biri onu değiştirir ve neyin bozulduğu görünmez.
    /// </summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } =
    [
        new("fields.coverage", "fields coverage",
            "Alan kapsamı",
            "Altın örneklerin taşıdığı bilginin ne kadarının olay tablosuna alan olarak indiğini ölçer.",
            CommandExposure.Tool.Instance),

        new("fields.values", "fields values",
            "Kolon değer uzayı",
            "Bir kolonun taşıdığı ayrık değerleri ve dağılımını sayar.",
            CommandExposure.Tool.Instance),

        new("fleet.apply", "fleet apply",
            "Filo tanımını uygula",
            "Simüle cihaz filosunu kontrol düzlemine yazar.",
            new CommandExposure.Exempt(
                "Simülasyon altyapısını DEĞİŞTİRİYOR ve ürün yüzeyine ait değil: simülatör " +
                "kontrolü ürün API'sinden sürülebilseydi, ürünün kendi süreci filoyu " +
                "değiştirebilirdi. Simülatörün kendi yüzeyi var ve o M03'ün işi.")),

        new("mcp.serve", "mcp serve",
            "MCP sunucusu",
            "MCP sunucusunu stdio üzerinden koşturur.",
            new CommandExposure.Exempt(
                "Sunucunun KENDİSİ. Bir aracın sunucuyu başlatması özyineleme; ayrıca bu " +
                "komut stdout'u protokol olarak sahipleniyor ve bir araç çağrısı içinden " +
                "çalıştırılması akışı kendi üstüne yazardı.")),

        new("parser.coverage", "parser coverage",
            "Katalog kapsamı",
            "Katalogdaki altın örneklerin kaçının çözüldüğünü ölçer.",
            CommandExposure.Tool.Instance),

        new("parser.lint", "parser lint",
            "Parser doğrulaması",
            "Parser YAML'ının şemasını doğrular ve ReDoS taraması yapar.",
            CommandExposure.Tool.Instance),

        new("parser.test", "parser test",
            "Parser testleri",
            "Parser YAML'ının gömülü `tests` bloğunu koşturur.",
            CommandExposure.Tool.Instance),

        new("parser.try", "parser try",
            "Tek satır dene",
            "Tek bir log satırını parser'dan geçirir ve çözülen alanları gösterir.",
            CommandExposure.Tool.Instance),

        new("schema.migrate", "schema migrate",
            "Şema göçü",
            "ClickHouse göçlerini uygular.",
            new CommandExposure.Exempt(
                "Göç GERİ ALINAMAZ ve sırası anlamlı. Bu deponun en pahalı ÖNLENMİŞ hatası " +
                "bir göçtü: `enabled` → `status` geçişi her pasif alarm kuralını sessizce " +
                "açıyordu. Bir modelin kendi kararıyla göç uygulaması, o hatayı insan onayı " +
                "olmadan mümkün kılar.")),

        new("seed.golden", "seed golden",
            "Altın örnek tohumlaması",
            "Ölçüm ve geliştirme verisi yükler.",
            new CommandExposure.Exempt(
                "Veri YAZIYOR. Üretim verisinin yanına ölçüm verisi karışması sessiz bir " +
                "yanlış: sonraki her ölçüm kirlenir ve KİRLENDİĞİ GÖRÜNMEZ — sayı yine " +
                "makul bir sayı olur.")),

        new("sigma.plan", "sigma plan",
            "Sigma senkron planı",
            "Manifestin alarm kurallarına ne getireceğini hiçbir şey yazmadan gösterir.",
            CommandExposure.Tool.Instance),

        new("sigma.sync", "sigma sync",
            "Sigma senkronu",
            "Derleme hattının manifestini alarm kurallarına yazar.",
            new CommandExposure.Exempt(
                "Kontrol düzlemine YAZIYOR — alarm kuralları ürünün davranışı. Okuma yarısı " +
                "ayrı bir komut olarak ilan edildi (`sigma.plan`), yani muafiyet " +
                "kabiliyeti değil yalnızca YAZMAYI kapsıyor.")),
    ];

    /// <summary>Araç olarak ilan edilen komutlar.</summary>
    public static IReadOnlyList<CommandDescriptor> Tools =>
        [.. All.Where(c => c.Exposure is CommandExposure.Tool)];

    /// <summary>Gerekçeli muafiyetler.</summary>
    public static IReadOnlyList<CommandDescriptor> Exempt =>
        [.. All.Where(c => c.Exposure is CommandExposure.Exempt)];
}

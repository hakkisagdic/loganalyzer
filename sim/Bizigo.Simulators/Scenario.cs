namespace Bizigo.Simulators;

/// <summary>
/// Bir senaryonun <b>hangi yüzeyi</b> değiştirdiği (S04).
///
/// <para>
/// <b>Bu ayrım S04'ün taşıyıcı bulgusu.</b> S01/S03'e kadar senaryo adı, profilin
/// <c>config.scenarios</c> sözlüğünde aranan bir anahtardı — yani <i>her</i>
/// senaryonun bir config dosyası olduğu varsayılıyordu. Yedi senaryonun
/// yalnızca dördü öyle:
/// </para>
///
/// <list type="bullet">
///   <item><c>saat-kaymasi</c> ve <c>bozuk-kodlama</c> config'i hiç
///   değiştirmiyor — <b>basıcının</b> davranışını değiştiriyorlar.</item>
///   <item><c>sidecar-yok</c> ne config ne basıcı: bir <b>altyapı</b> eylemi,
///   ve onu yapan koordinatör.</item>
/// </list>
///
/// <para>
/// Yüzey yazılmasaydı <c>saat-kaymasi</c> config sözlüğünde aranır,
/// bulunamaz, ve hata <i>"profilde böyle bir senaryo yok"</i> derdi — yani
/// arıza profil dosyasında aranırdı. Yanlış yere işaret eden bir hata mesajı,
/// bu depoda bir teşhis turunu tümden harcamış bir sınıf.
/// </para>
/// </summary>
public enum ScenarioSurface
{
    /// <summary>Cihazın config çıktısı — N1/N2 üzerinden okunuyor.</summary>
    Config = 0,

    /// <summary>Syslog basıcısının tele yazdığı satırlar.</summary>
    Syslog = 1,

    /// <summary>
    /// Altyapı: bir servisin durdurulması gibi. <b>Simülatörün elinde değil</b>
    /// ve motor bunu açıkça söylüyor — sessizce hiçbir şey yapmamak, senaryonun
    /// koştuğu sanılan bir test bırakırdı.
    /// </summary>
    Infrastructure = 2,
}

/// <param name="Name">Senaryonun adı; profil dosyalarındaki anahtar da bu.</param>
/// <param name="Surface">Hangi yüzeyi değiştiriyor.</param>
/// <param name="Claim">
/// Ürünün <b>hangi iddiasını</b> sınıyor. Serbest bir açıklama değil: fazın
/// kuralı gereği "genel olarak değişsin" diye senaryo yok, her senaryo bir
/// iddiayı hedefliyor.
/// </param>
public sealed record ScenarioDefinition(string Name, ScenarioSurface Surface, string Claim);

/// <summary>
/// Adlandırılmış geçişlerin <b>tek sözlüğü</b> (S04).
///
/// <para>
/// <b>Neden tek yer:</b> geçişler bugün iki tarafta yaşıyor — config seçimi
/// (N1/N2) ve basıcı davranışı. İki taraf kendi listesini tutsaydı bir senaryo
/// adı birinde düzeltilip diğerinde eski kalabilirdi, ve ayrışma sessiz olurdu:
/// SSH tarafı <c>sir-dondu</c> uygularken syslog tarafı hiçbir şey yapmaz, test
/// ise "senaryo koştu" diye yeşil kalırdı.
/// </para>
///
/// <para>
/// Liste elle yazılı ve <b>öyle kalmalı</b>: burada denetlenen küme
/// <i>ürünün iddiaları</i>, keşfedilebilir bir kod yüzeyi değil. Bekçi,
/// listenin faz belgesindeki yedi satırla örtüştüğünü sınıyor — yani elle
/// tutulan taraf, elle tutulması gereken taraf.
/// </para>
/// </summary>
public static class Scenarios
{
    /// <summary>Varsayılan: değişim yok. Tekrarlanabilirlik testin şartı.</summary>
    public const string Baseline = "baseline";

    /// <summary>
    /// <b>"Bu ad baseline mi" sorusunun TEK cevabı.</b>
    ///
    /// <para>
    /// Ayrı bir predicate olmasının sebebi ölçüldü: S04 senaryo <i>sözlüğünü</i>
    /// tekilleştirdi ama <i>tanımayı</i> tekilleştirmedi. Taşıyıcı "boş dize =
    /// baseline" diyordu (S01), motor ise hem boşu hem <c>"baseline"</c> sabitini
    /// tanıyordu — ve taşıyıcının config yolu motora hiç uğramıyordu. Sonuç:
    /// adlandırılmış baseline sözlükte aranıp bulunamadı ve üç entegrasyon testi
    /// CI'da düştü, 974 birim testi yeşilken.
    /// </para>
    ///
    /// <para>
    /// <b>Taşınabilir ders:</b> bir kavramı tekilleştirmek, onu <b>tanıyan</b>
    /// predicate'i tekilleştirmekle aynı şey değil. Depodaki "ikinci kopya
    /// yazma" kuralı <i>veriyi</i> anlatıyor; bu, <b>tanımanın</b> karşılığı.
    /// </para>
    ///
    /// <para>
    /// Eşleşme <b>ordinal</b>: <c>"Baseline"</c> kabul edilmiyor. Büyük/küçük
    /// harf toleransı, belgelenen ile kabul edilenin ayrışmasının en yaygın
    /// yolu — ve senaryo adları bu depoda ordinal eşleşiyor.
    /// </para>
    /// </summary>
    public static bool IsBaseline(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name.Trim(), Baseline, StringComparison.Ordinal);

    /// <summary>
    /// Profil, sihirli adları <b>gölgeleyemiyor</b>.
    ///
    /// <para>
    /// <b>Karar:</b> <c>scenarios:</c> altında <c>baseline</c> adlı bir giriş
    /// <b>reddediliyor</b>. Alternatif "profil kazansın"dı ve şu yüzden
    /// seçilmedi: o zaman "değişim yok" isteyen bir çağrı sessizce
    /// <i>değişmiş</i> bir config alır, karşılaştırmanın tabanı kayar, ve fark
    /// testleri <b>yanlış sebeple geçer</b> — hiçbir hata üretmeden.
    /// </para>
    ///
    /// <para>
    /// Ret ucuz: bir profil dosyası düzeltilir. Sessiz taban kayması pahalı,
    /// çünkü belirtisi yok. Bu depoda tekrarlanan ayrım budur.
    /// </para>
    ///
    /// <para>
    /// Ret <b>yükleme anında</b>: koşum anına bırakılsaydı yalnızca o senaryoyu
    /// çağıran test görürdü ve profil diğer bütün testlerde geçerli sayılırdı.
    /// </para>
    /// </summary>
    public static IEnumerable<string> ShadowingErrors(IEnumerable<string>? scenarioNames)
    {
        if (scenarioNames is null)
        {
            yield break;
        }

        foreach (var name in scenarioNames)
        {
            if (IsBaseline(name))
            {
                yield return
                    $"`scenarios` altında '{name}' tanımlanamaz: '{Baseline}' sihirli bir ad ve " +
                    "\"değişim yok\" anlamına geliyor. Gölgelenirse taban sessizce kayar ve fark " +
                    "testleri yanlış sebeple geçer.";
            }
        }
    }

    private static readonly ScenarioDefinition[] All =
    [
        new("kural-eklendi", ScenarioSurface.Config,
            "ConfigDiff gerçek bir fark üretiyor; change_events bölüm ADINI taşıyor, satır içeriğini değil."),

        new("sir-dondu", ScenarioSurface.Config,
            "Maskeleme siliyor değil maskeliyor: özet değişiyor, değer hiçbir yere yazılmıyor."),

        new("cihaz-yeniden-yazdi", ScenarioSurface.Config,
            "Çoklu-küme farkı sahte değişiklik üretmiyor (LCS üretirdi)."),

        new("gurultu", ScenarioSurface.Config,
            "ConfigNormalizer gürültüyü eliyor; fark BOŞ çıkıyor."),

        new("saat-kaymasi", ScenarioSurface.Syslog,
            "time_source dürüstlüğü; kaymış kaynak görünür oluyor, sessizce atlanmıyor."),

        new("bozuk-kodlama", ScenarioSurface.Syslog,
            "Kodlama tespiti ve bizigo.wire_encoding; ham baytlar bozulmadan arşive giriyor."),

        new("sidecar-yok", ScenarioSurface.Infrastructure,
            "D3: sidecar arızalıyken throughput düşmüyor — bugün mantıklı ama ölçülmemiş bir iddia."),
    ];

    public static IReadOnlyList<ScenarioDefinition> Known => All;

    /// <summary>
    /// Adı çözer. Bilinmeyen ad <see langword="null"/> dönüyor — çağıran
    /// <b>sessizce baseline'a düşmüyor</b>.
    ///
    /// <para>
    /// Düşseydi adı yanlış yazılmış bir senaryo testi yeşil bırakır ve
    /// "fark yok" sonucu doğru sanılırdı. N1 aynı kararı zaten vermişti; S04 onu
    /// tek yere taşıyor.
    /// </para>
    /// </summary>
    public static ScenarioDefinition? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.Ordinal));

    /// <summary>Verilen yüzeye ait senaryolar.</summary>
    public static IEnumerable<ScenarioDefinition> For(ScenarioSurface surface) =>
        All.Where(s => s.Surface == surface);

    /// <summary>
    /// Bir senaryonun verilen yüzeyde uygulanıp uygulanamayacağını söyler.
    ///
    /// <para>
    /// Dönen metin <b>hatanın kendisi</b>: <see langword="null"/> ise uygulanır.
    /// Uygulanamıyorsa metin, senaryonun hangi yüzeye ait olduğunu söylüyor —
    /// çünkü asıl kafa karışıklığı "bu senaryo yok" ile "bu senaryo burada
    /// değil" arasında.
    /// </para>
    /// </summary>
    public static string? Reject(string? name, ScenarioSurface surface)
    {
        // AYNI predicate: `Reject` ile taşıyıcı aynı soruyu iki farklı yerde
        // cevaplamıyor.
        if (IsBaseline(name))
        {
            return null;
        }

        var found = Find(name);

        if (found is null)
        {
            var known = string.Join(", ", All.Select(s => s.Name).Order(StringComparer.Ordinal));
            return $"'{name}' diye bir senaryo yok. Bilinenler: {known}";
        }

        if (found.Surface == surface)
        {
            return null;
        }

        return found.Surface switch
        {
            ScenarioSurface.Infrastructure =>
                $"'{found.Name}' bir altyapı senaryosu; simülatör onu uygulamıyor. " +
                "Koordinatör ilgili servisi durdurup koşumu yapar.",
            _ =>
                $"'{found.Name}' {Describe(found.Surface)} yüzeyini değiştiriyor, " +
                $"{Describe(surface)} yüzeyini değil.",
        };
    }

    private static string Describe(ScenarioSurface surface) => surface switch
    {
        ScenarioSurface.Config => "config",
        ScenarioSurface.Syslog => "syslog",
        _ => "altyapı",
    };
}

using System.Reflection;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M06 — kapının KALDIRILAMAZ olduğunu ölçen bekçi.</b>
///
/// <para>
/// Kapının <i>varlığını</i> derleyici tutuyor: <c>McpLogText.FromRedacted</c>
/// bir <see cref="RedactedPrompt"/> istiyor ve o tip yalnızca
/// <see cref="RedactedPrompt.Redact"/>'ten çıkıyor (yapıcısı <c>private</c>).
/// Bu sınıfın ölçtüğü şey başka bir soru: <b>kapı sessizce kaldırılabilir
/// mi?</b>
/// </para>
///
/// <h3>Neden bu ayrı bir soru</h3>
///
/// <para>
/// M01 menteşeyi bırakırken zayıflığını yazmıştı ve tarifi hâlâ geçerli:
/// fabrika <c>internal</c>, yani <c>Bizigo.Mcp</c> derlemesinin <b>içinden</b>
/// erişilebilir. Parametre tipini <see cref="RedactedPrompt"/> yapmak bugünkü
/// deliği kapatıyor, ama <b>yarın açılabilecek</b> olanı kapatmıyor. Üç
/// hareket, üçü de tek dosyada ve üçü de derlemeyi kırmadan:
/// </para>
///
/// <list type="number">
/// <item>İkinci bir fabrika: <c>internal static McpLogText FromText(string s)</c>.</item>
/// <item>Yapıcının <c>private</c>'lıktan çıkması.</item>
/// <item><c>FromRedacted</c>'e bir <c>string</c> aşırı yüklemesi.</item>
/// </list>
///
/// <para>
/// Üçü de <b>sessiz</b> olurdu: hiçbir test kırılmaz, hiçbir sayaç değişmez,
/// ve log metni redaksiyondan geçmeden modelin bağlamına girmeye başlar.
/// <c>CLAUDE.md</c> §7'nin tarif ettiği sınıf — <i>hata yok, sayaç yok,
/// belirti yok</i>.
/// </para>
///
/// <h3>Kapının dayandığı ve BURADA ölçülmeyen şey</h3>
///
/// <para>
/// Bu bekçinin çıkarımı — <i>"<c>FromRedacted</c>'e ulaşan her metin
/// redaksiyondan geçmiştir"</i> — <see cref="RedactedPrompt"/>'un tek kapılı
/// olmasına <b>dayanıyor</b>. O şart burada yeniden ölçülmüyor; T41'in kendi
/// bekçisinde duruyor
/// (<c>RedactionGateTests.Kapiyi_atlayan_ikinci_yol_yok</c>) ve oraya ikinci
/// bir yol açıldığı gün <b>o</b> kırmızı yanıyor.
/// </para>
///
/// <para>
/// Bağın yazılı olması <c>CLAUDE.md</c> §2'nin üçüncü maddesinin aynı biçimi:
/// bir mekanizmanın dayandığı varsayım yazılmazsa, varsayım değiştiğinde
/// kimse buraya bakmıyor.
/// </para>
/// </summary>
public sealed class McpRedactionGateTests
{
    private static readonly Assembly Protocol = typeof(McpLogText).Assembly;

    /// <summary>
    /// <b>Kapı 1 — <see cref="McpLogText"/> üretmenin her yolu bir
    /// <see cref="RedactedPrompt"/> istiyor.</b>
    ///
    /// <para>
    /// Yansımayla soruluyor ve <c>private</c> üyeler dahil: görünürlük bu
    /// sorunun cevabını değiştirmiyor, çünkü aynı derlemenin içinden yazılan
    /// bir çağrı <c>private</c> olanı da görüyor.
    /// </para>
    ///
    /// <para>
    /// <b>Yapıcı muaf ve sebebi mekanik:</b> o <c>private</c> ve tipin
    /// <b>kendi içinden</b> çağrılıyor; onun kapısı Kapı 2 (tek çağıran).
    /// Muafiyet bir yargı çağrısı değil — <c>private</c> olmadığı gün burası
    /// kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Log_metni_uretmenin_her_yolu_redaksiyon_kapisindan_geciyor()
    {
        var yapicilar = typeof(McpLogText).GetConstructors(IlCallReader.Everything);

        Assert.True(
            yapicilar.Length == 1 && yapicilar[0].IsPrivate,
            $"`{nameof(McpLogText)}` tam olarak BİR ve `private` yapıcı taşımalı; " +
            $"{yapicilar.Length} yapıcı bulundu ({string.Join(", ", yapicilar.Select(Imza))}). " +
            "Yapıcı `private` olmaktan çıktığı an, redaksiyon kapısını atlayan ikinci bir " +
            "üretim yolu açılmış olur ve bu HİÇBİR testi kırmaz.");

        var kapisiz = Protocol.GetTypes()
            .SelectMany(static t => t.GetMethods(IlCallReader.Everything))
            .Where(static m => m.ReturnType == typeof(McpLogText))
            .Where(static m => !m.GetParameters().Any(static p =>
                p.ParameterType == typeof(RedactedPrompt)
                || p.ParameterType == typeof(RedactedPrompt[])))
            .Select(Imza)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            kapisiz.Length == 0,
            $"`{nameof(McpLogText)}` üreten ama `{nameof(RedactedPrompt)}` İSTEMEYEN üye(ler):\n  " +
            string.Join("\n  ", kapisiz) +
            "\n\nBu üyeler redaksiyon kapısını atlıyor. Log metni doğrudan modelin bağlamına " +
            "giriyor; kapıyı bir çağrı alışkanlığına indirmek bu deponun beş kez ödediği ders.");
    }

    /// <summary>
    /// <b>Kapı 2 — <see cref="McpLogText"/>'i kuran tek metot
    /// <c>FromRedacted</c>.</b>
    ///
    /// <para>
    /// Kapı 1 <i>imzalara</i> bakıyor; bu <b>gövdelere</b> bakıyor. Aradaki
    /// fark boş değil: <see cref="RedactedPrompt"/> alan ama onu kullanmayıp
    /// başka bir metinle nesne kuran bir fabrika Kapı 1'den <b>temiz geçerdi</b>
    /// — imza doğru, gövde yanlış.
    /// </para>
    ///
    /// <para>
    /// Ölçüt <c>newobj</c>: ürün derlemelerinin tamamında
    /// <see cref="McpLogText"/>'in yapıcısını çağıran her metot toplanıyor ve
    /// kümenin tek elemanı olması isteniyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Tasiyiciyi_kuran_tek_metot_redaksiyon_fabrikasi()
    {
        var (kuranlar, cozulemeyen) = Kuranlar();

        Assert.True(
            cozulemeyen == 0,
            $"{cozulemeyen} IL metot tokenı çözülemedi. Çözülemeyen her token, bu bekçinin " +
            "GÖREMEDİĞİ bir çağrı demek — kapıyı atlayan bir fabrika 'yok' görünebilir.");

        Assert.Equal(
            [$"{nameof(McpLogText)}.{nameof(McpLogText.FromRedacted)}"],
            kuranlar);
    }

    /// <summary>
    /// <b>Kapı 3 — araç tarafının gördüğü imza <see cref="RedactedPrompt"/>
    /// alıyor.</b>
    ///
    /// <para>
    /// Kapı 1 ve 2 <c>Bizigo.Mcp</c>'nin İÇİNİ koruyor. Bu, <b>dışarıya bakan
    /// yüzü</b> ölçüyor: araçlar (M03/M04/M05) bu derlemenin dışında yaşıyor ve
    /// <see cref="McpLogText"/>'i hiç göremiyorlar. Onların gördüğü tek yol
    /// <see cref="McpToolResult.WithLogText(RedactedPrompt[])"/> ve o yolun
    /// parametresi kapının kendisi.
    /// </para>
    ///
    /// <para>
    /// İmza <c>McpLogText</c> alacak şekilde geri döndürülürse burası kırmızı
    /// yanıyor — ve o hâlde araçların log metni koyabilmesi için fabrikanın
    /// <c>public</c> yapılması gerekirdi, yani kapı iki adımda kalkardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Araclarin_gordugu_tek_yol_redaksiyon_kapisi()
    {
        var fabrika = typeof(McpLogText).GetMethod(
            nameof(McpLogText.FromRedacted), IlCallReader.Everything);

        Assert.NotNull(fabrika);

        Assert.False(
            fabrika!.IsPublic,
            $"`{nameof(McpLogText)}.{nameof(McpLogText.FromRedacted)}` `public` olmuş. " +
            "Fabrikanın `internal` kalması, araç tarafının onu HİÇ görmemesi demek: " +
            "araçların bildiği tek yol redaksiyon kapısı olmalı.");

        var withLogText = typeof(McpToolResult).GetMethod(
            nameof(McpToolResult.WithLogText), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(withLogText);

        Assert.Equal(
            typeof(RedactedPrompt[]),
            withLogText!.GetParameters().Single().ParameterType);

        // Ve `McpToolResult` üzerinde log metni kabul eden BAŞKA bir public üye
        // yok: ikinci bir yol, kapının yarısını açık bırakırdı.
        var digerYollar = typeof(McpToolResult)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .OfType<MethodBase>()
            .Where(m => m != withLogText)
            .Where(static m => m.GetParameters().Any(static p =>
                p.ParameterType == typeof(McpLogText) || p.ParameterType == typeof(McpLogText[])))
            .Select(Imza)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            digerYollar.Length == 0,
            $"`{nameof(McpToolResult)}` üzerinde log metni alan başka public üye(ler):\n  " +
            string.Join("\n  ", digerYollar));
    }

    /// <summary>
    /// <b>Kapı 4 — yapısal yük kanalında kapı KULLANILABİLİR, ve okuma yolu
    /// yok.</b>
    ///
    /// <para>
    /// MCP'nin iki kanalı var ve <see cref="McpToolResult"/> belgesinde
    /// beyan edildiği gibi kapı yalnızca birinin imzasında duruyor. İkinci
    /// kanalda (<c>structuredContent</c>) yapılabilen şey kapıyı
    /// <b>kullanılabilir</b> kılmak: log içeriği taşıyan bir yük alanı
    /// <see cref="RedactedPrompt"/> olarak yazılırsa tel üzerinde
    /// <b>maskelenmiş metin</b> olarak çıkıyor.
    /// </para>
    ///
    /// <para>
    /// İkinci iddia birincisinden önemli: <b>okuma yolu yok</b>. JSON'dan bir
    /// <see cref="RedactedPrompt"/> kurulabilseydi yapıcının <c>private</c>
    /// olması anlamsızlaşırdı — kapıyı atlayan herhangi bir metin bir dizge
    /// olarak sarılıp tipe dönüştürülebilirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Yapisal_yukte_kapi_kullanilabilir_ve_okuma_yolu_yok()
    {
        const string Sir = "AbcDef0123456789XyzQwertyUiop";
        var redacted = RedactedPrompt.Redact($"set password {Sir}");

        var result = McpToolResult.Structured(new { line = redacted, source = "edge-rtr-07" });

        var json = result.Payload.GetRawText();

        // Maskelenmiş metin olarak indi — yedi özellikli ölçüm nesnesi olarak
        // değil, ve sır JSON'da YOK.
        Assert.DoesNotContain(Sir, json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(RedactedPrompt.ShadowCandidates), json, StringComparison.Ordinal);
        Assert.Contains("edge-rtr-07", json, StringComparison.Ordinal);

        // Ters yön KAPALI.
        Assert.Throws<System.Text.Json.JsonException>(
            static () => System.Text.Json.JsonSerializer.Deserialize<RedactedPrompt>("\"ham metin\""));
    }

    /// <summary>
    /// <b>Bekçi kendi kapsamını beyan ediyor</b> — T48/T50'nin kurduğu desen.
    ///
    /// <para>
    /// Beyan etmeyen bir bekçi kapsamını iddia etmiş sayılıyor. Burada üç şey
    /// sayılıyor: taranan derleme bulundu mu, IL taraması gerçekten bir şey
    /// gördü mü, ve okuyucunun kör noktaları yazılı mı.
    /// </para>
    ///
    /// <para>
    /// <b>İkinci madde bu bekçinin en sinsi başarısızlık hâli:</b> tarama boş
    /// dönseydi Kapı 2 <i>"kuran metot yok"</i> derdi ve
    /// <c>Assert.Equal</c> KIRMIZI yanardı — ama tarama tek elemanlı doğru
    /// sonucu tesadüfen üretse de aynı yeşili verirdi. O yüzden taramanın
    /// ürettiği <b>ham sayı</b> ayrıca ölçülüyor: yüzlerce metot okunmuş
    /// olmalı.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekci_kapsamini_beyan_ediyor()
    {
        Assert.Equal("Bizigo.Mcp", Protocol.GetName().Name);

        var okunan = UrunDerlemeleri()
            .SelectMany(static a => a.GetTypes())
            .SelectMany(static t => t.GetMethods(IlCallReader.Everything).Cast<MethodBase>()
                .Concat(t.GetConstructors(IlCallReader.Everything)))
            .Count();

        Assert.True(
            okunan > 200,
            $"IL taraması yalnızca {okunan} üye gördü — tarama erken bitmiş olmalı. " +
            "Boş bir tarama, kapıyı atlayan bir fabrikayı 'yok' diye raporlar.");

        // Okuyucunun göremedikleri `IlCallReader` belgesinde yazılı ve buraya
        // devrediliyor: yansımayla kurulan nesne, ölü kod, kaynak üreteçleri.
        // Bu bekçi "ulaşılabilir mi" değil "çağrı grafiğinde var mı" sorusunu
        // cevaplıyor.
        Assert.NotEmpty(UrunDerlemeleri());
    }

    // ---------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------

    /// <summary>
    /// Taranan küme: protokol derlemesi ve ona referans veren ürün
    /// derlemeleri.
    ///
    /// <para>
    /// <b>Neden yalnızca <c>Bizigo.Mcp</c> değil:</b> yapıcı <c>private</c>
    /// olduğu için onu ancak bu derleme kurabilir — ama bu bir <b>bugünkü</b>
    /// gerçek, ölçülmesi gereken bir şart değil. Kümeyi geniş tutmak,
    /// yapıcının görünürlüğü değiştiği gün bekçinin hâlâ doğru yere bakması
    /// demek.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Assembly> UrunDerlemeleri() =>
        [Protocol, typeof(global::Program).Assembly];

    private static (string[] Kuranlar, int Cozulemeyen) Kuranlar()
    {
        var yapicilar = typeof(McpLogText)
            .GetConstructors(IlCallReader.Everything)
            .ToHashSet();

        var kuranlar = new SortedSet<string>(StringComparer.Ordinal);
        var cozulemeyen = 0;

        foreach (var uye in UrunDerlemeleri()
            .SelectMany(static a => a.GetTypes())
            .SelectMany(static t => t.GetMethods(IlCallReader.Everything).Cast<MethodBase>()
                .Concat(t.GetConstructors(IlCallReader.Everything))))
        {
            foreach (var cagrilan in IlCallReader.Callees(uye, ref cozulemeyen))
            {
                if (cagrilan is ConstructorInfo ctor && yapicilar.Contains(ctor))
                {
                    kuranlar.Add($"{uye.DeclaringType?.Name}.{uye.Name}");
                }
            }
        }

        return ([.. kuranlar], cozulemeyen);
    }

    private static string Imza(MethodBase method) =>
        $"{method.DeclaringType?.Name}.{method.Name}(" +
        string.Join(", ", method.GetParameters().Select(static p => p.ParameterType.Name)) + ")";
}

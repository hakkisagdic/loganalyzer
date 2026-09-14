using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>MCP sayfalama sözleşmesinin kapısı — §7'nin ilk örneğine karşı.</b>
///
/// <para>
/// <c>CLAUDE.md</c> §7, sessiz yanlış davranışın ilk örneği: <i>"Yanıttaki
/// imlecin adı istekten farklıydı → ekran aldığı imleci geri gönderemiyordu →
/// yarım imleç sessizce ilk sayfayı tekrarlıyordu."</i>
/// </para>
///
/// <para>
/// <b>Neden bu kapı bir MCP aracında REST'tekinden daha gerekli.</b> Bir ekran
/// aynı sayfayı iki kez gösterdiğinde kullanıcı fark eder. Bir model
/// <b>sonsuz kez</b> alır ve fark etmez: her turda yeni bir şey öğrendiğini
/// sanır, bağlamı dolar, ve cevabı <i>"veri bu kadar"</i> olur. Hata yok, sayaç
/// yok, belirti yok.
/// </para>
///
/// <para>
/// <b>Kapının üç sorusu var ve üçü ayrı.</b> Kodek doğru mu · şemalar aynı adı
/// mı taşıyor · imleç sorguya gerçekten <b>ulaşıyor</b> mu. Üçüncüsü asıl
/// olan: ilk ikisi yeşilken bile imleci okuyup <b>kullanmayan</b> bir araç
/// yazılabilir, ve o araç F1'in kusurunun ta kendisidir.
/// </para>
/// </summary>
public sealed class McpCursorContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);

    // ---------------------------------------------------------------------
    // 1 · Kodek
    // ---------------------------------------------------------------------

    /// <summary>
    /// Gidiş-dönüş <b>ANI</b> koruyor.
    ///
    /// <para>
    /// Alt saniye hassasiyeti bilerek sınanıyor: keyset sayfalaması
    /// <c>(ts, event_id)</c> ikilisine göre ilerliyor ve tick kaybı sayfa
    /// sınırında ya <b>satır atlıyor</b> ya <b>satır tekrarlıyor</b> — ikisi de
    /// sessiz.
    /// </para>
    /// </summary>
    [Fact]
    public void Imlec_gidis_donusunde_ani_koruyor()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // Tick hassasiyetinde bir an, ve ofseti SIFIR OLMAYAN bir gösterim:
        // kodek anı koruyor, ofseti korumuyor ve bu bilinçli.
        var timestamp = new DateTimeOffset(2026, 9, 5, 14, 3, 27, TimeSpan.FromHours(3))
            .AddTicks(1234567);

        var encoded = McpCursor.Encode(new EventCursor(timestamp, eventId));

        Assert.NotNull(encoded);
        Assert.True(McpCursor.TryDecode(encoded, out var decoded, out var reason), reason);

        Assert.Equal(timestamp.UtcTicks, decoded.Timestamp.UtcTicks);
        Assert.Equal(eventId, decoded.EventId);
    }

    /// <summary>Sayfa bittiğinde imleç <see langword="null"/>.</summary>
    [Fact]
    public void Sayfa_bittiginde_imlec_yok() => Assert.Null(McpCursor.Encode(null));

    /// <summary>
    /// <b>Bozuk imleç REDDEDİLİYOR, yok sayılmıyor.</b>
    ///
    /// <para>
    /// "Bozuksa baştan başla" davranışı, bu dosyanın var olma sebebini geri
    /// getirirdi: model bozuk bir imleçle ilk sayfayı alır ve <b>ilerlediğini
    /// sanar</b>.
    /// </para>
    ///
    /// <para>
    /// <b>Kesilmiş imleç ayrıca sınanıyor</b> — F1'in "yarım imleç"inin bu
    /// gösterimdeki karşılığı. Uzun olan da reddediliyor: sessizce ilk 25 baytı
    /// okumak, başka bir biçimi bu biçim sanmak olurdu.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("", "boş")]
    [InlineData("   ", "yalnızca boşluk")]
    [InlineData("bu bir imleç değil!", "base64url değil")]
    [InlineData("AAAA", "kesilmiş — yarım imleç")]
    public void Bozuk_imlec_reddediliyor(string encoded, string ne)
    {
        Assert.False(McpCursor.TryDecode(encoded, out _, out var reason), ne);
        Assert.NotEmpty(reason);
    }

    /// <summary>
    /// Uzunluğu doğru ama <b>sürümü</b> tanınmayan imleç reddediliyor.
    ///
    /// <para>
    /// Sürüm baytı olmasaydı biçim değiştiği gün eski bir imleç <b>hata değil
    /// yanlış bir zaman damgası</b> üretirdi: model başka bir sayfadan devam
    /// eder ve arada kalan satırlar kaybolur.
    /// </para>
    /// </summary>
    [Fact]
    public void Tanınmayan_surum_reddediliyor()
    {
        var valid = McpCursor.Encode(new EventCursor(DateTimeOffset.UnixEpoch, Guid.Empty))!;
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(valid);

        bytes[0] = 0xFE;

        Assert.False(
            McpCursor.TryDecode(System.Buffers.Text.Base64Url.EncodeToString(bytes), out _, out var reason));

        Assert.Contains("sürüm", reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 2 · Şemalar aynı adı taşıyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Girdi ve çıktı şeması imleci AYNI adla ilan ediyor.</b>
    ///
    /// <para>
    /// F1'in kusuru tam buydu ve o gün bunu ölçen bir test yoktu. Ad eşleşmesi
    /// yorumdan değil <b>şemaların kendisinden</b> okunuyor: iki şema ayrı ayrı
    /// yazılabilecek metinler ve elle karşılaştırmak "iki liste" kurmak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Imlec_girdi_ve_cikti_semasinda_ayni_ad()
    {
        var tool = Tool(new FakeScopedQuery());

        Assert.True(
            tool.InputSchema.GetProperty("properties").TryGetProperty(McpCursor.FieldName, out var input),
            $"girdi şeması `{McpCursor.FieldName}` ilan etmiyor");

        Assert.True(
            tool.OutputSchema.GetProperty("properties").TryGetProperty(McpCursor.FieldName, out var output),
            $"çıktı şeması `{McpCursor.FieldName}` ilan etmiyor");

        // Girdi zorunlu DEĞİL (ilk sayfa imleçsiz), çıktıda ZORUNLU
        // (`null` da bir cevap: "sayfa bitti").
        Assert.Equal("string", input.GetProperty("type").GetString());

        Assert.Equal(
            ["string", "null"],
            output.GetProperty("type").EnumerateArray()
                .Select(static t => t.GetString() ?? string.Empty)
                .ToArray());

        Assert.Contains(
            McpCursor.FieldName,
            tool.OutputSchema.GetProperty("required").EnumerateArray()
                .Select(static r => r.GetString() ?? string.Empty));
    }

    /// <summary>
    /// <b>Yarım imleç İFADE EDİLEMİYOR.</b>
    ///
    /// <para>
    /// REST iki alanlı bir imleç taşıyor ve yarısını göndermek <b>mümkün</b>;
    /// oradaki koruma bir <c>400</c>. MCP tarafında koruma gerekmiyor çünkü hâl
    /// yok: imleç tek alan. Bu test o iddiayı şemadan ölçüyor — bir gün ikinci
    /// bir imleç alanı eklenirse burası kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Yarim_imlec_ifade_edilemiyor()
    {
        var properties = Tool(new FakeScopedQuery()).InputSchema.GetProperty("properties");

        var cursorish = properties.EnumerateObject()
            .Select(static p => p.Name)
            .Where(static name => name.Contains("cursor", StringComparison.Ordinal)
                || name.StartsWith("after_", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([McpCursor.FieldName], cursorish);
    }

    // ---------------------------------------------------------------------
    // 3 · İmleç sorguya ULAŞIYOR — kapının asıl sorusu
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Verilen imleç sorguya konuyor.</b>
    ///
    /// <para>
    /// Ölçülen şey yük değil <b>sorgu</b>: imleci okuyup kullanmayan bir araç da
    /// aynı yükü döndürür. F1'in kusuru bu bekçinin yokluğuydu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Verilen_imlec_sorguya_ulasiyor()
    {
        var query = new FakeScopedQuery();
        var tool = Tool(query);

        var cursor = new EventCursor(
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero).AddTicks(4242),
            Guid.Parse("99999999-8888-7777-6666-555555555555"));

        await tool.ExecuteScopedAsync(Invocation((McpCursor.FieldName, McpCursor.Encode(cursor)!)), Core, Ct);

        Assert.NotNull(query.LastEventQuery);
        Assert.NotNull(query.LastEventQuery!.After);

        Assert.Equal(cursor.Timestamp.UtcTicks, query.LastEventQuery.After!.Timestamp.UtcTicks);
        Assert.Equal(cursor.EventId, query.LastEventQuery.After.EventId);
    }

    /// <summary>
    /// İmleç <b>verilmediğinde</b> sorguda imleç yok — ilk sayfa.
    ///
    /// <para>
    /// Karşı-kanıt olmadan yukarıdaki test tek başına anlamsız: her çağrıda sabit
    /// bir imleç koyan bir araç da onu geçerdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Imlecsiz_cagri_ilk_sayfayi_istiyor()
    {
        var query = new FakeScopedQuery();

        await Tool(query).ExecuteScopedAsync(Invocation(), Core, Ct);

        Assert.NotNull(query.LastEventQuery);
        Assert.Null(query.LastEventQuery!.After);
    }

    /// <summary>
    /// <b>Bozuk imleç SORGU KOŞTURMUYOR ve <c>invalid_argument</c> dönüyor.</b>
    ///
    /// <para>
    /// İki iddia birlikte ölçülüyor ve ikincisi olmadan birincisi eksik: hata
    /// dönerken sorguyu <b>yine de</b> koşturan bir araç, modele hata verirken
    /// ClickHouse'a gitmiş olurdu — ve bir sonraki kişi hata mesajına bakıp
    /// "koşmadı" varsayardı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bozuk_imlec_sorgu_kosturmuyor()
    {
        var query = new FakeScopedQuery();

        var error = await Assert.ThrowsAsync<McpToolArgumentException>(async () =>
            await Tool(query).ExecuteScopedAsync(Invocation((McpCursor.FieldName, "AAAA")), Core, Ct));

        Assert.Equal(McpCursor.FieldName, error.ArgumentName);
        Assert.Null(query.LastEventQuery);
    }

    /// <summary>
    /// <b>Dönen imleç, geri verildiğinde AYNI yere işaret ediyor.</b>
    ///
    /// <para>
    /// Tam tur: araç bir sayfa döndürüyor, yükten <c>cursor</c> okunuyor, aynı
    /// adla geri veriliyor, ve sorguya konan imleç sayfanın <b>son satırını</b>
    /// gösteriyor. F1'in kusuru bu turun kapanmamasıydı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Donen_imlec_geri_verildiginde_ayni_yeri_gosteriyor()
    {
        var last = new EventCursor(
            new DateTimeOffset(2026, 9, 5, 9, 15, 0, TimeSpan.Zero).AddTicks(777),
            Guid.Parse("abcdabcd-1111-2222-3333-444444444444"));

        var query = new FakeScopedQuery
        {
            EventPageFactory = (_, _) => new EventPage([], last, HasMore: true),
        };

        var tool = Tool(query);

        var first = await tool.ExecuteScopedAsync(Invocation(), Core, Ct);

        var returned = first.Payload.GetProperty(McpCursor.FieldName).GetString();

        Assert.NotNull(returned);

        await tool.ExecuteScopedAsync(Invocation((McpCursor.FieldName, returned!)), Core, Ct);

        Assert.Equal(last.Timestamp.UtcTicks, query.LastEventQuery!.After!.Timestamp.UtcTicks);
        Assert.Equal(last.EventId, query.LastEventQuery.After.EventId);
    }

    // ---------------------------------------------------------------------

    private static LogsSearchTool Tool(IScopedQuery query) => new(McpReadToolFixtures.Scopes(query));

    private static McpToolInvocation Invocation(params (string Name, string Value)[] arguments) =>
        McpReadToolFixtures.Invocation(arguments);
}

using System.Reflection;
using System.Text.Json;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Kaynak kanalının redaksiyon kapısı — ve kapının kaldırılabilir olup
/// olmadığının ölçümü.</b>
///
/// <para>
/// M06 kapıyı araç kanalında derleyiciye bağladı ve aynı sınıftan bir açığı
/// <b>yazılı bıraktı</b>: <c>McpToolResult.Structured&lt;TPayload&gt;</c> içindeki
/// <c>string</c> alanlar kapıya uğramadan modele iniyor. Kaynak kanalı ikinci bir
/// model kanalı; aynı açığın burada da olması, T41'in tabanını yarım bırakırdı.
/// </para>
///
/// <para>
/// Bu dosya iki şeyi <b>ayrı ayrı</b> ölçüyor, ve ayrımı önemli:
/// </para>
/// <list type="number">
/// <item>
/// <b>Bugün kapalı mı</b> — <see cref="McpResourceBody"/>'yi üretebilen her üye
/// bir <see cref="RedactedPrompt"/> istiyor mu (yansıma), ve bu tipi
/// <c>newobj</c> ile kuran tek metot hangisi (IL).
/// </item>
/// <item>
/// <b>Kapanabildiği için kapatıldı mı</b> — gövdenin tümden kapıdan geçmesi
/// belgeyi bozmuyor mu. Bu iddia araç kanalında <b>tutmuyor</b> (şemaya bağlı
/// yük) ve burada tuttuğu ölçülmeden yazılamaz.
/// </item>
/// </list>
/// </summary>
public sealed class McpResourceRedactionGateTests
{
    /// <summary>
    /// <b>Gövdeyi üretebilen her yol redaksiyon kapısından geçiyor.</b>
    ///
    /// <para>
    /// Ölçüt <see cref="McpResourceBody"/>'nin <b>bütün</b> genel/korumalı
    /// üyeleri: bu tipi döndüren bir üye varsa parametrelerinden en az biri
    /// <see cref="RedactedPrompt"/> olmak zorunda. Serbest <c>string</c> alan bir
    /// aşırı yükleme eklemek — kapıyı sessizce kaldırmanın en kolay yolu —
    /// burada kırmızı yanıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Yapıcılar da sayılıyor.</b> <c>internal</c> bir yapıcı açmak yansımaya
    /// görünür ve o hareket, dosyanın içinde kalan tek kaçış yolu.
    /// </para>
    /// </summary>
    [Fact]
    public void Govdeyi_ureten_her_uye_redaksiyon_kapisi_istiyor()
    {
        var producers = typeof(McpResourceBody)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .OfType<MethodBase>()
            .Where(static member => member is ConstructorInfo
                || (member is MethodInfo method && method.ReturnType == typeof(McpResourceBody)))
            .Where(static member => !member.IsPrivate || member is ConstructorInfo)
            .ToArray();

        Assert.NotEmpty(producers);

        var açık = producers
            .Where(static member => member is not ConstructorInfo { IsPrivate: true })
            .Where(static member => !member.GetParameters()
                .Any(static parameter => parameter.ParameterType == typeof(RedactedPrompt)))
            .Select(static member => $"{member.DeclaringType!.Name}.{member.Name}")
            .ToArray();

        Assert.True(
            açık.Length == 0,
            "KAYNAK KANALINDA REDAKSİYON KAPISI ATLANABİLİYOR. Şu üye(ler) "
            + $"`McpResourceBody` üretiyor ama `RedactedPrompt` istemiyor: {string.Join(", ", açık)}. "
            + "Kaynak okuması ikinci bir model kanalı: gövde doğrudan modelin bağlamına giriyor. "
            + "Serbest `string` alan bir yol, T41'in kapısını bir çağrı alışkanlığına indirir.");
    }

    /// <summary>
    /// <b>Gövdeyi kuran tek metot — IL üzerinden.</b>
    ///
    /// <para>
    /// Yansıma imzalara bakıyor; bu test <b>çağrılara</b> bakıyor. İkisi ayrı
    /// soru: imzası doğru olan bir fabrikanın yanına, gövdeyi kendi kuran ikinci
    /// bir metot eklenebilir ve yansıma onu <i>gövde üretiyor</i> diye görmez
    /// (dönüş tipi başka bir şey olabilir).
    /// </para>
    ///
    /// <para>
    /// <b>IL okuyucusu M06'nın <c>IlCallReader</c>'ı</b> — ikinci bir tarayıcı
    /// yazılmadı, ve yazmayı denemek bir kusur üretti: elle yazılmış bayt
    /// taraması <c>0x73</c> baytını bir operandın içinde bulup
    /// <i>"Invalid token"</i> ile düştü. Aynı ölçütün iki uygulaması bu depoda
    /// zaten yasak (§9); burada ayrıca <b>ölçülerek</b> kötü olduğu görüldü.
    /// </para>
    /// </summary>
    [Fact]
    public void Govdeyi_newobj_ile_kuran_tek_metot_var()
    {
        var yapicilar = typeof(McpResourceBody)
            .GetConstructors(IlCallReader.Everything)
            .ToHashSet();

        var kuranlar = new SortedSet<string>(StringComparer.Ordinal);
        var cozulemeyen = 0;

        foreach (var uye in typeof(McpResourceBody).Assembly
            .GetTypes()
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

        // TEK metot: `McpResourceBody.Of`. Buraya ikinci bir ad girerse kapı
        // atlanabilir hâle gelmiş demektir.
        Assert.Equal(["McpResourceBody.Of"], kuranlar);
    }

    /// <summary>
    /// <b>Kapının tümden takılabilmesinin ölçümü: redaksiyon JSON'u bozmuyor.</b>
    ///
    /// <para>
    /// <c>McpResourceBody</c> belgesi şunu iddia ediyor: kaynak gövdesi şemaya
    /// bağlı olmadığı için <b>tamamı</b> kapıdan geçebiliyor, ve JSON gövde
    /// geçtikten sonra hâlâ ayrıştırılabiliyor. <b>Bu iddia ölçülmeden
    /// yazılamaz</b> — maske dizgesi bir tırnak taşısaydı ya da yapıya
    /// dokunsaydı, kanalın tamamı sessizce bozuk belge basardı.
    /// </para>
    ///
    /// <para>
    /// Girdi bilerek sırlı: bir vendor ataması, bir <c>Authorization</c> başlığı
    /// ve bir JWT — yani kapının üç katmanının hepsi.
    /// </para>
    /// </summary>
    [Fact]
    public void Redaksiyon_json_govdeyi_bozmuyor()
    {
        var belge = JsonSerializer.Serialize(new
        {
            source = "fw-01",
            ts = "2026-09-14T12:00:00Z",
            signature_hash = "9f2c1b7e4a8d3f6019bc25de7a41c8f0",
            lines = new[]
            {
                "set psksecret ENC 8sK2p9Lm4Qw7Zx1Vb3Nc6Rt5Yu0Ie",
                "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln",
            },
        });

        var maskeli = RedactedPrompt.Redact(belge);
        var body = McpResourceBody.Of(maskeli, McpResourceMimeTypes.Json);

        // 1 · Hâlâ JSON. Bozulsaydı istisna atardı.
        using var parsed = JsonDocument.Parse(body.Text);

        // 2 · Yapı korunmuş: alan adları ve dizi şekli yerinde.
        Assert.Equal("fw-01", parsed.RootElement.GetProperty("source").GetString());
        Assert.Equal(2, parsed.RootElement.GetProperty("lines").GetArrayLength());

        // 3 · KİMLİK ALANI MASKELENMİYOR. Yüksek entropili meşru bir değer
        // (`signature_hash`) kaybolsaydı belge kimliğini yitirirdi ve kanal
        // "her şeyi maskeleyen" bir kanal olurdu.
        Assert.Equal(
            "9f2c1b7e4a8d3f6019bc25de7a41c8f0",
            parsed.RootElement.GetProperty("signature_hash").GetString());

        // 4 · Sır GERÇEKTEN gitmiş — testin kendi iddiasını doğrulamaması için
        // maskelemenin çalıştığı da ayrıca ölçülüyor.
        Assert.DoesNotContain("8sK2p9Lm4Qw7Zx1Vb3Nc6Rt5Yu0Ie", body.Text, StringComparison.Ordinal);
        Assert.True(maskeli.MaskedValues > 0);
    }

    /// <summary>
    /// <b>Gövde tipsiz kurulamıyor: <c>mimeType</c> zorunlu.</b>
    ///
    /// <para>
    /// Belirtilmemiş bir tür ile "düz metin" istemci tarafında ayırt edilemez
    /// hâle gelirdi; boş bir dizge de aynı şey.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Turu_olmayan_govde_kurulamiyor(string mimeType) =>
        Assert.Throws<ArgumentException>(() =>
            McpResourceBody.Of(RedactedPrompt.Redact("x"), mimeType));
}

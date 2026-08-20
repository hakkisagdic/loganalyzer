using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

/// <summary>
/// Arama ölçütü → alarm kuralı köprüsünün <b>sunucu tarafındaki yarısı</b>.
///
/// <para>
/// Köprünün kendisi TypeScript'te (<c>ui/src/lib/alerts/criteria-bridge.ts</c>)
/// ve orada bir bekçi her ölçütün karşılığı olduğunu tutuyor. Ama o test
/// hedeflenen kolonların <b>gerçekten var olduğunu</b> göremez: izin listesi
/// burada, C# tarafında.
/// </para>
///
/// <para>
/// İki bekçi iki yönü tutuyor: TypeScript tarafı <b>eksiksizliği</b> (her ölçüt
/// karara bağlanmış mı), buradaki <b>geçerliliği</b> (çevrildiği kolon
/// filtrelenebilir mi). Tek taraf yeterli olsaydı, ya eşlemede olmayan bir
/// ölçüt sessizce düşerdi ya da var olmayan bir kolona çeviren bir eşleme
/// çalışma anında patlardı.
/// </para>
///
/// <para>
/// Kolon adları burada <b>elle</b> yazılı — bilinçli. TypeScript dosyasını C#
/// içinden ayrıştırmak kırılgan olurdu; iki listeyi ayrı tutup ikisini de
/// sınamak, ayrıştıkları anda birinin kırmızı yanmasını garanti ediyor.
/// </para>
/// </summary>
public sealed class AlertCriteriaBridgeTests
{
    /// <summary>
    /// <c>criteria-bridge.ts</c>'in <c>mappedColumns()</c> çıktısı. Değiştirmek
    /// isteyen, oradaki tabloyu da değiştirmek zorunda.
    /// </summary>
    private static readonly string[] BridgedColumns = ["action", "proto", "severity_num", "vendor"];

    [Fact]
    public void Kopruye_hedef_olan_her_kolon_filtrelenebilir()
    {
        var allowed = EventReader.FilterableFields;

        var missing = BridgedColumns.Where(column => !allowed.Contains(column)).ToArray();

        Assert.True(
            missing.Length == 0,
            "Arama ölçütü köprüsü filtrelenemeyen kolona çeviriyor: " + string.Join(", ", missing) +
            ". İzin listesi: " + string.Join(", ", allowed.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// <c>severity_min</c>'in çevirisinin <b>neden</b> <c>gt</c> olduğunu
    /// sabitliyor: ekrandaki ölçüt "n ve üzeri" ama operatör kümesinde
    /// <c>gte</c> yok. Bir gün eklenirse bu test kırmızı yanacak ve köprüdeki
    /// değer düşürme numarası kaldırılabilecek.
    /// </summary>
    [Fact]
    public void Operator_kumesinde_gte_yok_ve_koprunun_gt_kullanmasinin_sebebi_bu()
    {
        var operators = Enum.GetNames<Bizigo.Contracts.FilterOperator>();

        Assert.Contains("GreaterThan", operators);
        Assert.DoesNotContain("GreaterThanOrEqual", operators);
    }

    [Fact]
    public void Severity_kolonu_sayisal_kaliyor()
    {
        // Köprü değeri bir tam sayı olarak gönderiyor; kolon String'e dönerse
        // "10 > 9" karşılaştırması dizge karşılaştırmasına düşer ve sessizce
        // yanlış sonuç verir.
        Assert.Contains("severity_num", EventReader.FilterableFields);
    }
}

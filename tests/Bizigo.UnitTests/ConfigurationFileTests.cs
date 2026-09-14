using System.Text.Json;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Yapılandırma dosyalarının kendi bekçileri</b> (T57).
///
/// <para>
/// <b>Ölçülmüş bir kusurun bekçisi:</b> <c>src/Bizigo.Api/appsettings.json</c>
/// içinde <c>"Rca"</c> anahtarı <b>iki kez</b> vardı — biri <c>Quota</c> ve
/// <c>Schedules</c>, diğeri <c>Model</c> taşıyor. Ürün doğru çalışıyordu,
/// çünkü JSON yapılandırma sağlayıcısı <b>nesne</b> çakışmasında iki bloğu
/// sessizce birleştiriyor.
/// </para>
///
/// <para>
/// Sessizliğin iki bedeli vardı ve ikisi de §7'nin sınıfı:
/// </para>
/// <list type="number">
///   <item>
///     Sağlayıcı <b>yaprak</b> çakışmasında <b>fırlatıyor</b>. Yani
///     <c>Rca:Quota</c>'nın yanına bir gün <c>Rca:Model</c> ile aynı adı
///     taşıyan bir kardeş eklenseydi, uygulama <b>açılışta ölürdü</b> — ve
///     sebebi dosyanın hiçbir yerinde görünmezdi.
///   </item>
///   <item>
///     Dosyayı okuyan insan birleşmeyi <b>görmüyor</b>. İki blok yüz otuz satır
///     arayla duruyordu; <c>Rca</c>'nın neleri kapsadığını öğrenmenin tek yolu
///     dosyanın tamamını taramaktı.
///   </item>
/// </list>
///
/// <para>
/// <b>Neden bir bekçi, neden tek seferlik bir düzeltme değil:</b> ölçüldü —
/// sınıf tamamen mekanik. Kardeş düzeyde tekrar eden bir anahtar ayrıştırıcı
/// tarafından görülebiliyor, yargı gerektirmiyor, ve yanlış pozitifi yok.
/// Tek seferlik düzeltme aynı hatayı bir sonraki <c>appsettings</c> için açık
/// bırakırdı; bu depoda "bir kez oldu" ile "bir daha olmayacak" arasındaki
/// fark tam olarak bir bekçi.
/// </para>
/// </summary>
public sealed class ConfigurationFileTests
{
    /// <summary>
    /// Depodaki her <c>appsettings*.json</c> — derleme çıktıları hariç.
    ///
    /// <para>
    /// <c>bin/</c> ve <c>obj/</c> <b>dışlanıyor</b>: oradaki kopyalar kaynağın
    /// aynısı ve her biri aynı kusuru ikinci kez raporlardı. Daha kötüsü, bir
    /// dosya kaynaktan silindiğinde çıktıda kalmaya devam ediyor — yani bekçi
    /// artık var olmayan bir dosya hakkında konuşurdu.
    /// </para>
    /// </summary>
    private static readonly string[] Files =
        [.. Directory
            .EnumerateFiles(RepositoryLayout.Root, "appsettings*.json", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(static path => Path.GetRelativePath(RepositoryLayout.Root, path))
            .Order(StringComparer.Ordinal)];

    public static TheoryData<string> SettingsFiles => [.. Files];

    /// <summary>
    /// <b>Hiçbir yapılandırma dosyasında kardeş düzeyde tekrar eden anahtar
    /// yok.</b>
    ///
    /// <para>
    /// Tarama <b>her düzeyde</b> yapılıyor, yalnızca kökte değil: iç içe bir
    /// nesnede tekrar eden anahtar da aynı sessiz birleşmeyi üretiyor ve
    /// kökteki kadar görünmez.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(SettingsFiles))]
    public void Yapilandirmada_kardes_duzeyde_tekrar_eden_anahtar_yok(string relativePath)
    {
        var path = Path.Combine(RepositoryLayout.Root, relativePath);

        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var duplicates = new List<string>();

        Walk(document.RootElement, string.Empty, duplicates);

        Assert.True(
            duplicates.Count == 0,
            $"{relativePath} içinde kardeş düzeyde tekrar eden anahtar var: " +
            $"{string.Join(", ", duplicates)}.\n\n" +
            "JSON yapılandırma sağlayıcısı NESNE çakışmasını sessizce birleştiriyor, " +
            "YAPRAK çakışmasında ise fırlatıyor. Yani bugün çalışan bir dosya, iki bloğa " +
            "aynı adda bir yaprak eklendiği gün uygulamayı AÇILIŞTA öldürür — ve sebebi " +
            "dosyada görünmez. Blokları tek bir nesnede birleştirin.");
    }

    /// <summary>
    /// <b>Bekçinin kendisi ölçülebiliyor:</b> tarama gerçekten dosya buluyor.
    ///
    /// <para>
    /// <c>MemberData</c> boş dönseydi yukarıdaki teori <b>hiç koşmadan</b>
    /// yeşil sayılırdı — bu depoda adı konmuş sınıf: sessizce atlayan bir
    /// bekçi, bekçinin kendisinden tehlikeli (§7).
    /// </para>
    /// </summary>
    [Fact]
    public void Taranan_yapilandirma_dosyasi_var()
    {
        Assert.NotEmpty(Files);

        // Ürünün kendi dosyası mutlaka kapsamda: tarama kökü kayarsa burası
        // kırmızı yanıyor ve teori sessizce boşalmıyor.
        Assert.Contains(
            Files,
            path => path.EndsWith("Bizigo.Api" + Path.DirectorySeparatorChar + "appsettings.json", StringComparison.Ordinal));
    }

    private static void Walk(JsonElement element, string path, List<string> duplicates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        duplicates.Add(path.Length == 0 ? property.Name : $"{path}:{property.Name}");
                    }

                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}:{property.Name}", duplicates);
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var index = 0;

                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]", duplicates);
                }

                break;
            }
        }
    }
}

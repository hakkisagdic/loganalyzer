using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;

namespace Bizigo.Alerting;

/// <summary>
/// Kuralın kaydedilmiş araması (T21: "kaydedilmiş arama + tip + parametreler").
///
/// <para>
/// <b>Serbest SQL yok</b> — kasten. Kapsam zorlaması yalnızca sorgu API'sinde
/// uygulanıyor (K17); ham SQL kabul eden bir kural alanı, kapsam ayrımını arka
/// kapıdan delerdi ve bunu yapan kişi kötü niyetli bile olmak zorunda değil.
/// Alan filtreleri <see cref="FieldFilter"/> üzerinden gidiyor, yani okuyucudaki
/// izin listesi burada da geçerli.
/// </para>
///
/// <para>
/// F3'te Sigma kurallarının ürettiği SQL de bu motora takılacak. O yüzden bu tip
/// "arama" soyutlamasının tamamı değil, <b>bugünkü</b> hâli: Sigma geldiğinde
/// yanına ikinci bir arama kaynağı eklenecek, bu tipin anlamı değişmeyecek.
/// </para>
/// </summary>
public sealed record AlertSearch
{
    public string? FullText { get; init; }

    public IReadOnlyList<FieldFilter> Filters { get; init; } = [];

    /// <summary>
    /// Kaynak daraltması. Sessizlik tipinde <b>tek anlamlı alan</b>: hangi
    /// kaynakların susmasının izleneceği. Boşsa kapsamdaki tüm kaynaklar.
    /// </summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public IReadOnlyList<ParseStatus> ParseStatuses { get; init; } = [];
}

/// <summary>
/// <see cref="AlertSearch"/> ile veritabanındaki JSON arasındaki tek dönüşüm.
///
/// <para>
/// Ayarlar <c>static readonly</c>: <c>JsonSerializerOptions</c> her çağrıda
/// yeniden kurulursa .NET her seferinde yeni bir sözleşme önbelleği üretir ve
/// bu, değerlendirme başına ölçülebilir bir maliyet olur — motor dakikada
/// yüzlerce kez buradan geçiyor.
/// </para>
/// </summary>
public static class AlertSearchCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Operatör ve durum adları veritabanında okunabilir kalsın: `"Equals"`
        // sayı olarak yazılsaydı bir enum'a değer eklendiği gün eski satırlar
        // sessizce başka bir operatöre kayardı.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AlertSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return JsonSerializer.Serialize(search, Options);
    }

    /// <summary>
    /// Bozuk ya da boş JSON <b>istisna fırlatıyor</b>, boş aramaya düşmüyor.
    /// Sessizce boş aramaya düşmek, filtresiz bir kuralın kapsamdaki her olayı
    /// sayması demekti — yani en pahalı sorgunun en sessiz yoldan üretilmesi.
    /// </summary>
    public static AlertSearch Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AlertSearch();
        }

        return JsonSerializer.Deserialize<AlertSearch>(json, Options)
            ?? throw new JsonException("Kuralın arama tanımı çözülemedi.");
    }
}

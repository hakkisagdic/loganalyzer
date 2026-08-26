using System.Diagnostics.CodeAnalysis;

namespace Bizigo.ScenarioPlugin;

/// <summary>
/// <b>Koşum anının</b> doğrulaması: kanıt bloğunun alanları sağlayıcının kendi
/// şemasına uyuyor mu (§6.1).
///
/// <para>
/// Çekirdek bu arayüzü yükleme anında <b>çağırmıyor</b> ve bu bilerek: şekli
/// bilen taraf sağlayıcı. Çekirdeğin bildiği tek şey zarf — sağlayıcı adının
/// kayıtlı olduğu ve bloğun iyi biçimli olduğu.
/// </para>
///
/// <para>
/// Bugün üretimde uygulaması <b>yok</b>. Arayüz şimdi yazıldı çünkü ayrımı
/// sonradan bölmek zor; ilk uygulaması, kanıt bloğunun şeklini gerçekten
/// tüketen ilk sağlayıcıyla gelecek. Şeklin kendisi çivilenmedi (§5.2) —
/// çivilenen şey, doğrulamanın <b>nerede</b> durduğu.
/// </para>
/// </summary>
public interface IScenarioEvidenceSchema
{
    /// <summary><see cref="IScenarioProviderRegistry"/>'deki kayıtlı ad.</summary>
    string ProviderId { get; }

    /// <summary>
    /// İhlal listesi; boş liste "uyuyor" demek. İstisna fırlatmıyor — koşum
    /// tarafı ihlallerin <b>hepsini</b> tek seferde görmeli.
    /// </summary>
    IReadOnlyList<string> Validate(ScenarioValue.Mapping content);
}

/// <summary>
/// Yükleme anında <c>evidence.providers</c>'ın karşılığı olan küme.
///
/// <para>
/// Küme <b>keşfediliyor</b>, elle yazılmıyor: üretimde kayıtlı
/// <c>IEvidenceProvider</c>'lardan besleniyor. Elle yazılan bir liste, listede
/// olmayan sağlayıcıyı kapıya görünmez yapardı — ve kapı yeşil yanardı.
/// </para>
/// </summary>
public interface IScenarioProviderRegistry
{
    IReadOnlyCollection<string> ProviderIds { get; }

    bool IsRegistered(string providerId);

    /// <summary>
    /// Koşum anının şeması. Yükleme anında çağrılmıyor; kayıtsız sağlayıcıyı
    /// yakalayan şey bu değil, <see cref="IsRegistered"/>.
    /// </summary>
    bool TryGetEvidenceSchema(string providerId, [NotNullWhen(true)] out IScenarioEvidenceSchema? schema);
}

/// <summary>
/// Elle verilmiş bir id kümesinden kurulan kayıt. Testler ve senaryo doğrulama
/// araçları için; üretimde <c>EvidenceScenarioProviderRegistry</c> kullanılıyor.
/// </summary>
public sealed class ScenarioProviderRegistry : IScenarioProviderRegistry
{
    private readonly HashSet<string> _ids;
    private readonly Dictionary<string, IScenarioEvidenceSchema> _schemas;

    public ScenarioProviderRegistry(
        IEnumerable<string> providerIds,
        IEnumerable<IScenarioEvidenceSchema>? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(providerIds);

        _ids = new HashSet<string>(providerIds, StringComparer.Ordinal);
        _schemas = (schemas ?? []).ToDictionary(s => s.ProviderId, StringComparer.Ordinal);

        // Şeması olup kayıtlı olmayan bir sağlayıcı sessiz bir yanlış davranış:
        // şema hiç koşmaz ve kimse fark etmez.
        var orphan = _schemas.Keys.FirstOrDefault(id => !_ids.Contains(id));
        if (orphan is not null)
        {
            throw new ArgumentException(
                $"'{orphan}' için kanıt şeması verildi ama sağlayıcı kayıtlı değil; şema hiç koşmazdı.",
                nameof(schemas));
        }
    }

    public IReadOnlyCollection<string> ProviderIds => _ids;

    public bool IsRegistered(string providerId) => _ids.Contains(providerId);

    public bool TryGetEvidenceSchema(string providerId, [NotNullWhen(true)] out IScenarioEvidenceSchema? schema) =>
        _schemas.TryGetValue(providerId, out schema);
}

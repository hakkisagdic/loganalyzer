namespace Bizigo.ScenarioPlugin;

/// <summary>
/// Bölmenin <b>koşum anı</b> yarısı (§6.1).
///
/// <para>
/// Yükleme anı zarfı doğruladı: sağlayıcı adı kayıtlı, blok iyi biçimli,
/// kısıtlar adlandırılmış. Geriye <b>tek</b> soru kaldı ve onu yalnızca
/// sağlayıcı cevaplayabiliyor: alanlar kendi şemasına uyuyor mu.
/// </para>
///
/// <para>
/// Ayrımın bedeli açık ve kabul edildi: şeması olmayan bir sağlayıcının kanıt
/// bloğu <b>hiç doğrulanmıyor</b>. Bu, kapının sessizce atlaması değil —
/// <see cref="Describe"/> hangi sağlayıcının şemasız geçtiğini söylüyor, yani
/// "doğrulandı" ile "doğrulanacak kimse yoktu" ayırt edilebiliyor.
/// </para>
/// </summary>
public static class ScenarioEvidenceGate
{
    /// <param name="scenario">Zarfı doğrulanmış senaryo.</param>
    /// <param name="registry">Sağlayıcı kaydı; şemalar buradan geliyor.</param>
    /// <returns>İhlal listesi; boş liste "koşabilir" demek.</returns>
    public static IReadOnlyList<string> Check(ScenarioDefinition scenario, IScenarioProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(registry);

        var violations = new List<string>();

        foreach (var providerId in scenario.Evidence.Providers)
        {
            if (!registry.TryGetEvidenceSchema(providerId, out var schema))
            {
                continue;
            }

            foreach (var violation in schema.Validate(scenario.Evidence.Content))
            {
                violations.Add($"{scenario.Metadata.Id} · {providerId}: {violation}");
            }
        }

        return violations;
    }

    /// <summary>
    /// Hangi sağlayıcı şemayla doğrulandı, hangisi şemasız geçti.
    /// <b>"Bakılmadı" ile "bakıldı, temiz" ayrı cümleler</b> — kanıt katmanının
    /// <c>EvidenceStatus</c> ile çözdüğü hata sınıfının aynısı.
    /// </summary>
    public static IReadOnlyList<string> Describe(ScenarioDefinition scenario, IScenarioProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(registry);

        return
        [
            .. scenario.Evidence.Providers.Select(id =>
                registry.TryGetEvidenceSchema(id, out _)
                    ? $"{id}: şemayla doğrulandı"
                    : $"{id}: şema kayıtlı DEĞİL — kanıt bloğu doğrulanmadı"),
        ];
    }
}

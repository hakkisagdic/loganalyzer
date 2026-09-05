using Bizigo.Contracts.Security;

namespace Bizigo.Rca.Models;

/// <summary>
/// Model ucunun yapılandırması (K6 — OpenAI-uyumlu uç, base URL config'den).
///
/// <para>
/// <b>Yerel ve uzak için ayrı seçenek sınıfı yok</b> ve olmamalı: K6 tek bir
/// soyutlama diyor (OpenAI-uyumlu endpoint) ve Ollama, vLLM, kurum içi GPU
/// kümesi bu soyutlamanın altında aynı şekilde konuşuyor. İki sınıf olsaydı
/// K6'nın kapısı iki kez yazılırdı ve biri eksik kalırdı.
/// </para>
/// </summary>
public sealed class ModelEndpointOptions
{
    public const string SectionName = "Rca:Model";

    /// <summary>
    /// Kayıtlarda ve hata mesajlarında görünen ad. Uç değişince hangi ucun
    /// reddedildiği sorusunun cevabı bu.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>OpenAI-uyumlu taban adres — <c>http://localhost:11434/v1</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// <b>K6 beyanı.</b> Varsayılan <see cref="DataBoundary.Unspecified"/>
    /// ve o değerde uç kurulamıyor — beyan etmemek bir seçenek değil.
    /// </summary>
    public DataBoundary DataBoundary { get; set; } = DataBoundary.Unspecified;

    /// <summary>
    /// <c>raw</c> düzeyinin açık olup olmadığı. Varsayılan <b>kapalı</b>.
    ///
    /// <para>
    /// Bu bayrak sağlayıcı hakkında değil <b>kurum</b> hakkında: hangi
    /// sağlayıcı seçilirse seçilsin <c>raw</c> ayrı bir kararla açılıyor.
    /// §2.1'in "taban ölçülene kadar kapalı" sıralamasının kaydı.
    /// </para>
    /// </summary>
    public bool AllowRawContentLevel { get; set; }

    /// <summary>
    /// Beyanın adres doğrulamasından muaf tutulduğu hâl ve <b>gerekçesi</b>.
    ///
    /// <para>
    /// Gerçek bir ihtiyaç: kurumun kendi AS'inde yönlendirilebilir adres
    /// kullanan bir GPU kümesi, adres sınıfına bakan bir kapıda yanlış yere
    /// düşer. Muafiyet mümkün ama <b>bedava değil</b> — gerekçe yazılmadan
    /// açılmıyor, ve gerekçe koşum kaydına giriyor. §8'in muafiyet disiplini:
    /// istisna <b>iki ayrı bilinçli hareket</b> istiyor.
    /// </para>
    /// </summary>
    public string? BoundaryOverrideReason { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Uç bir anahtar istiyorsa. <b>Değerin kendisi değil</b> — okunacağı
    /// ortam değişkeninin adı; simülatör profillerinin
    /// <c>credential_env</c> disiplininin aynısı.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }
}

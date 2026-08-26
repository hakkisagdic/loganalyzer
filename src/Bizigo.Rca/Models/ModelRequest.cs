using Bizigo.Contracts.Security;

namespace Bizigo.Rca.Models;

/// <summary>
/// Modele gidebilen <b>tek</b> şey — ve iki sözün buluştuğu yer.
///
/// <para>
/// <b>T41'in şartı burada karşılığını buluyor.</b> T41 redaksiyon kapısını
/// <see cref="RedactedPrompt"/> tipine bağlarken bir şart koymuştu: prompt'u
/// kuran taraf girdisini <c>string</c> değil o tipten almalı, yoksa garanti
/// yarım kalır. Bu tip o şartı uyguluyor —
/// <see cref="Create"/> bir <see cref="RedactedPrompt"/> istiyor, dolayısıyla
/// kapıdan geçmemiş bir metin modele <b>derlenmiyor</b>.
/// </para>
///
/// <para>
/// <b>Ve K6 ile içerik düzeyi tek yerde buluşuyor.</b> İkisi ayrı yerlerde
/// kontrol edilseydi ayrışırlardı — bu turda dört kez ödenen şeklin beşincisi.
/// Burada bir istek üretmek üç şeyi birden gerektiriyor: kapıdan geçmiş metin,
/// K6'yı geçmiş uç, ve o uçta açık olan bir düzey.
/// </para>
///
/// <para>
/// Yapıcı yine <c>private</c>, üretici tek. T41'de aynı karar aynı gerekçeyle
/// verilmişti: <c>string</c> döndüren bir kapı bir çağrı alışkanlığıdır ve
/// unutulduğu gün hiçbir şey kırılmaz.
/// </para>
/// </summary>
public sealed class ModelRequest
{
    private ModelRequest(
        ModelEndpoint endpoint,
        PromptContentLevel level,
        RedactedPrompt system,
        RedactedPrompt user)
    {
        Endpoint = endpoint;
        Level = level;
        System = system;
        User = user;
    }

    public ModelEndpoint Endpoint { get; }

    public PromptContentLevel Level { get; }

    /// <summary>Sistem yönergesi — o da kapıdan geçiyor.</summary>
    public RedactedPrompt System { get; }

    public RedactedPrompt User { get; }

    /// <summary>
    /// Bu isteğin ürettiği kanıt alanları: uç, düzey ve <b>redaksiyonun kendi
    /// sayıları</b> bir arada.
    ///
    /// <para>
    /// Gölge sayıları burada da yayılıyor çünkü terfi kararı prompt başına
    /// okunacak (T41 §3) — ve <b>sıfırken de</b> yazılıyorlar.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, object> AuditFields()
    {
        var fields = new Dictionary<string, object>(Endpoint.AuditFields(), StringComparer.Ordinal)
        {
            ["prompt_content_level"] = Level.ToString().ToLowerInvariant(),
        };

        foreach (var (key, value) in User.EvidenceFields())
        {
            fields[key] = value;
        }

        return fields;
    }

    /// <summary>
    /// İstek üretir; üretemezse <b>sebebiyle</b> reddeder.
    ///
    /// <para>
    /// İstisna fırlatmıyor: ret bir arıza değil bir <b>karar</b>, ve kararın
    /// kaydı tutulacak. Bir istisna yakalanmadığı gün koşumu düşürürdü;
    /// yakalandığı gün sebebi kaybolurdu.
    /// </para>
    /// </summary>
    public static ModelRequestResult Create(
        ModelEndpoint endpoint,
        PromptContentLevel level,
        RedactedPrompt system,
        RedactedPrompt user)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(user);

        if (!Enum.IsDefined(level))
        {
            return new ModelRequestResult(null, $"Bilinmeyen içerik düzeyi: {level}.");
        }

        if (level == PromptContentLevel.Raw && !endpoint.AllowRawContentLevel)
        {
            return new ModelRequestResult(
                null,
                "`raw` düzeyi kapalı. RCA §2.1 `masked` ve `raw` düzeylerini taban " +
                "ölçülene kadar kapalı tutuyordu; T41 tabanı ölçtü ve `raw` artık " +
                "açılabilir — ama açılışın ayrı ve görünür bir hareket olması şart: " +
                "`AllowRawContentLevel`.");
        }

        return new ModelRequestResult(new ModelRequest(endpoint, level, system, user), null);
    }
}

/// <summary>Ret de bir sonuç; sessiz <see langword="null"/> sebebi siler.</summary>
public sealed record ModelRequestResult(ModelRequest? Request, string? Rejection)
{
    public bool Allowed => Request is not null;
}

using System.Text.Json.Serialization;

namespace Bizigo.Simulators.Mcp;

/// <summary>
/// Bir cihaz hakkında <b>bu katmandan ne istendiği</b>.
///
/// <para>
/// <b>Bu tip simülatörün gerçeğini değil MCP'nin niyetini tutuyor</b>, ve ayrım
/// alan adlarına yazılı. Sebebi: simülatör tek atımlık bir CLI, uzun ömürlü bir
/// prosesi yok. MCP bir senaryo "ayarladığında" ortada onu uygulayan bir daemon
/// yok — istek burada duruyor ve <b>bir sonraki basım</b> onu okuyor. İkisi
/// arasındaki pencerede filo hâlâ eski davranışta.
/// </para>
///
/// <para>
/// <b>Niyet ile etki neden AYRI alanlar.</b> Tek bir <c>updated_at</c> yazsaydık
/// <i>"saat 10'da saat-kaymasi istendi"</i> ile <i>"saat 10'da saat-kaymasi tele
/// yazıldı"</i> aynı kutuya düşerdi ve okuyan ikincisini varsayardı — bu deponun
/// §7'de tarif ettiği sınıf: hata yok, sayaç yok, ve okunan şey yanlış.
/// Ayrıştıkları an <see cref="ScenarioSetAt"/> dolu, <see cref="LastAppliedAt"/>
/// boş oluyor ve <c>sim.state</c> bunu <b>söylüyor</b>.
/// </para>
/// </summary>
/// <param name="Scenario">
/// İstenen senaryo adı. <see langword="null"/> ya da <c>baseline</c>: değişim
/// istenmiyor. Ad <b>doğrulanmış</b> olarak buraya giriyor —
/// <c>sim.scenario.set</c> bilinmeyen bir adı hiç yazmıyor.
/// </param>
/// <param name="ScenarioSetAt">
/// <b>Niyet:</b> bu senaryonun ne zaman istendiği.
/// </param>
/// <param name="LastAppliedAt">
/// <b>Etki:</b> bu senaryonun en son ne zaman gerçekten tele yazıldığı
/// (<c>sim.syslog.burst</c> dolduruyor). <see langword="null"/> ise
/// <b>istendi ama hiç uygulanmadı</b> — ve bu, <i>"senaryo yok"</i> ile
/// karıştırılmaması gereken ayrı bir hâl.
/// </param>
/// <param name="Silenced">
/// Cihaz susturuldu mu. <b>Basmayı kesmek demek, container'ı durdurmak
/// değil</b>: aracın sınavı sessizlik korelasyonu, yani ürünün gördüğü şeyin
/// <i>log akışının durması</i> olması gerekiyor. Container'ın ölmesi farklı bir
/// olay (ulaşılamama) ve başka bir senaryonun konusu.
/// </param>
/// <param name="SilencedAt">Susturmanın istendiği an.</param>
public sealed record SimulatorDeviceState(
    [property: JsonPropertyName("scenario")] string? Scenario = null,
    [property: JsonPropertyName("scenario_set_at")] DateTimeOffset? ScenarioSetAt = null,
    [property: JsonPropertyName("last_applied_at")] DateTimeOffset? LastAppliedAt = null,
    [property: JsonPropertyName("silenced")] bool Silenced = false,
    [property: JsonPropertyName("silenced_at")] DateTimeOffset? SilencedAt = null);

/// <summary>
/// <c>bizigo-sim</c> yüzeyinin <b>tek</b> kalıcı durumu.
///
/// <para>
/// <b>Neden dosyada, neden bellekte değil.</b> stdio taşımasında <b>her bağlantı
/// kendi süreci</b>. Bellekte tutulan bir durum oturumla birlikte ölürdü ve iki
/// istemci <b>farklı</b> durum görürdü — yani <c>sim.state</c>'in ve
/// <c>sim.device.silence</c>'ın verdiği cevap, kim sorduğuna göre değişirdi.
/// İkisi de yalan söylemiş olurdu ve yalanın belirtisi olmazdı.
/// </para>
///
/// <para>
/// <b>Neden bir daemon değil.</b> Uzun ömürlü bir simülatör prosesi açmak
/// alternatifti ve elendi: bu depoda §3 kaçak proses konusunda net ve makine
/// bugün onun bedelini iki kez ödedi. Dosya, daemon'ın verdiği paylaşılan
/// durumu yeni bir port ve yeni bir ömür yönetimi olmadan veriyor.
/// </para>
/// </summary>
/// <param name="SchemaVersion">
/// Şema sürümü. <b>İlk sürümde yazılmasının sebebi ikinci sürüm:</b> alan
/// eklendiğinde eski bir dosyayı okuyan kod, sürümü görmeden onu <i>eksik</i>
/// değil <i>varsayılan</i> diye okur ve fark hiçbir yerde görünmez.
/// </param>
/// <param name="Devices">
/// Profil kimliği → durum. <b>Yalnızca dokunulmuş cihazlar burada</b>; filonun
/// tamamı değil. Bir cihazın burada olmaması <i>"varsayılan hâlde"</i> demek ve
/// <c>sim.state</c> bunu ayrıca söylüyor.
/// </param>
public sealed record SimulatorState(
    [property: JsonPropertyName("schema_version")] int SchemaVersion = SimulatorState.CurrentSchemaVersion,
    [property: JsonPropertyName("devices")] IReadOnlyDictionary<string, SimulatorDeviceState>? Devices = null)
{
    /// <summary>Bugünkü şema sürümü.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Boş bir durum — dosya hiç yokken okunan şey.</summary>
    public static SimulatorState Empty { get; } = new();

    /// <summary>
    /// Cihaz kayıtları; <see langword="null"/> gelmiyor.
    ///
    /// <para>
    /// Serileştirici <c>devices</c> anahtarı olmayan bir dosyada
    /// <see cref="Devices"/>'ı <see langword="null"/> bırakıyor. Çağıranların
    /// her birinde <c>?? []</c> yazmak, bir gün birinin yazmayı unutması demek;
    /// tek yerde çözülüyor.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, SimulatorDeviceState> DeviceStates =>
        Devices ?? new Dictionary<string, SimulatorDeviceState>(StringComparer.Ordinal);

    /// <summary>Cihazın durumu, ya da hiç dokunulmadıysa varsayılan.</summary>
    public SimulatorDeviceState For(string deviceId) =>
        DeviceStates.TryGetValue(deviceId, out var state) ? state : new SimulatorDeviceState();

    /// <summary>Tek bir cihazın durumunu değiştirmiş yeni bir kopya.</summary>
    public SimulatorState With(string deviceId, SimulatorDeviceState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(state);

        var devices = new Dictionary<string, SimulatorDeviceState>(DeviceStates, StringComparer.Ordinal)
        {
            [deviceId] = state,
        };

        return this with { SchemaVersion = CurrentSchemaVersion, Devices = devices };
    }
}

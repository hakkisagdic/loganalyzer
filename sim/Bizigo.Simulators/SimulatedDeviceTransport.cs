using Bizigo.Devices;

namespace Bizigo.Simulators;

/// <summary>
/// <b>N1 — süreç içi sahte taşıyıcı.</b>
///
/// <para>
/// Profilin seçili senaryosundaki config'i döndürüyor. SSH yok, container yok,
/// Docker yok — yani ajan sınırında koşabiliyor (§2).
/// </para>
///
/// <para>
/// <b>NE KANITLADIĞI:</b> toplayıcı, <c>ConfigNormalizer</c>, <c>ConfigDiff</c>
/// ve maskeleme zinciri; yani cihazdan gelen metnin ürün içinde doğru
/// işlendiği.
/// </para>
///
/// <para>
/// <b>NE KANITLAMADIĞI:</b> SSH'ın kendisi. Kimlik doğrulama, komut
/// çalıştırma, çıktı çerçeveleme, sayfalama ve zaman aşımı bu seviyede
/// <b>hiç koşmuyor</b>. Bu sınır burada yazılı olmak zorunda: bu depoda adı ile
/// gövdesi ayrışan bir bekçi (§6) defalarca ölçüldü ve N1 ile yazılan bir test
/// "cihazdan config çekiliyor" DEMEZ — diyebileceği şey "toplayıcı verilen
/// çıktıyı doğru işliyor".
/// </para>
/// </summary>
public sealed class SimulatedDeviceTransport : IDeviceTransport
{
    private readonly SimulatorProfile _profile;
    private readonly string _profileDirectory;
    private readonly string _scenario;

    /// <param name="scenario">
    /// Boş bırakılırsa <c>baseline</c>. Varsayılanın statik olması bilinçli:
    /// aynı girdi aynı çıktı, yoksa test duvar saatine bağlanır (§6).
    /// </param>
    public SimulatedDeviceTransport(
        SimulatorProfile profile,
        string profileDirectory,
        string? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);

        _profile = profile;
        _profileDirectory = profileDirectory;
        _scenario = scenario ?? string.Empty;
    }

    /// <summary>Bu taşıyıcıya gelen komutlar — testin ne çağrıldığını görmesi için.</summary>
    public List<string> ReceivedCommands { get; } = [];

    public Task<DeviceCommandResult> RunAsync(
        DeviceTarget target,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(commands);

        cancellationToken.ThrowIfCancellationRequested();

        ReceivedCommands.AddRange(commands);

        if (_profile.Config is null)
        {
            // Config'i olmayan profil (ör. yalnızca syslog basan `lb-web-01`)
            // bu yüzeyde BAŞARISIZ dönüyor, boş metin değil. Boş metin
            // "cihaz bağlandı ama config'i yok" derdi; gerçek şu ki bu cihazın
            // config yüzeyi hiç yok.
            return Task.FromResult(DeviceCommandResult.Failed(
                // Bu cihazın config YÜZEYİ yok — komut reddi değil, ulaşılamama
                // da değil. N1 bir soket açmadığı için "ulaşılamadı" demek
                // yanlış olurdu; en yakın doğru şey cihazın bu komutu
                // karşılamaması.
                DeviceFailureKind.CommandRejected,
                $"'{_profile.Id}' profilinin config yüzeyi yok."));
        }

        // ÖNCE YÜZEY, SONRA PROFİL (S04).
        //
        // Sıra önemli: `saat-kaymasi` config sözlüğünde aranırsa bulunamaz ve
        // hata "profilde böyle bir senaryo yok" der — yani arıza profil
        // dosyasında aranır. Oysa senaryo var, yalnızca BAŞKA BİR YÜZEYE ait.
        // Motor bunu önce söylüyor.
        if (Scenarios.Reject(_scenario, ScenarioSurface.Config) is { } yuzeyHatasi)
        {
            return Task.FromResult(
                DeviceCommandResult.Failed(DeviceFailureKind.CommandRejected, yuzeyHatasi));
        }

        // TEK PREDICATE: "bu ad baseline mi" sorusunu taşıyıcı kendi
        // cevaplamıyor. Cevabı burada tekrarlamak, S04'ün düştüğü kusurun
        // aynısını üçüncü kez üretmek olurdu — boş dize burada, `"baseline"`
        // motorda, ve ikisi bir gün yine ayrışır.
        var relative = Scenarios.IsBaseline(_scenario)
            ? _profile.Config.Baseline
            : _profile.Config.Scenarios.TryGetValue(_scenario, out var senaryoYolu)
                ? senaryoYolu
                : null;

        if (relative is null)
        {
            // Buraya düşen senaryo TANINIYOR ve config yüzeyine ait — ama bu
            // profil onu taşımıyor. Yukarıdaki hatadan farklı bir durum ve
            // farklı bir cümle: eksik olan senaryo değil, profilin o senaryoyu
            // karşılayan dosyası.
            var bilinen = string.Join(", ", _profile.Config.Scenarios.Keys.Order(StringComparer.Ordinal));

            return Task.FromResult(DeviceCommandResult.Failed(
                DeviceFailureKind.CommandRejected,
                $"'{_profile.Id}' profilinde '{_scenario}' senaryosu tanımlı değil. Bu profilde olanlar: {bilinen}"));
        }

        var path = Path.Combine(_profileDirectory, relative);

        return Task.FromResult(File.Exists(path)
            ? DeviceCommandResult.Succeeded(File.ReadAllText(path))
            : DeviceCommandResult.Failed(
                DeviceFailureKind.CommandRejected, $"Config dosyası yok: {path}"));
    }
}

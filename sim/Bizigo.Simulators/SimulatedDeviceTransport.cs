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
            return Task.FromResult(new DeviceCommandResult(
                false,
                string.Empty,
                $"'{_profile.Id}' profilinin config yüzeyi yok."));
        }

        var relative = _scenario.Length == 0
            ? _profile.Config.Baseline
            : _profile.Config.Scenarios.TryGetValue(_scenario, out var senaryoYolu)
                ? senaryoYolu
                : null;

        if (relative is null)
        {
            // Bilinmeyen senaryo SESSİZCE baseline'a düşmüyor. Düşseydi, adı
            // yanlış yazılmış bir senaryo testi yeşil bırakır ve "fark yok"
            // sonucu doğru sanılırdı.
            var bilinen = string.Join(", ", _profile.Config.Scenarios.Keys.Order(StringComparer.Ordinal));

            return Task.FromResult(new DeviceCommandResult(
                false,
                string.Empty,
                $"'{_profile.Id}' profilinde '{_scenario}' senaryosu yok. Bilinenler: {bilinen}"));
        }

        var path = Path.Combine(_profileDirectory, relative);

        return Task.FromResult(File.Exists(path)
            ? new DeviceCommandResult(true, File.ReadAllText(path), string.Empty)
            : new DeviceCommandResult(false, string.Empty, $"Config dosyası yok: {path}"));
    }
}

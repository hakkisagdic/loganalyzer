namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <c>sim.*</c> araçlarının ortak zemini: filo nerede, profiller nerede, hangi
/// collector'a basılıyor.
///
/// <para>
/// <b>Neden tek bir tip.</b> Yedi aracın her biri depo kökünü ve profil dizinini
/// kendi başına çözseydi, yedi ayrı <i>"kök neresi"</i> cevabı doğardı ve
/// ayrıştıkları gün belirti şu olurdu: bir araç filoyu görüyor, diğeri
/// <i>"filo dosyası yok"</i> diyor. Bu depoda aynı sınıfın kaydı var —
/// <c>Bizigo.Api</c>'nin katalog yollarını CWD'den çözmesi.
/// </para>
///
/// <para>
/// <b>Filo ve profiller her çağrıda YENİDEN okunuyor, önbelleğe alınmıyor.</b>
/// Karar ölçülmüş bir gerekçeyle: bu araçlar bir geliştirme oturumunda koşuyor
/// ve o oturumda profil dosyaları <b>elle düzenleniyor</b>. Önbellek, düzeltilmiş
/// bir profili görmeyen bir <c>sim.fleet.list</c> demek olurdu — yani ekranda
/// eski gerçek. Maliyeti birkaç küçük YAML dosyası; ölçüldü, kayda değer değil.
/// </para>
/// </summary>
public sealed class SimulatorMcpContext
{
    /// <summary>
    /// Collector'ın varsayılan adresi. <c>Program.cs</c>'in CLI varsayılanıyla
    /// <b>aynı</b> — iki varsayılan, bir gün ayrışacak iki varsayılandır.
    /// </summary>
    public const string DefaultCollectorHost = "127.0.0.1";

    /// <param name="repositoryRoot">Katalog ve örnek yollarının çözüleceği kök.</param>
    /// <param name="state">Durum katmanı.</param>
    /// <param name="collectorHost">Syslog basımının hedefi.</param>
    public SimulatorMcpContext(
        string repositoryRoot,
        SimulatorStateStore state,
        string? collectorHost = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(state);

        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        State = state;
        CollectorHost = string.IsNullOrWhiteSpace(collectorHost) ? DefaultCollectorHost : collectorHost;
    }

    /// <summary>Depo kökü.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Durum katmanı.</summary>
    public SimulatorStateStore State { get; }

    /// <summary>Syslog basımının hedef adresi.</summary>
    public string CollectorHost { get; }

    /// <summary>Profil kataloğu.</summary>
    public string ProfileDirectory => Path.Combine(RepositoryRoot, "catalog", "simulators");

    /// <summary>
    /// Filoyu ve profillerini okur.
    ///
    /// <para>
    /// <b>Doğrulama hataları yutulmuyor</b>, çağırana veriliyor: bozuk bir filo
    /// ile <i>"filo boş"</i> aynı şey değil ve <c>sim.fleet.list</c> ikisini
    /// ayrı ayrı raporluyor. Yutulsaydı boş bir liste <i>"cihaz yok"</i> diye
    /// okunurdu.
    /// </para>
    /// </summary>
    public FleetSnapshot LoadFleet()
    {
        if (!Directory.Exists(ProfileDirectory))
        {
            return new FleetSnapshot(
                [],
                [$"Profil kataloğu yok: {ProfileDirectory}. Depo kökü yanlış olabilir ({RepositoryRoot})."]);
        }

        var loaded = FleetStore.Load(ProfileDirectory, RepositoryRoot);
        var profiles = FleetStore.Profiles(loaded.Fleet, ProfileDirectory, RepositoryRoot);

        return new FleetSnapshot(profiles, loaded.Errors);
    }

    /// <summary>
    /// Tek bir profili kimliğiyle bulur. Bulunamazsa <see langword="null"/> —
    /// çağıran <c>not_found</c> üretiyor.
    /// </summary>
    public SimulatorProfile? FindProfile(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return LoadFleet().Profiles
            .FirstOrDefault(p => string.Equals(p.Id, deviceId.Trim(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Depo kökünü bulur — <c>Bizigo.sln</c>'i arayarak.
    ///
    /// <para>
    /// <c>Program.cs</c>'teki arayışın <b>aynısı</b> ve bilerek aynı: iki ayrı
    /// "kök neresi" cevabı, ayrıştıkları gün CLI ile MCP'nin farklı filolar
    /// görmesi demek olurdu.
    /// </para>
    /// </summary>
    public static string? DiscoverRepositoryRoot(string? startingAt = null)
    {
        var directory = new DirectoryInfo(startingAt ?? AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}

/// <param name="Profiles">Filodaki cihazların profilleri, dosyadaki sırayla.</param>
/// <param name="Errors">
/// Filo doğrulama bulguları. <b>Boş değilse filo güvenilmez</b> ve araçlar bunu
/// yanıtta <b>taşıyor</b> — yutulan bir doğrulama hatası, yarım bir kapsamı
/// tam gibi gösterirdi.
/// </param>
public sealed record FleetSnapshot(
    IReadOnlyList<SimulatorProfile> Profiles,
    IReadOnlyList<string> Errors);

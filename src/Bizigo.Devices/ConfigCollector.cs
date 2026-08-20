namespace Bizigo.Devices;

/// <param name="Lines">Normalize edilmiş, gizli değerleri maskelenmiş config.</param>
public sealed record ConfigCapture(bool Ok, IReadOnlyList<ConfigLine> Lines, string Error)
{
    public static ConfigCapture Failed(string error) => new(false, [], error);
}

/// <summary>
/// Bir vendor'dan config çeken toplayıcı (T26).
///
/// <para>
/// <b>Yüzey iki somut vendor yazıldıktan sonra çıkarıldı</b> — ticket'ın uyarısı
/// buydu: SSH, REST ve SNMP'yi tek soyutlamaya baştan sıkıştırmak erken
/// genelleme olurdu. Ortaya çıkan yüzey şaşırtıcı derecede küçük: "hangi
/// komutlar" ve "çıktı nasıl normalize edilir". Üçü de SSH konuştuğu için
/// taşıma ortak kaldı; REST konuşan bir vendor geldiğinde
/// <see cref="IDeviceTransport"/>'un ikinci bir uygulaması yazılacak, bu arayüz
/// değişmeyecek.
/// </para>
/// </summary>
public interface IConfigCollector
{
    /// <summary>F1 kataloğundaki parser kimliğiyle aynı: <c>fortinet.fortigate</c>.</summary>
    string Vendor { get; }

    /// <summary>
    /// Cihazda koşturulacak komutlar. Hepsi <b>okuma</b> — bu ürün config
    /// değiştirmiyor ve arayüzde yazma diye bir şey yok.
    /// </summary>
    IReadOnlyList<string> Commands { get; }
}

/// <summary>
/// FortiGate.
///
/// <para>
/// <c>show</c> (<c>show full-configuration</c> değil) bilinçli: <c>show</c>
/// yalnızca <b>varsayılandan sapan</b> ayarları basıyor. Tam config her firmware
/// yükseltmesinde yüzlerce varsayılan satırı değiştirir ve fark raporunu
/// kullanılamaz hâle getirirdi — oysa RCA'nın aradığı şey operatörün ne
/// değiştirdiği.
/// </para>
/// </summary>
public sealed class FortiGateCollector : IConfigCollector
{
    public string Vendor => ConfigNormalizer.FortiGate;

    public IReadOnlyList<string> Commands { get; } =
    [
        // Sayfalama kapatılmadan çıktı "--More--" ile kesiliyor ve config
        // yarım geliyor; yarım config, silinmiş yüzlerce satır gibi görünür.
        "config system console\nset output standard\nend",
        "show",
    ];
}

/// <summary>
/// Cisco ASA.
///
/// <para>
/// <c>more system:running-config</c>, <c>show running-config</c>'e tercih
/// edildi: ikincisi terminal genişliğine göre satır kaydırıyor ve aynı config
/// farklı oturumlarda farklı satırlara bölünebiliyor — yani sahte fark.
/// </para>
/// </summary>
public sealed class CiscoAsaCollector : IConfigCollector
{
    public string Vendor => ConfigNormalizer.CiscoAsa;

    public IReadOnlyList<string> Commands { get; } =
    [
        "terminal pager 0",
        "more system:running-config",
    ];
}

/// <summary>
/// MikroTik RouterOS.
///
/// <para>
/// <c>/export terse</c>: her ayarı tek satıra basıyor. Varsayılan çok satırlı
/// export'ta bir ayarın satır sonu konumu sürüme göre değişiyor ve bu da sahte
/// fark üretiyor.
/// </para>
/// </summary>
public sealed class MikroTikCollector : IConfigCollector
{
    public string Vendor => ConfigNormalizer.MikroTik;

    public IReadOnlyList<string> Commands { get; } = ["/export terse"];
}

/// <summary>
/// Toplayıcıları vendor'a göre çözer ve çekimi yürütür.
///
/// <para>
/// <b>Eşzamanlılık burada sınırlanıyor.</b> Yüzlerce cihazın hepsine aynı anda
/// SSH açmak iki tarafı da yorar: bizim tarafta soket ve iş parçacığı, cihaz
/// tarafında yönetim CPU'su — ve izlediğimiz cihazı yormak, izlemenin kendisini
/// bir arıza sebebine çevirir (ticket kabul kriteri: "çekim maliyeti sınırlı").
/// </para>
/// </summary>
public sealed class DeviceConfigService(
    IDeviceTransport transport,
    IEnumerable<IConfigCollector> collectors,
    int maxConcurrency = 8)
{
    private readonly Dictionary<string, IConfigCollector> _collectors =
        collectors.ToDictionary(c => c.Vendor, StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(Math.Max(1, maxConcurrency));

    public IReadOnlyCollection<string> SupportedVendors => _collectors.Keys;

    public async Task<ConfigCapture> CaptureAsync(
        DeviceTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_collectors.TryGetValue(target.Vendor, out var collector))
        {
            return ConfigCapture.Failed(
                $"'{target.Vendor}' için toplayıcı yok. Desteklenenler: {string.Join(", ", _collectors.Keys)}.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var result = await transport.RunAsync(target, collector.Commands, cancellationToken);

            // Cihaza ulaşılamaması bir istisna DEĞİL, bir sonuç: çekim döngüsü
            // tek bir erişilemez cihaz yüzünden ölmemeli (ticket kabul kriteri).
            return result.Ok
                ? new ConfigCapture(true, ConfigNormalizer.Normalize(target.Vendor, result.Output), string.Empty)
                : ConfigCapture.Failed(result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }
}

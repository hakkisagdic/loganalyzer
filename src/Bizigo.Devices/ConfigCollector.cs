namespace Bizigo.Devices;

/// <param name="Lines">Normalize edilmiş, gizli değerleri maskelenmiş config.</param>
/// <param name="Failure">
/// Başarısızlığın türü — <see cref="DeviceFailureKind.None"/> ancak ve ancak
/// <paramref name="Ok"/> doğruyken.
///
/// <para>
/// <b>S08'de eklendi ve gerekçesi ölçülmüş bir kayıp:</b> S06 taşıma katmanında
/// "komut reddedildi" ile "cihaza ulaşılamadı"yı iki ayrı değere ayırmıştı, ama
/// <see cref="DeviceConfigService"/> çıkışında ikisi yeniden <b>tek bir
/// metne</b> düşüyordu. Yani ayrımı yapmak için harcanan iş servis kapısında
/// geri alınıyordu ve ekrana giden şey yine bir cümleydi.
/// </para>
///
/// <para>
/// Teşhisin metinden okunması iki yönden kırılgan: cümle Türkçe ve bir gün
/// düzeltilecek, ve okuyan tarafın hangi kelimeyi arayacağı hiçbir yerde
/// yazılı değil.
/// </para>
/// </param>
public sealed record ConfigCapture(
    bool Ok,
    IReadOnlyList<ConfigLine> Lines,
    string Error,
    DeviceFailureKind Failure)
{
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="DeviceFailureKind.None"/> verilirse — başarısızlığın türsüz
    /// olması mümkün olmamalı.
    /// </exception>
    public static ConfigCapture Failed(DeviceFailureKind kind, string error)
    {
        if (kind == DeviceFailureKind.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), "Başarısız çekim için bir başarısızlık türü zorunlu.");
        }

        return new ConfigCapture(false, [], error, kind);
    }
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
    ///
    /// <para>
    /// <b>Her eleman KENDİ OTURUMUNDA koşuyor ve bu yüzden kendi kendine
    /// yetmek zorunda</b> (S08). <see cref="SshDeviceTransport"/> her eleman
    /// için ayrı bir exec kanalı açıyor; gerçek bir cihazda da her exec kanalı
    /// ayrı bir oturum. Yani bir elemanda yazılan <b>oturum ayarı</b> —
    /// sayfalamayı kapatmak gibi — bir sonrakine <b>taşınmıyor</b>.
    /// </para>
    ///
    /// <para>
    /// Bu sözleşme S08'e kadar yazılı değildi ve ihlali sessizdi: sayfalamayı
    /// ayrı bir elemanda kapatan bir toplayıcı, config'i <b>yarım</b> alıyor ve
    /// hiçbir şey şikâyet etmiyordu (<c>Ok=true</c>, <c>Error</c> boş). Çok
    /// satırlı bir eleman <b>tek oturumda</b> koşuyor — FortiGate toplayıcısı
    /// zaten öyle yazılmıştı, yalnızca <c>show</c> dışarıda kalmıştı.
    /// </para>
    ///
    /// <para>
    /// Birden çok eleman hâlâ meşru: birbirinden <b>bağımsız</b> okumalar için.
    /// Ölçüt basit — ikinci eleman, birincinin yazdığı bir ayara dayanıyorsa
    /// ikisi tek eleman olmalı.
    /// </para>
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

    /// <summary>
    /// <b>Tek eleman</b> — sayfalama kapatma ile <c>show</c> aynı oturumda
    /// (S08).
    ///
    /// <para>
    /// İkisi ayrı elemandayken sayfalama kapatma <b>hiçbir işe yaramıyordu</b>:
    /// her eleman ayrı bir exec kanalı, yani ayrı bir oturum, ve oturum ayarı
    /// kanalla birlikte ölüyor. Sonuç <c>--More--</c> ile kesilmiş yarım bir
    /// config'ti — <c>Ok=true</c>, <c>Error</c> boş — ve fark motoru onu
    /// <b>silinmiş yüzlerce satır</b> diye okuyordu.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Commands { get; } =
    [
        "config system console\nset output standard\nend\nshow",
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

    /// <summary>
    /// <b>Tek eleman</b> — <c>terminal pager 0</c> ile okuma aynı oturumda
    /// (S08); gerekçesi <see cref="FortiGateCollector.Commands"/> ile aynı.
    /// </summary>
    public IReadOnlyList<string> Commands { get; } =
    [
        "terminal pager 0\nmore system:running-config",
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
            // Cihaza HİÇ bağlanılmadı: arıza cihazda değil yapılandırmada.
            return ConfigCapture.Failed(
                DeviceFailureKind.Unsupported,
                $"'{target.Vendor}' için toplayıcı yok. Desteklenenler: {string.Join(", ", _collectors.Keys)}.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var result = await transport.RunAsync(target, collector.Commands, cancellationToken);

            // Cihaza ulaşılamaması bir istisna DEĞİL, bir sonuç: çekim döngüsü
            // tek bir erişilemez cihaz yüzünden ölmemeli (ticket kabul kriteri).
            // Başarısızlığın TÜRÜ taşımadan geçiyor: servis kapısı onu yeniden
            // yorumlamıyor, yalnızca aktarıyor. Yorumlasaydı iki yerde iki
            // farklı sınıflandırma doğar ve ayrıştıkları gün hangisinin doğru
            // olduğunu söyleyen hiçbir şey olmazdı (§9).
            return result.Ok
                ? new ConfigCapture(
                    true,
                    ConfigNormalizer.Normalize(target.Vendor, result.Output),
                    string.Empty,
                    DeviceFailureKind.None)
                : ConfigCapture.Failed(result.Failure, result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }
}

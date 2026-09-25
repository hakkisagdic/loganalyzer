using System.Globalization;
using System.Text.Json;
using Bizigo.Capacity;
using Bizigo.Simulators;

// ---------------------------------------------------------------------------
// Cihaz simülatörü — komut satırı girişi (FS · S02 · S07).
//
// Proje hem kütüphane hem çalıştırılabilir: N1 sahte taşıyıcısı birim
// testlerinden referansla kullanılıyor, syslog basıcısı ise buradan
// koşturuluyor. İkiye bölmek, profil okuyucusunun iki yerden referanslanması
// demekti ve §9'un yasakladığı ikinci kopyaya davetiye çıkarırdı.
//
//   dotnet run --project sim/Bizigo.Simulators -- \
//       --profile fw-ankara-01 --count 200
//
//   dotnet run --project sim/Bizigo.Simulators -- \
//       --profile fw-ankara-01 --webhook github \
//       --url http://127.0.0.1:8080/v1/changes/webhooks/ci --secret anahtar --times 2
//
// Varsayılan hedef `localhost` çünkü collector portu makineye açık. Container
// içinden koşarken `--host otel-collector`.
// ---------------------------------------------------------------------------

var profileId = Arg("--profile");
var host = Arg("--host") ?? "127.0.0.1";
var countText = Arg("--count");
var repositoryRoot = Arg("--repo") ?? FindRepositoryRoot();
var paceName = Arg("--pace");

var count = 100;
if (countText is not null
    && (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count <= 0))
{
    count = 0;
}

if (profileId is null || count <= 0)
{
    Console.Error.WriteLine("""
        Kullanım:
          --profile <id>     catalog/simulators/<id>.yaml   (zorunlu)
          --count <n>        basılacak satır sayısı         (varsayılan 100)
          --host <adres>     collector adresi               (varsayılan 127.0.0.1)
          --repo <yol>       depo kökü                      (varsayılan: otomatik)

        Taşıma ve hız PROFİLDEN geliyor; komut satırından ezilmiyor. Bir cihazın
        hangi hızda ve hangi taşımayla bastığı o cihazın özelliği, koşumun değil.

        Kapasite modu:
          --pace fixed|ramp|burst|soak|max
          --duration <saniye> --run-id <kimlik> --manifest <json-yolu>
          --eps <n>                         fixed/soak
          --from-eps <n> --to-eps <n>       ramp
          --base-eps <n> --peak-eps <n>     burst
          --peak-at <s> --peak-duration <s> burst
          --connections <n> --batch <n> --payload raw|tagged
          --generator-location same|separate|unknown
        """);

    return 2;
}

var results = SimulatorProfileStore.LoadAll(
    Path.Combine(repositoryRoot, "catalog", "simulators"),
    repositoryRoot);

var match = results.FirstOrDefault(r => r.Profile.Id == profileId);

if (match is null)
{
    Console.Error.WriteLine(
        $"'{profileId}' profili yok. Bilinenler: " +
        string.Join(", ", results.Select(r => r.Profile.Id).Order(StringComparer.Ordinal)));

    return 3;
}

// Doğrulama SESSİZCE atlanmıyor: bozuk bir profille basmak, var olmayan bir
// örnek dosyayı ya da yanlış kapsam grubunu sessizce ölçmeye çalışmaktır.
if (match.Errors.Count > 0)
{
    Console.Error.WriteLine($"'{profileId}' profili geçersiz:");

    foreach (var error in match.Errors)
    {
        Console.Error.WriteLine("  " + error);
    }

    return 4;
}

var profile = match.Profile;

// ------------------------------------------------------- webhook modu (S07)

if (Arg("--webhook") is { } provider)
{
    var url = Arg("--url");
    var secret = Arg("--secret");

    // Teslimat kimliği ÇAĞIRANDAN geliyor ve verilmezse profilden türetiliyor —
    // rastgele üretilmiyor. Rastgele olsaydı `--times 2` iki FARKLI teslimat
    // gönderirdi ve idempotans sınaması hiçbir zaman kurulamazdı; üstelik
    // "iki kayıt oluştu" sonucu doğru görünürdü.
    var deliveryId = Arg("--delivery") ?? $"{profile.Id}-1";

    if (url is null || secret is null || !WebhookProviders.All.Contains(provider, StringComparer.Ordinal))
    {
        Console.Error.WriteLine($"""
            Webhook modu:
              --webhook <sağlayıcı>  {string.Join(" | ", WebhookProviders.All)}
              --url <adres>          POST /v1/changes/webhooks/<uç>     (zorunlu)
              --secret <anahtar>     ucun paylaşılan gizli anahtarı     (zorunlu)
              --delivery <kimlik>    teslimat kimliği (varsayılan: <profil>-1)
              --times <n>            aynı teslimatı kaç kez göndersin   (varsayılan 1)

            Aynı teslimat iki kez gönderilince alıcı TEK kayıt oluşturmalı:
            ikincisi 200 ve `duplicate: true` dönüyor, 201 değil.
            """);

        return 5;
    }

    var times = int.TryParse(Arg("--times"), out var t) && t > 0 ? t : 1;

    // Zaman damgası sabit bir andan geliyor, `UtcNow`'dan değil: aynı istek
    // aynı baytları üretmezse gövde hash'ine düşen idempotans yolu her turda
    // farklı bir anahtar üretir.
    var delivery = WebhookDeliveryFactory.Create(
        WebhookDeliveryRequest.FromProfile(
            profile,
            provider,
            secret,
            deliveryId,
            new DateTimeOffset(2026, 8, 18, 9, 19, 47, TimeSpan.Zero)));

    Console.WriteLine(
        $"· {provider} teslimatı: hedef {profile.Hostname}, kimlik {deliveryId}, " +
        $"{delivery.Body.Length} bayt, {times} kez");

    using var http = new HttpClient();

    var sent = await WebhookSender.SendAsync(
        http, new Uri(url), delivery, times, CancellationToken.None);

    foreach (var (result, index) in sent.Select((r, i) => (r, i + 1)))
    {
        Console.WriteLine($"· {index}. gönderim → HTTP {result.Status} {result.Body}");
    }

    // Tek satırlık ölçüt: ikinci gönderim 201 dönerse idempotans kırık.
    var created = sent.Count(r => r.Status == 201);

    Console.WriteLine(
        created == 1 || times == 1
            ? $"· idempotans: {sent.Count} gönderim, {created} kayıt oluştu."
            : $"· UYARI: {sent.Count} gönderim {created} kayıt oluşturdu — beklenen 1.");

    return created <= 1 ? 0 : 6;
}

// -------------------------------------------------------- syslog modu (S02)

if (paceName is not null)
{
    var runId = Arg("--run-id");
    var manifestPath = Arg("--manifest");
    var duration = PositiveDouble("--duration");
    var pace = BuildPace(paceName, duration);
    var connections = PositiveInt("--connections", 1);
    var batch = PositiveInt("--batch", 1);
    var payload = Arg("--payload") switch
    {
        null or "tagged" => CapacityPayloadMode.Tagged,
        "raw" => CapacityPayloadMode.Raw,
        var value => throw new ArgumentException($"Bilinmeyen --payload değeri: '{value}'."),
    };
    bool? sameHost = Arg("--generator-location") switch
    {
        "same" => true,
        "separate" => false,
        "unknown" => null,
        var value => throw new ArgumentException(
            $"--generator-location same|separate|unknown olmalı; gelen: '{value ?? "—"}'."),
    };

    if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(manifestPath))
    {
        Console.Error.WriteLine(
            "Kapasite modu --run-id ve --manifest ister; sayı hükümsüz ve kayıtsız üretilemez.");
        return 7;
    }

    var options = new CapacityEmitOptions
    {
        RunId = runId,
        Pace = pace,
        MaxLines = countText is null ? null : count,
        Connections = connections,
        BatchSize = batch,
        PayloadMode = payload,
        GeneratorOnSameHost = sameHost,
        Port = Arg("--port") is null ? null : PositiveInt("--port", 5140),
    };

    if (Arg("--transport") is { } transport)
    {
        if (transport is not ("tcp" or "udp")) throw new ArgumentException("--transport must be tcp or udp.");
        profile.Syslog!.Transport = transport;
    }

    Console.WriteLine(
        $"· kapasite {pace.Name}: run={runId}, {connections} bağlantı, batch={batch}, {payload}");

    var result = await CapacityEmitter.EmitAsync(
        profile, repositoryRoot, host, options, CancellationToken.None);

    var fullManifestPath = Path.GetFullPath(manifestPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullManifestPath)!);
    await File.WriteAllTextAsync(
        fullManifestPath,
        JsonSerializer.Serialize(result.Manifest, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine("· " + result.Manifest.Describe());
    Console.WriteLine($"· manifest: {fullManifestPath} ({result.Manifest.Digests.Count} sha256)");

    return result.Manifest.Attainment.Verdict == GeneratorVerdict.GeneratorLimited ? 8 : 0;
}

Console.WriteLine(
    $"· {profile.Id} ({profile.Vendor}/{profile.Product}) → {host}, " +
    $"{profile.Syslog?.Transport}, kodlama {profile.Encoding}, {count} satır");

var emit = await SyslogEmitter.EmitAsync(profile, repositoryRoot, host, count);

Console.WriteLine(
    $"· basıldı: {emit.Lines} satır, {emit.Bytes} bayt, {emit.Elapsed.TotalSeconds:F1} sn");

// Basmak "ulaştı" demek DEĞİL. TCP'ye yazmak yalnızca collector'ın soketi
// aldığını söylüyor; satırın WAL'a, arşive ve ClickHouse'a ulaştığını sorgulayan
// başka bir adım (S02'nin kabul ölçütü) yapıyor.
Console.WriteLine("· not: bu sayı TELE giden satır; ClickHouse'a ulaştığını ayrıca doğrulayın.");

return 0;

static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int PositiveInt(string name, int fallback)
{
    var value = Arg(name);
    if (value is null)
    {
        return fallback;
    }

    return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(name, value, "Pozitif tamsayı bekleniyor.");
}

static double PositiveDouble(string name)
{
    var value = Arg(name);
    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && double.IsFinite(parsed)
        && parsed > 0.0
            ? parsed
            : throw new ArgumentOutOfRangeException(name, value, "Pozitif sonlu sayı bekleniyor.");
}

static PaceProfile BuildPace(string name, double durationSeconds)
{
    var duration = TimeSpan.FromSeconds(durationSeconds);

    return name switch
    {
        "fixed" => new PaceProfile.Fixed(PositiveDouble("--eps"), duration),
        "soak" => new PaceProfile.Soak(PositiveDouble("--eps"), duration),
        "ramp" => new PaceProfile.Ramp(
            PositiveDouble("--from-eps"), PositiveDouble("--to-eps"), duration),
        "burst" => new PaceProfile.Burst(
            PositiveDouble("--base-eps"),
            PositiveDouble("--peak-eps"),
            TimeSpan.FromSeconds(PositiveDouble("--peak-at")),
            TimeSpan.FromSeconds(PositiveDouble("--peak-duration")),
            duration),
        "max" => new PaceProfile.Max(duration),
        _ => throw new ArgumentException($"Bilinmeyen --pace değeri: '{name}'.", nameof(name)),
    };
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName
        ?? throw new InvalidOperationException("Depo kökü bulunamadı: Bizigo.sln hiçbir üst dizinde yok.");
}

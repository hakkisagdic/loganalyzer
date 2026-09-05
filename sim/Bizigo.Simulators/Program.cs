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
var countText = Arg("--count") ?? "100";
var repositoryRoot = Arg("--repo") ?? FindRepositoryRoot();

if (profileId is null || !int.TryParse(countText, out var count) || count <= 0)
{
    Console.Error.WriteLine("""
        Kullanım:
          --profile <id>     catalog/simulators/<id>.yaml   (zorunlu)
          --count <n>        basılacak satır sayısı         (varsayılan 100)
          --host <adres>     collector adresi               (varsayılan 127.0.0.1)
          --repo <yol>       depo kökü                      (varsayılan: otomatik)

        Taşıma ve hız PROFİLDEN geliyor; komut satırından ezilmiyor. Bir cihazın
        hangi hızda ve hangi taşımayla bastığı o cihazın özelliği, koşumun değil.
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

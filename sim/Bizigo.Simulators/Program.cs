using Bizigo.Simulators;

// ---------------------------------------------------------------------------
// Cihaz simülatörü — komut satırı girişi (FS · S02).
//
// Proje hem kütüphane hem çalıştırılabilir: N1 sahte taşıyıcısı birim
// testlerinden referansla kullanılıyor, syslog basıcısı ise buradan
// koşturuluyor. İkiye bölmek, profil okuyucusunun iki yerden referanslanması
// demekti ve §9'un yasakladığı ikinci kopyaya davetiye çıkarırdı.
//
//   dotnet run --project sim/Bizigo.Simulators -- \
//       --profile fw-ankara-01 --count 200
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

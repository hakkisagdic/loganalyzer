using System.Reflection;

namespace Bizigo.Api;

/// <summary>
/// OpenAPI belgesinin derleme zamanında üretilmesi (T14).
///
/// <para>
/// <c>dotnet-getdocument</c> belgeyi statik olarak okumuyor: derlenmiş
/// derlemeyi yükleyip <c>Main</c>'i <b>gerçekten çalıştırıyor</b>, uygulamayı
/// ayakta yakalıyor, belgeyi alıp süreci durduruyor. Dolayısıyla açılışta
/// yapılan her şey — veritabanı göçleri, katalog yüklemeleri — o sırada da
/// yapılmaya çalışılıyor. Bu sınıf o koşumu ayakta tutan üç ayarı topluyor.
/// </para>
/// </summary>
public static class DocumentGeneration
{
    /// <summary>
    /// Belge üretimi sırasında mıyız.
    ///
    /// <para>
    /// Ayrım bir ortam değişkeniyle değil <b>giriş derlemesinin adıyla</b>
    /// yapılıyor: bir bayrak yanlışlıkla üretime taşınabilir ve göçleri sessizce
    /// atlayan bir API bırakırdı. Araç adı değişirse belge üretimi kırmızı yanar
    /// — hatanın doğru yönü bu.
    /// </para>
    /// </summary>
    public static bool IsActive { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    /// <summary>
    /// Belge üretimine uygun bir uygulama kurucusu döndürüyor; normal koşumda
    /// varsayılan kurucuyu olduğu gibi bırakıyor.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        if (!IsActive)
        {
            return WebApplication.CreateBuilder(args);
        }

        // Araç MSBuild `Exec`'i üzerinden PROJE dizininde koşuyor, uygulama ise
        // depo kökünden koşacak şekilde yapılandırılmış: `catalog/masks/...`,
        // `catalog/patterns/...`, `db/clickhouse` hepsi göreli. Kökü bulup
        // çalışma dizinini oraya almak, aynı yolları ortam değişkenleriyle tek
        // tek ezmekten hem kısa hem de yeni bir göreli yol eklendiğinde
        // kendiliğinden doğru.
        Directory.SetCurrentDirectory(RepositoryRoot());

        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,

            // Çalışma dizini depo köküne alındı; `appsettings.json` ise derleme
            // çıktısının yanında duruyor. İçerik kökünü elle göstermezsek
            // yapılandırma hiç okunmaz ve uygulama
            // "ConnectionStrings:ControlPlane tanımlı değil" diyerek düşer.
            ContentRootPath = AppContext.BaseDirectory,

            // Ortam adı verilmezse `Production` varsayılıyor ve WAL dizini
            // `/var/lib/bizigo` oluyor — bir geliştirici makinesinde ya da CI
            // çalıştırıcısında yazılamaz. Belge üretimi bir geliştirme adımı.
            EnvironmentName = Environments.Development,
        });
    }

    /// <summary><c>Bizigo.sln</c>'i barındıran dizin.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Depo kökü bulunamadı: Bizigo.sln hiçbir üst dizinde yok.");
    }
}

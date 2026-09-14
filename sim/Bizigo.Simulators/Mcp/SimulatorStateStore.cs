using System.Text.Json;

namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <see cref="SimulatorState"/>'i diske yazan ve okuyan tek yer.
///
/// <para>
/// <b>Üç karar burada ve üçü de bu deponun ölçülmüş derslerinden geliyor.</b>
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Yazma atomik</b> — geçici dosya + <c>rename</c>. Yarım yazılmış bir durum
/// dosyası, okuyan için <i>"durum yok"</i> ile <b>ayırt edilemez</b> ve ikisi
/// zıt iş emri veriyor: biri <i>"senaryo uygulanmamış, uygula"</i> diyor, diğeri
/// <i>"hiç istenmemiş, dokunma"</i>. Doğrudan üstüne yazmak, süreç o sırada
/// ölürse tam olarak o dosyayı bırakıyor.
/// </item>
/// <item>
/// <b>Oku-değiştir-yaz kilit altında.</b> İki stdio oturumu aynı anda farklı
/// cihazlara dokunabiliyor. Kilitsiz hâlde ikisi de dosyanın <i>eski</i> hâlini
/// okur ve ikincisi birincinin değişikliğini <b>sessizce siler</b> — bu depoda
/// ölçülmüş bir olayın şekli: <i>"CSV'de aynı kaynak iki kez geçince son satır
/// sessizce kazanıyordu, ve kazanan şey owner_group'tu."</i>
/// </item>
/// <item>
/// <b>Dosya <c>artifacts/</c> altında.</b> <c>catalog/</c> altına düşseydi
/// üretilen bir dosya versiyonlanır ve <c>compile --check</c> sınıfı kapılara
/// yakalanırdı. <b>Alt dizin</b> kullanılmasının ayrı bir sebebi var:
/// <c>UseArtifactsOutput</c> bugün kapalı ama biri onu açarsa SDK
/// <c>artifacts/</c> köküne derleme çıktısı yazar ve <c>dotnet clean</c> durum
/// dosyasını <b>sessizce</b> siler.
/// </item>
/// </list>
///
/// <para>
/// <b>Eşzamanlılık kararı, ve düz "son yazan kazanır"dan neden dar.</b> Kilit
/// bütün oku-değiştir-yaz turunu kapsıyor, yani <b>farklı cihazlara</b> yapılan
/// iki değişikliğin ikisi de yaşıyor. Yalnızca <b>aynı cihaz</b> aynı anda
/// ayarlanırsa son yazan kazanıyor — ve orada bu anlamlı bir cevap: bir cihazın
/// iki senaryosu olamaz. Kaybolan şey bir başkasının işi değil, aynı sorunun
/// eski cevabı.
/// </para>
///
/// <para>
/// <b>Kilidin kapsamadığı şey, dürüstlük gereği yazılı:</b> kilit
/// <b>tavsiye niteliğinde</b> ve yalnızca bu tipten geçen yazarları
/// bağlıyor. Dosyayı elle düzenleyen biri kilidi görmez. Bu kabul edilebilir,
/// çünkü dosyanın tüketicisi tek bir araç kümesi; ama <c>rename</c> sayesinde
/// elle düzenleyen bile <b>yarım</b> bir dosya görmüyor.
/// </para>
/// </summary>
public sealed class SimulatorStateStore
{
    /// <summary>
    /// Varsayılan yol, depo köküne göre. Yapılandırılabilir olması şart:
    /// testler birbirinin durumuna yazamamalı, yoksa paralel koşumda
    /// <b>testler birbirini bozar</b> ve düşüş kodda aranır.
    /// </summary>
    public const string DefaultRelativePath = "artifacts/bizigo-sim/state.json";

    /// <summary>
    /// Kilidi almak için beklenecek en uzun süre.
    ///
    /// <para>
    /// <b>Duvar saati burada denklemde ve olması gerekiyor</b> (§6): ölçülen şey
    /// <i>"kilit serbest kaldı mı"</i>, ve bir üst sınır olmadan çöken bir süreç
    /// kalan bütün çağrıları süresiz askıya alırdı. Süre <b>sonucu</b>
    /// belirlemiyor, yalnızca sonsuz beklemeyi kesiyor: kilidi alan tur
    /// milisaniyeler sürüyor (tek küçük dosya), yani bu sınıra dayanmak bir
    /// arıza işareti ve mesajı bunu söylüyor.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider clock;

    /// <param name="path">Durum dosyasının tam yolu.</param>
    /// <param name="clock">
    /// Zaman kaynağı. Enjekte ediliyor çünkü niyet/etik damgaları testin
    /// iddia ettiği şey; <c>UtcNow</c>'a bağlı bir test duvar saatini ölçerdi.
    /// </param>
    public SimulatorStateStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = System.IO.Path.GetFullPath(path);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Durum dosyasının tam yolu.</summary>
    public string Path { get; }

    /// <summary>Şimdiki an — araçlar damgayı buradan alıyor.</summary>
    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>
    /// Durumu okur. Dosya yoksa <see cref="SimulatorState.Empty"/>.
    ///
    /// <para>
    /// <b>Bozuk bir dosya sessizce boş sayılmıyor</b>, fırlatıyor. Boş saymak
    /// <i>"hiç senaryo istenmemiş"</i> demek olurdu ve bu, istenmiş bir
    /// senaryonun kaybolduğu hâlle aynı çıktıyı verirdi — okuyan ikincisini
    /// hiç düşünmezdi.
    /// </para>
    /// </summary>
    public SimulatorState Read()
    {
        if (!File.Exists(Path))
        {
            return SimulatorState.Empty;
        }

        return Parse(File.ReadAllText(Path));
    }

    /// <summary>
    /// Durumu <b>kilit altında</b> okuyup değiştirip yazar ve yeni hâli döner.
    /// </summary>
    /// <param name="change">
    /// Değişiklik. <b>Saf olmalı</b>: kilit altında çağrılıyor ve içinden başka
    /// bir <see cref="Mutate"/> çağırmak kendi kilidini bekleyerek zaman
    /// aşımına düşer.
    /// </param>
    public SimulatorState Mutate(Func<SimulatorState, SimulatorState> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // Kilit AYRI bir dosya, durum dosyasının kendisi değil. Sebebi
        // `rename`: atomik yazma durum dosyasını YERİNDEN DEĞİŞTİRİYOR, yani
        // ona tutunan bir tanıtıcı yazmadan sonra artık başka bir dosyayı
        // gösteriyor olurdu ve kilit hiçbir şeyi korumazdı.
        using var gate = AcquireLock();

        var updated = change(Read()) with { SchemaVersion = SimulatorState.CurrentSchemaVersion };

        WriteAtomic(updated);

        return updated;
    }

    /// <summary>
    /// Metni duruma çevirir. <see langword="public"/> çünkü bekçiler dosyanın
    /// <b>teldeki</b> hâlini de sınıyor.
    /// </summary>
    public static SimulatorState Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SimulatorState? state;

        try
        {
            state = JsonSerializer.Deserialize<SimulatorState>(json, Json);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                $"Simülatör durum dosyası ayrıştırılamadı: {error.Message}. Boş sayılmıyor — "
                + "boş saymak, istenmiş bir senaryonun kaybolduğu hâli 'hiç istenmemiş' diye "
                + "okuturdu.",
                error);
        }

        if (state is null)
        {
            throw new InvalidOperationException(
                "Simülatör durum dosyası `null` ayrıştırdı. Dosya `null` içeriyor olabilir; "
                + "boş bir durum istiyorsanız dosyayı silin.");
        }

        if (state.SchemaVersion > SimulatorState.CurrentSchemaVersion)
        {
            // GERİYE değil İLERİYE dönük uyumsuzluk reddediliyor: daha yeni bir
            // sürümü eski kodla okumak, tanınmayan alanları sessizce DÜŞÜRÜP
            // geri yazmak demek — yani veriyi kaybeden taraf okuyucu olurdu.
            throw new InvalidOperationException(
                $"Durum dosyası şema sürümü {state.SchemaVersion}, bu kod {SimulatorState.CurrentSchemaVersion} "
                + "biliyor. Daha yeni bir dosyayı eski kodla yazmak tanınmayan alanları sessizce siler.");
        }

        return state;
    }

    /// <summary>Durumu JSON'a çevirir — dosyaya yazılan birebir metin.</summary>
    public static string Serialize(SimulatorState state) =>
        JsonSerializer.Serialize(state, Json);

    private FileStream AcquireLock()
    {
        var lockPath = Path + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Başka bir oturum kilidi tutuyor. Dosya kilidinde bir
                // "bekle ve uyan" yolu yok; kısa bir uyku ile yeniden deniyoruz.
                //
                // BEKLEME RASTGELE, SABİT DEĞİL — ve bu ölçülerek düzeltildi.
                // İlk hâli sabit 25 ms'ydi ve iki yazarın 40'ar turluk bir
                // ölçümünde biri 10 sn'lik tavana dayanıp DÜŞTÜ: sabit geri
                // çekilmede iki yazar adım kilidine giriyor, aynı anda uyanıp
                // aynı anda deniyor ve biri sistematik olarak açlığa
                // düşebiliyor. Rastgelelik o eşzamanlılığı kırıyor.
                //
                // Tavan da yükseltildi (10 → 30 sn) ama ASIL DÜZELTME BU
                // DEĞİL: süreyi büyütmek aynı kusuru daha seyrek gösterirdi ve
                // yeşillik makinenin o anki yüküne bağlı kalırdı (§6). Tavanın
                // işlevi hâlâ yalnızca çökmüş bir sürecin sonsuz beklemeye
                // dönüşmemesi.
                Thread.Sleep(Random.Shared.Next(1, 15));
            }
            catch (IOException error)
            {
                throw new InvalidOperationException(
                    $"Simülatör durum kilidi {LockTimeout.TotalSeconds:F0} sn içinde alınamadı ({lockPath}). "
                    + "Tek bir oku-değiştir-yaz turu milisaniyeler sürüyor, yani bu süreye dayanmak "
                    + "bir arıza işareti: çökmüş bir süreç kilidi bırakmamış olabilir.",
                    error);
            }
        }
    }

    private void WriteAtomic(SimulatorState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;

        // Geçici dosya AYNI dizinde: `File.Move` yalnızca aynı birim içinde
        // atomik. `Path.GetTempPath()` başka bir birim olabilir ve orada taşıma
        // kopyala-sil'e dönerek atomikliği sessizce kaybederdi.
        var temporary = System.IO.Path.Combine(
            directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporary, Serialize(state));
            File.Move(temporary, Path, overwrite: true);
        }
        catch
        {
            // Taşıma düşerse geçici dosya kalmasın (§3: başlattığın her şeyi
            // topla — bir dosya da bir kalıntı).
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }
}

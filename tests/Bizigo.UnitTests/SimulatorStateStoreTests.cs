using Bizigo.Simulators.Mcp;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Durum katmanının bekçileri</b> (M03).
///
/// <para>
/// Bu dosyanın ölçtüğü şeyler bir depolama detayı değil: <c>bizigo-sim</c>
/// yüzeyinin <b>tamamı</b> bu dosyanın doğru okunmasına bağlı. Yarım yazılmış
/// ya da yarısı kaybolmuş bir durum, <c>sim.state</c>'i ve
/// <c>sim.device.silence</c>'ı sessizce yalancı yapıyor.
/// </para>
/// </summary>
public sealed class SimulatorStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 18, 9, 19, 47, TimeSpan.Zero);

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"bizigo-sim-state-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(directory, "state.json");

    private SimulatorStateStore Store() => new(StatePath, new FakeTimeProvider(Moment));

    /// <summary>Dosya yokken okumak <b>boş</b> döner, patlamaz.</summary>
    [Fact]
    public void Dosya_yokken_bos_durum()
    {
        var state = Store().Read();

        Assert.Equal(SimulatorState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Empty(state.DeviceStates);
    }

    /// <summary>
    /// <b>Şema sürümü yazılıyor.</b>
    ///
    /// <para>
    /// İlk sürümde yazılmasının sebebi ikinci sürüm: alan eklendiğinde eski bir
    /// dosyayı okuyan kod, sürümü görmeden onu <i>eksik</i> değil
    /// <i>varsayılan</i> diye okur ve fark hiçbir yerde görünmez.
    /// </para>
    /// </summary>
    [Fact]
    public void Sema_surumu_dosyaya_yaziliyor()
    {
        Store().Mutate(state => state.With("fw-ankara-01", new SimulatorDeviceState("saat-kaymasi", Moment)));

        Assert.Contains("\"schema_version\": 1", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Yazma atomik: geçici dosya arkada kalmıyor.</b>
    ///
    /// <para>
    /// Kalıntı bir <c>.tmp</c> tek başına zararsız görünüyor ama ölçtüğü şey
    /// başka: taşımanın <b>gerçekten</b> olduğu. Kopyala-üstüne-yaz'a dönmüş bir
    /// uygulama burayı geçemiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Yazmadan_sonra_gecici_dosya_kalmiyor()
    {
        var store = Store();

        store.Mutate(state => state.With("fw-ankara-01", new SimulatorDeviceState("saat-kaymasi", Moment)));
        store.Mutate(state => state.With("lb-web-01", new SimulatorDeviceState(Silenced: true)));

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        // Ve dosya HER ZAMAN ayrıştırılabilir — yarım bir hâl yok.
        Assert.Equal(2, store.Read().DeviceStates.Count);
    }

    /// <summary>
    /// <b>İki eşzamanlı yazar, iki AYRI cihaz — ikisi de yaşıyor.</b>
    ///
    /// <para>
    /// <b>Bu testin ölçtüğü kayıp bu depoda kayıtlı.</b> Kilitsiz bir
    /// oku-değiştir-yaz'da iki yazar da dosyanın <i>eski</i> hâlini okur ve
    /// ikincisi birincinin değişikliğini <b>sessizce siler</b> — <i>"CSV'de aynı
    /// kaynak iki kez geçince son satır sessizce kazanıyordu, ve kazanan şey
    /// owner_group'tu."</i> Burada kaybolacak şey bir cihazın senaryosu ya da
    /// susturması olurdu, ve belirtisi olmazdı.
    /// </para>
    ///
    /// <para>
    /// <b>İki AYRI depo örneği</b> kullanılıyor, tek bir örnek değil: üretimde
    /// iki stdio oturumu iki <b>ayrı süreç</b>, yani paylaşılan bellek içi bir
    /// kilit onları bağlamazdı. Ölçülen şey dosya kilidi.
    /// </para>
    ///
    /// <para>
    /// <b>Duvar saati denklemde değil</b> (§6): iddia <i>"iki kayıt da var"</i>,
    /// ve bu makinenin hızından bağımsız olarak doğru ya da yanlış. Tekrar sayısı
    /// yarışı <b>olası</b> kılmak için; sonucu belirlemiyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Iki_esZamanli_yazar_birbirinin_isini_silmiyor()
    {
        const int Rounds = 20;

        // Belirteç DIŞARIDA yakalanıyor: `TestContext.Current` bir
        // `AsyncLocal` ve `Task.Run` gövdesinde güvenilir şekilde akmıyor.
        var ct = TestContext.Current.CancellationToken;

        var first = Task.Run(
            () =>
            {
                var store = Store();

                for (var i = 0; i < Rounds; i++)
                {
                    store.Mutate(state => state.With(
                        "fw-ankara-01",
                        state.For("fw-ankara-01") with { Scenario = "saat-kaymasi", ScenarioSetAt = Moment }));
                }
            },
            ct);

        var second = Task.Run(
            () =>
            {
                var store = Store();

                for (var i = 0; i < Rounds; i++)
                {
                    store.Mutate(state => state.With(
                        "lb-web-01",
                        state.For("lb-web-01") with { Silenced = true, SilencedAt = Moment }));
                }
            },
            ct);

        await Task.WhenAll(first, second);

        var final = Store().Read();

        Assert.Equal("saat-kaymasi", final.For("fw-ankara-01").Scenario);
        Assert.True(
            final.For("lb-web-01").Silenced,
            "İkinci yazarın işi kayboldu: kilitsiz bir oku-değiştir-yaz turunda son yazan, "
            + "başkasının cihazını da sessizce siliyor.");
    }

    /// <summary>
    /// <b>Bozuk dosya sessizce boş sayılmıyor.</b>
    ///
    /// <para>
    /// Boş saymak <i>"hiç senaryo istenmemiş"</i> demek olurdu ve bu, istenmiş
    /// bir senaryonun <b>kaybolduğu</b> hâlle aynı çıktıyı verirdi — okuyan
    /// ikincisini hiç düşünmezdi (§7).
    /// </para>
    /// </summary>
    [Fact]
    public void Bozuk_dosya_bos_sayilmiyor_firlatiyor()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatePath, "{ bu gecerli json degil");

        var error = Assert.Throws<InvalidOperationException>(() => Store().Read());

        Assert.Contains("ayrıştırılamadı", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Daha yeni bir şema sürümü reddediliyor.</b>
    ///
    /// <para>
    /// İleriye dönük uyumsuzluğun reddedilmesi, geriye dönükten farklı bir
    /// karar: daha yeni bir dosyayı eski kodla <b>yazmak</b>, tanınmayan
    /// alanları sessizce düşürüp geri yazmak demek — yani veriyi kaybeden taraf
    /// okuyucu olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Daha_yeni_sema_surumu_reddediliyor()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatePath, """{ "schema_version": 99, "devices": {} }""");

        var error = Assert.Throws<InvalidOperationException>(() => Store().Read());

        Assert.Contains("99", error.Message, StringComparison.Ordinal);
        Assert.Contains("sessizce siler", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Niyet ile etki ayrı alanlar ve teldeki adları da ayrı.</b>
    ///
    /// <para>
    /// Alan adlarının dosyada ölçülmesi gerekiyor: C# tarafında iki ayrı
    /// özellik olması, ikisinin JSON'a <b>ayrı anahtarlarla</b> indiğini
    /// kanıtlamıyor — ve dosyayı okuyan bir insan ya da başka bir araç yalnızca
    /// anahtarları görüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Niyet_ve_etki_dosyada_ayri_anahtarlar()
    {
        Store().Mutate(state => state.With(
            "fw-ankara-01",
            new SimulatorDeviceState("saat-kaymasi", Moment, LastAppliedAt: null)));

        var written = File.ReadAllText(StatePath);

        Assert.Contains("\"scenario_set_at\"", written, StringComparison.Ordinal);

        // `last_applied_at` NULL olduğu için yazılmıyor (`WhenWritingNull`) —
        // ve bu doğru: "yazılmadı" ile "null yazıldı" aynı şeyi söylüyor.
        // Ölçülen şey okunduğunda ayrımın KORUNDUĞU.
        var reloaded = Store().Read().For("fw-ankara-01");

        Assert.Equal(Moment, reloaded.ScenarioSetAt);
        Assert.Null(reloaded.LastAppliedAt);
    }

    public void Dispose()
    {
        // §3: başlattığın her şeyi topla — bir dizin de bir kalıntı.
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

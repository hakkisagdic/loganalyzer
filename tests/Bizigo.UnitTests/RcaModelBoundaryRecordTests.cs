using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Bizigo.Rca.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T54 — muafiyetli bir koşum, muafiyetsizinden AYIRT EDİLEBİLİR olmalı.</b>
///
/// <para>
/// Olgu §7'nin tam sınıfı: biri model sınırı muafiyeti açıyor, gerekçesini
/// yapılandırmaya yazıyor, ve o koşumun kaydına bakan hiç kimse muafiyetin
/// uygulandığını göremiyor. Hata yok, sayaç yok, belirti yok — kayıt
/// <b>muafiyetsiz bir koşumdan ayırt edilemiyor</b>.
/// </para>
///
/// <para>
/// <b>Neden yapılandırma okumak cevap değil.</b> Raporu okuyan kişi o koşum
/// sırasında <i>geçerli olan</i> gerekçeyi görmek zorunda; yapılandırma o gün
/// değişmiş olabilir ve <c>Rca:Model:BoundaryOverrideReason</c>'ı okuyan bir
/// ekran <b>bugünkü</b> gerekçeyi dünkü koşumun yanına yazardı. Muafiyetin
/// <i>ne zaman</i> uygulandığı kaydın kendisinde durmak zorunda.
/// </para>
///
/// <para>
/// <b>Bu sınıf bir ölçümle başladı.</b> Bağlanmadan önce koşturuldu ve dördü de
/// kırmızı yandı: <c>rca_runs</c>'ta alan yoktu, telde anahtar yoktu. Kusur
/// enjekte edilmedi — zaten oradaydı.
/// </para>
/// </summary>
public sealed class RcaModelBoundaryRecordTests
{
    /// <summary>
    /// Telin taşımak zorunda olduğu iki anahtar. Adları
    /// <c>ModelEndpoint.AuditFields()</c>'takilerle <b>aynı</b> — ikinci bir
    /// adlandırma, iki gösterimin ayrışabileceği bir yer daha açardı.
    /// </summary>
    private const string BoundaryKey = "model_boundary";
    private const string ReasonKey = "model_boundary_override_reason";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private const string Gerekce = "DNS çözümlemesi kapalı ortamda; uç adresi elle doğrulandı (BZ-4417).";

    /// <summary>
    /// Yönlendirilebilir bir adres döndüren çözücü — muafiyet yolunu tetikleyen
    /// tek koşul.
    /// </summary>
    private sealed class YonlendirilebilirCozucu : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.113.10")]);
    }

    private sealed class OzelAdresCozucu : IEndpointAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.20.30.40")]);
    }

    /// <summary>
    /// <b>Uçlar gerçek kapıdan geçirilerek kuruluyor</b>, elle kurulmuyor:
    /// <c>ModelEndpoint</c>'in yapıcısı <c>private</c> ve muafiyetin hangi
    /// koşulda doğduğu <c>ModelBoundaryGate</c>'in kararı. Elle kurmak, kapının
    /// gevşediği gün bu sınıfın hâlâ yeşil kalması demekti.
    /// </summary>
    private static ModelEndpoint Uc(IEndpointAddressResolver resolver, string? overrideReason)
    {
        var verdict = new ModelBoundaryGate(resolver)
            .VerifyAsync(
                new ModelEndpointOptions
                {
                    Name = "kurum-ici-gpu",
                    BaseUrl = "http://gpu-kume.kurum.local:8000/v1",
                    Model = "qwen3-32b",
                    DataBoundary = DataBoundary.Internal,
                    BoundaryOverrideReason = overrideReason,
                },
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return verdict.Endpoint
            ?? throw new InvalidOperationException(
                $"Fixture ucu kapıdan geçemedi: {verdict.Rejection}");
    }

    private static ModelEndpoint MuafUc() => Uc(new YonlendirilebilirCozucu(), Gerekce);

    private static ModelEndpoint NormalUc() => Uc(new OzelAdresCozucu(), null);

    /// <summary>
    /// <b>Sabit kimlik ve sabit zaman</b> — ve bu bir kolaylık değil bu
    /// sınıfın koşulu.
    ///
    /// <para>
    /// <see cref="RcaRunEntity.Id"/> varsayılanı <c>Guid.NewGuid()</c>,
    /// <see cref="RcaRunEntity.RequestedAt"/> varsayılanı
    /// <c>DateTimeOffset.UtcNow</c>. İkisi de her çağrıda değişiyor, yani iki
    /// koşumun serileştirilmiş hâli <b>her zaman</b> farklı çıkıyor — sınır
    /// alanı ne yaparsa yapsın.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçümle bulundu:</b> sınır alanını tele sabit bir dizge yazarak bozan
    /// kusur <see cref="Muaf_kosum_muafiyetsizinden_ayirt_edilebiliyor"/>'u iki
    /// kez yeşil bıraktı. Birinci düzeltme (yalnızca sınırda ayrılan bir çift
    /// eklemek) <b>yetmedi</b>, çünkü asıl sebep buydu: karşılaştırma
    /// <i>kimlikleri</i> karşılaştırıyordu. Kapı, ölçmek istediği şeyi hiç
    /// ölçmemiş olmasına rağmen yeşil yanıyordu.
    /// </para>
    /// </summary>
    private static readonly Guid SabitKimlik = Guid.Parse("8c1f0e42-5b6d-4a71-9f30-2ad7c4e51b09");

    private static readonly DateTimeOffset SabitAn = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    private static RcaRunEntity Run() => new()
    {
        Id = SabitKimlik,
        RootRunId = SabitKimlik,
        RequestedAt = SabitAn,
        OwnerGroup = "network/core",
        Source = RcaTriggerSource.Schedule,
        TriggerIdentity = "gecelik",
        WindowFrom = DateTimeOffset.UnixEpoch,
        WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(45),
        State = RcaRunState.Complete,
        Accepted = true,
    };

    private static JsonElement Serialize(RcaRunEntity run) =>
        JsonSerializer.SerializeToElement(RcaRunResponse.Of(run), Wire);

    // ---------------------------------------------------------------------
    // Asıl iddia
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Muaf koşum ile muafiyetsiz koşum telde aynı baytları üretmiyor.</b>
    ///
    /// <para>
    /// Bu bekçinin şekli bilerek "alan var mı" değil "<b>iki koşum ayırt
    /// edilebiliyor mu</b>": alanın varlığı, dolduğunu kanıtlamıyor. Bir
    /// dönüşümün ikisini aynı çıktıya indirmesi (gerekçeyi kısaltmak, boş
    /// dizeye çevirmek, alanı <c>null</c> iken atlamak) tam olarak bu deponun
    /// ödediği hata sınıfı.
    /// </para>
    ///
    /// <para>
    /// <b>İki çift karşılaştırılıyor ve ikincisi ölçümle eklendi.</b> İlk hâli
    /// yalnızca <c>Overridden</c> + gerekçe ile <c>Verified</c>'i
    /// karşılaştırıyordu; sınır alanını tele sabit bir dizge yazarak bozan bir
    /// kusur bu testi <b>yeşil bıraktı</b>, çünkü iki koşum <i>gerekçe</i>
    /// alanından da ayrılıyordu ve tek başına o fark iddiayı geçiriyordu. Yani
    /// kapı doğru cevabı <b>yanlış sebeple</b> veriyordu.
    /// </para>
    ///
    /// <para>
    /// <b>Kimliğin ve zamanın sabit olması bu kapının şartı</b> —
    /// <see cref="SabitKimlik"/>'in belgesinde ölçümüyle yazılı. Sabit
    /// olmasalar bu iddia hiçbir şey ölçmez ve <b>her zaman geçer</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Muaf_kosum_muafiyetsizinden_ayirt_edilebiliyor()
    {
        var muaf = Run();
        muaf.ModelBoundary = RcaModelBoundary.Overridden;
        muaf.ModelBoundaryOverrideReason = "DNS kapalı; adres doğrulaması atlandı.";

        var normal = Run();
        normal.ModelBoundary = RcaModelBoundary.Verified;

        // Önce iddianın boş olmadığını göster: iki koşum SINIR DIŞINDA birebir
        // aynı. Bu satır olmadan aşağıdaki `NotEqual` başka bir farkı ölçebilir.
        var taban = Run();
        taban.ModelBoundary = RcaModelBoundary.Verified;
        Assert.Equal(Serialize(normal).GetRawText(), Serialize(taban).GetRawText());

        Assert.NotEqual(
            Serialize(normal).GetRawText(),
            Serialize(muaf).GetRawText());

        // Yalnızca sınır alanında ayrılan çift — gerekçe ikisinde de `null`,
        // yani farkı taşıyabilecek tek alan sınırın kendisi.
        var konusmadi = Run();
        konusmadi.ModelBoundary = RcaModelBoundary.NotEngaged;

        Assert.NotEqual(
            Serialize(normal).GetRawText(),
            Serialize(konusmadi).GetRawText());
    }

    /// <summary>
    /// Gerekçe telde <b>birebir</b> — kısaltılmıyor, boşaltılmıyor.
    /// </summary>
    [Fact]
    public void Muafiyet_gerekcesi_telde_birebir()
    {
        var run = Run();
        run.ModelBoundary = RcaModelBoundary.Overridden;
        run.ModelBoundaryOverrideReason = Gerekce;

        var wire = Serialize(run);

        Assert.Equal(Gerekce, wire.GetProperty(ReasonKey).GetString());
        Assert.Equal("overridden", wire.GetProperty(BoundaryKey).GetString());
    }

    /// <summary>
    /// <b>Muafiyetsiz koşum da bunu SÖYLÜYOR</b> — sessiz kalmıyor.
    ///
    /// <para>
    /// Yalnızca muafiyet varken görünen bir alan, muafiyetsiz koşumu <i>"bu soru
    /// sorulmamış"</i> hâline sokardı. T38'in "gizlenen sıfır" kararıyla aynı
    /// gerekçe: iyi olan uç da yazılır, yoksa okuyan iyimser yanılır.
    /// </para>
    /// </summary>
    [Fact]
    public void Muafiyetsiz_kosum_da_bunu_soyluyor()
    {
        var run = Run();
        run.ModelBoundary = RcaModelBoundary.Verified;

        var wire = Serialize(run);

        Assert.Equal("verified", wire.GetProperty(BoundaryKey).GetString());
        Assert.True(
            wire.TryGetProperty(ReasonKey, out var reason),
            $"`{ReasonKey}` muafiyetsiz koşumda telden DÜŞÜYOR. Alanın yok olması ile " +
            "`null` olması istemcide aynı görünür, ama biri \"muafiyet yok\" diğeri " +
            "\"bu sürüm bu soruyu bilmiyor\" demek.");

        Assert.Equal(JsonValueKind.Null, reason.ValueKind);
    }

    /// <summary>
    /// <b><c>null</c> ile boş dize aynı şeye indirilmiyor.</b>
    ///
    /// <para>
    /// İkisi farklı iddia: <c>null</c> <i>"muafiyet yok"</i>, boş dize
    /// <i>"muafiyet var ama gerekçesi yazılmamış"</i> — ve ikincisi
    /// <see cref="ModelBoundaryGate"/> tarafından <b>reddediliyor</b>, yani tele
    /// hiç inmemeli. Kayıt o kapıya <b>güvenmiyor</b>: bir gerekçesiz muafiyet
    /// satıra yazılabilse bile telde <c>overridden</c> + boş gerekçe olarak
    /// görünür ve okuyan farkı görür.
    /// </para>
    ///
    /// <para>
    /// <c>string.Empty</c>'ye çeviren bir dönüşüm bu ayrımı siler ve
    /// <c>AuditFields()</c> tam olarak bunu yapıyor
    /// (<c>BoundaryOverrideReason ?? string.Empty</c>) — orada doğru, çünkü
    /// denetim sözlüğünün değerleri <c>object</c> ve <c>null</c> anahtarı
    /// düşürürdü; burada yanlış olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Null_ile_bos_dize_ayni_seye_indirilmiyor()
    {
        var yok = Run();
        yok.ModelBoundary = RcaModelBoundary.Verified;

        var yazilmamis = Run();
        yazilmamis.ModelBoundary = RcaModelBoundary.Overridden;
        yazilmamis.ModelBoundaryOverrideReason = string.Empty;

        var a = Serialize(yok).GetProperty(ReasonKey);
        var b = Serialize(yazilmamis).GetProperty(ReasonKey);

        Assert.Equal(JsonValueKind.Null, a.ValueKind);
        Assert.Equal(JsonValueKind.String, b.ValueKind);
        Assert.Equal(string.Empty, b.GetString());
    }

    /// <summary>
    /// Dört hâlin dördü telde <b>ayrı</b> dizgeler.
    ///
    /// <para>
    /// <c>Unspecified</c>'ın ayrı durması gerekiyor ve bu bir kaçış değil bir
    /// olgu: satır <see cref="RcaAdmission.AdmitAsync"/> ile doğuyor, damga
    /// <see cref="RcaAdmission.TryStartAsync"/>'te basılıyor, ve reddedilen bir
    /// koşum hiç başlamıyor. Yani <i>"henüz kimse sınır hakkında bir şey
    /// söylemedi"</i> gerçek bir hâl — <c>NotEngaged</c> ile birleştirilseydi
    /// reddedilen bir koşum <i>"modele konuşmadı"</i> diye okunurdu, ki doğru
    /// ama <b>ölçülmemiş</b>: o koşum hiçbir şey yapmadı.
    /// </para>
    /// </summary>
    [Fact]
    public void Dort_hal_telde_ayri_gorunuyor()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var boundary in Enum.GetValues<RcaModelBoundary>())
        {
            var run = Run();
            run.ModelBoundary = boundary;

            Assert.True(
                seen.Add(Serialize(run).GetProperty(BoundaryKey).GetString()!),
                $"`{boundary}` telde başka bir hâlle aynı dizgeye iniyor.");
        }

        Assert.Equal(Enum.GetValues<RcaModelBoundary>().Length, seen.Count);
    }

    // ---------------------------------------------------------------------
    // İki gösterim ayrışmıyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Koşum kaydının gerekçe alan adı, <c>AuditFields()</c>'takiyle aynı.</b>
    ///
    /// <para>
    /// İki gösterim (denetim sözlüğü ve koşum kaydı) aynı olguyu anlatıyor;
    /// farklı adlarla anlatırlarsa bir gün biri değişir, diğeri kalır ve
    /// ayrışmayı hiçbir şey yakalamaz. Ad eşitliği bu yüzden bir bekçi —
    /// T61'in "statünün dört gösterimi" kalıbının aynısı.
    /// </para>
    /// </summary>
    [Fact]
    public void Alan_adi_AuditFields_ile_ayni()
    {
        var audit = MuafUc().AuditFields();

        Assert.True(
            audit.ContainsKey(ReasonKey),
            $"`AuditFields()` artık `{ReasonKey}` yazmıyor — koşum kaydı ile denetim " +
            "sözlüğü ayrıştı ve iki gösterim aynı olguya iki ad veriyor.");

        var wireKeys = typeof(RcaRunResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(static p => p.GetCustomAttributes<JsonPropertyNameAttribute>())
            .Select(static a => a.Name)
            .ToArray();

        Assert.Contains(ReasonKey, wireKeys);
    }

    // ---------------------------------------------------------------------
    // Damga: geçersiz hâl var OLAMIYOR
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Muaf bir uçtan kurulan damga gerekçeyi ucun kendisinden alıyor.</b>
    ///
    /// <para>
    /// Gerekçenin çağırandan alınmaması bu kapının asıl konusu: alınsaydı bir
    /// çağıran <c>Overridden</c> deyip başka bir metin geçirebilirdi ve kayıt,
    /// ucun gerçekten hangi gerekçeyle açıldığından <b>ayrışabilirdi</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Damga_gerekceyi_ucun_kendisinden_aliyor()
    {
        var stamp = RcaModelBoundaryStamp.From(MuafUc());

        Assert.Equal(RcaModelBoundary.Overridden, stamp.Boundary);
        Assert.Equal(Gerekce, stamp.Reason);
    }

    /// <summary>
    /// Muafiyetsiz uç <see cref="RcaModelBoundary.Verified"/> damgalanıyor ve
    /// gerekçesi <see langword="null"/> — boş dize <b>değil</b>.
    /// </summary>
    [Fact]
    public void Muafiyetsiz_uc_verified_damgaliyor()
    {
        var stamp = RcaModelBoundaryStamp.From(NormalUc());

        Assert.Equal(RcaModelBoundary.Verified, stamp.Boundary);
        Assert.Null(stamp.Reason);
    }

    /// <summary>
    /// <b>Modele konuşmayan koşumun damgası <see cref="RcaModelBoundary.Unspecified"/>
    /// DEĞİL.</b>
    ///
    /// <para>
    /// İkisini birleştirmek, reddedilmiş bir koşumu <i>"modele konuşmadı"</i>
    /// diye kaydetmek olurdu — doğru bir cümle, ama ölçülmemiş bir yerden
    /// söylenmiş: o koşum hiçbir şey yapmadı.
    /// </para>
    /// </summary>
    [Fact]
    public void Modele_konusmayan_kosum_unspecified_degil()
    {
        var stamp = RcaModelBoundaryStamp.NotEngaged();

        Assert.Equal(RcaModelBoundary.NotEngaged, stamp.Boundary);
        Assert.NotEqual(RcaModelBoundary.Unspecified, stamp.Boundary);
        Assert.Null(stamp.Reason);
    }

    // ---------------------------------------------------------------------
    // YAZMA yolu: damga gerçekten satıra iniyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b><see cref="RcaAdmission.TryStartAsync"/> damgayı satıra yazıyor.</b>
    ///
    /// <para>
    /// Yukarıdaki kapılar tipleri ve teli tutuyor; bu, ikisinin arasındaki
    /// <b>yazma</b> yolunu tutuyor — damga kurulabiliyor, tel taşıyabiliyor, ama
    /// arada onu satıra yazan bir satır olmasa kayıt yine boş kalırdı ve
    /// hiçbir şey kırmızı yanmazdı.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçülen bir boşluk:</b> <c>TryStartAsync</c>'in bu değişiklikten önce
    /// hiçbir testte çağıranı yoktu (birim ve entegrasyon paketlerinin
    /// tamamında sıfır isabet) — yani <c>Queued → Running</c> geçişi ve
    /// <c>StartedAt</c> damgası da sınanmamıştı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryStart_damgayi_satira_yaziyor()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));

        var admission = new RcaAdmission(
            factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            time);

        var run = Run();
        run.State = RcaRunState.Queued;
        run.RootRunId = run.Id;

        await using (var db = factory.CreateDbContext())
        {
            db.RcaRuns.Add(run);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(RcaModelBoundary.Unspecified, run.ModelBoundary);

        Assert.True(await admission.TryStartAsync(
            run.Id,
            RcaModelBoundaryStamp.From(MuafUc()),
            TestContext.Current.CancellationToken));

        await using (var db = factory.CreateDbContext())
        {
            var saved = await db.RcaRuns.SingleAsync(
                r => r.Id == run.Id,
                TestContext.Current.CancellationToken);

            Assert.Equal(RcaModelBoundary.Overridden, saved.ModelBoundary);
            Assert.Equal(Gerekce, saved.ModelBoundaryOverrideReason);
            Assert.Equal(RcaRunState.Running, saved.State);
        }
    }

    /// <summary>
    /// <b>Damganın geçersiz hâli tip düzeyinde ulaşılamaz.</b>
    ///
    /// <para>
    /// İki iddia, ikisi de koşum gerektirmiyor ve <b>ikisi de bu ticket'ın
    /// taşıyıcı kararı</b>: muafiyetin gerekçesiz kaydedilmesi bir çalışma anı
    /// kontrolüyle değil, tipin varoluş şartıyla engelleniyor.
    /// </para>
    ///
    /// <list type="number">
    /// <item><c>RcaModelBoundaryStamp</c>'in <b>public yapıcısı yok</b> — bir
    /// damga yalnızca iki fabrikadan çıkabiliyor, dolayısıyla
    /// <c>Overridden</c> + <c>null</c> bileşimi <b>kurulamıyor</b>.</item>
    /// <item><c>TryStartAsync</c>'in damga parametresi <b>zorunlu</b> —
    /// varsayılanı yok. Varsayılanı olsaydı model yolu bağlandığı gün çağıran
    /// hiçbir şey değiştirmeden derlenir ve kayıt sessizce
    /// <see cref="RcaModelBoundary.Unspecified"/> kalırdı; yani kapı, kurulduğu
    /// gün işe yarayıp <b>ihtiyaç duyulduğu gün</b> sessizce çekilirdi.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Damganin_gecersiz_hali_tip_duzeyinde_ulasilamaz()
    {
        var ctors = typeof(RcaModelBoundaryStamp)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(
            ctors.Length == 0,
            "`RcaModelBoundaryStamp`'in public yapıcısı var — gerekçesiz bir `Overridden` " +
            "damgası artık KURULABİLİR ve muafiyetin sessiz kalması yeniden mümkün.");

        var stamp = typeof(RcaAdmission)
            .GetMethod(nameof(RcaAdmission.TryStartAsync))!
            .GetParameters()
            .Single(static p => p.ParameterType == typeof(RcaModelBoundaryStamp));

        Assert.False(
            stamp.IsOptional,
            "`TryStartAsync`'in damga parametresi isteğe bağlı hâle gelmiş. Varsayılan bir " +
            "damga, model yolu bağlandığı gün çağıranı hiçbir şey değiştirmeye zorlamaz ve " +
            "kayıt sessizce `Unspecified` kalır — kapının ihtiyaç duyulduğu gün çekilmesi.");
    }

    /// <summary>
    /// <b>Bekçi boş küme üzerinde dönmüyor.</b>
    ///
    /// <para>
    /// Yukarıdaki kapıların hepsi tel anahtarlarını <b>ad</b> ile arıyor. Bir
    /// yeniden adlandırma bu sınıfı sessizce ölçtüğü şeyden ayırırdı:
    /// <c>TryGetProperty</c> her yerde <c>false</c> dönerdi ve iddiaların bir
    /// kısmı yine geçerdi. Anahtarların gerçekten var olması ayrı bir kapı.
    /// </para>
    ///
    /// <para>
    /// Muaf ucun da gerçekten muaf çıktığı burada sınanıyor: kapının kendisi
    /// gevşerse <see cref="Damga_gerekceyi_ucun_kendisinden_aliyor"/> hâlâ
    /// geçerdi ama ölçtüğü şey değişirdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekci_bos_kume_uzerinde_donmuyor()
    {
        var wire = Serialize(Run());

        Assert.True(wire.TryGetProperty(BoundaryKey, out _), $"`{BoundaryKey}` telde yok.");
        Assert.True(wire.TryGetProperty(ReasonKey, out _), $"`{ReasonKey}` telde yok.");
        Assert.NotEmpty(Enum.GetValues<RcaModelBoundary>());

        Assert.True(
            MuafUc().BoundaryOverridden,
            "Muaf uç fixture'ı artık muaf değil — `ModelBoundaryGate` muafiyet yolunu " +
            "başka bir koşula bağlamış olabilir ve bu sınıfın yarısı ölçtüğünü sanmadığı " +
            "şeyi ölçüyor.");

        Assert.False(NormalUc().BoundaryOverridden);
    }
}

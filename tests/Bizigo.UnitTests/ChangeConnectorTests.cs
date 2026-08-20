using System.Text.Json;
using Bizigo.Api.Connectors;
using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// T25'in dört kabul kriteri.
///
/// <para>
/// En sertlerinden biri <see cref="Bagli_hata_mesaji_kimlik_bilgisi_sizdirmiyor"/>:
/// bağlantı testinin hata mesajı, gizli bilginin en sık sızdığı yer. Kütüphane
/// istisnaları parolayı ya da adresi metnin içinde taşıyor ve o metni kimse
/// yazmıyor — bu yüzden temizlik runner'ın dikkatine değil, servisin kapısına
/// konuldu.
/// </para>
/// </summary>
public sealed class ChangeConnectorTests : IDisposable
{
    private const string Credential = "cihaz-p4rol4-XyZgizliJETON9876";

    private static readonly DateTimeOffset Start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Key =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly SecretProtector _protector = new(Key);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private static AccessScope Scope(params string[] groups) =>
        AccessScope.ForGroups("esra", groups);

    private ChangeConnectorService Service(params IChangeConnectorRunner[] runners) => new(
        _factory,
        _protector,
        runners.Length > 0 ? runners : [new WebhookConnectorRunner(NullLogger<WebhookConnectorRunner>.Instance)],
        _time,
        NullLogger<ChangeConnectorService>.Instance);

    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    private static ConnectorInput WebhookInput(
        string slug = "gh-network",
        string ownerGroup = "network/core",
        bool enabled = true,
        string? credential = Credential) => new()
        {
            Slug = slug,
            Name = "GitHub — ağ yapılandırması",
            ConnectorType = ChangeConnectorType.Webhook,
            OwnerGroup = ownerGroup,
            Config = Config("""{"provider":"github","targetKind":"Config","defaultChangeKind":"deploy"}"""),
            Credential = credential,
            Enabled = enabled,
        };

    // ------------------------------------------------- kimlik bilgisi saklama

    [Fact]
    public async Task Kimlik_bilgisi_veritabaninda_duz_metin_durmuyor()
    {
        var saved = await Service().SaveAsync(null, WebhookInput(), Scope("network/core"), Token);

        Assert.True(saved.Ok, saved.Error);

        await using var db = _factory.CreateDbContext();
        var row = await db.ChangeConnectors.SingleAsync(Token);

        Assert.NotEqual(Credential, row.CredentialCipher);
        Assert.DoesNotContain(Credential, row.CredentialCipher, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, row.ConfigJson, StringComparison.Ordinal);

        // Ama gerçekten geri okunabiliyor — maskeleme, kaybetmek değil.
        Assert.Equal(Credential, _protector.Unprotect(row.CredentialCipher));
    }

    [Fact]
    public async Task Anahtar_yoksa_kimlik_bilgisi_duz_metne_dusmuyor_kaydedilmiyor()
    {
        // "Şifreli saklanıyor" iddiasının sessizce yanlışlanacağı tek yer.
        var service = new ChangeConnectorService(
            _factory,
            new SecretProtector(base64Key: null),
            [new WebhookConnectorRunner(NullLogger<WebhookConnectorRunner>.Instance)],
            _time,
            NullLogger<ChangeConnectorService>.Instance);

        var result = await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token);

        Assert.False(result.Ok);
        Assert.Contains("Security:SecretKey", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, result.Error, StringComparison.Ordinal);

        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.ChangeConnectors.ToListAsync(Token));
    }

    [Fact]
    public async Task Bos_kimlik_bilgisi_mevcut_olani_silmiyor()
    {
        var service = Service();
        var created = await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token);

        // Ekran mevcut değeri hiç görmüyor (maskeli dönüyor), dolayısıyla her
        // kaydetmede geri gönderemiyor. Boşu "sil" saysaydık her ad düzeltmesi
        // kimlik bilgisini uçururdu.
        var updated = await service.SaveAsync(
            created.Connector!.Id,
            WebhookInput(credential: null) with { Name = "Yeni ad" },
            Scope("network/core"),
            Token);

        Assert.True(updated.Ok, updated.Error);
        Assert.Equal("Yeni ad", updated.Connector!.Name);
        Assert.Equal(Credential, _protector.Unprotect(updated.Connector.CredentialCipher));
    }

    // --------------------------------------------------------- sızıntı kapısı

    [Fact]
    public async Task Bagli_hata_mesaji_kimlik_bilgisi_sizdirmiyor()
    {
        // Runner bilerek sızdırıyor: gerçekte bunu yazan bir kütüphane
        // istisnası olur ve kimse fark etmez. Kapı servisde olduğu için
        // runner'ın niyeti sonucu değiştirmiyor.
        var leaky = new LeakyRunner();
        var service = Service(leaky);

        var created = await service.SaveAsync(
            null,
            WebhookInput() with { ConnectorType = ChangeConnectorType.DeviceConfig, IntervalSeconds = 300 },
            Scope("network/core"),
            Token);

        Assert.True(created.Ok, created.Error);

        var test = await service.TestAsync(created.Connector!.Id, Scope("network/core"), Token);

        Assert.False(test.Ok);
        Assert.DoesNotContain(Credential, test.Message, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_istisna_firlatsa_da_mesaj_temiz()
    {
        var service = Service(new ThrowingRunner());

        var created = await service.SaveAsync(
            null,
            WebhookInput() with { ConnectorType = ChangeConnectorType.DeviceConfig, IntervalSeconds = 300 },
            Scope("network/core"),
            Token);

        var test = await service.TestAsync(created.Connector!.Id, Scope("network/core"), Token);

        Assert.False(test.Ok);
        Assert.DoesNotContain(Credential, test.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- kapsam kapısı

    [Fact]
    public async Task Baska_grubun_connector_i_gorunmuyor_ve_yazilamiyor()
    {
        var service = Service();
        var created = await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token);
        var other = Scope("network/edge");

        // Görünmüyor.
        Assert.Empty(await service.ListAsync(other, Token));
        Assert.Null(await service.GetAsync(created.Connector!.Id, other, Token));

        // Yazılamıyor — ve 403 yerine "bulunamadı": o kimlikte bir connector
        // olduğunu doğrulamak da bilgi sızdırmaktır.
        var hijack = await service.SaveAsync(
            created.Connector.Id, WebhookInput(), other, Token);

        Assert.False(hijack.Ok);
        Assert.False(await service.DeleteAsync(created.Connector.Id, other, Token));
    }

    [Fact]
    public async Task Kapsam_disi_gruba_connector_tanimlanamiyor()
    {
        var result = await Service().SaveAsync(
            null, WebhookInput(ownerGroup: "network/edge"), Scope("network/core"), Token);

        Assert.False(result.Ok);
        Assert.Contains("kapsamınızda değil", result.Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ zamanlayıcı

    [Fact]
    public async Task Pasif_connector_kosmuyor()
    {
        var runner = new CountingRunner();
        var service = Service(runner);

        var created = await service.SaveAsync(
            null,
            WebhookInput(enabled: false) with
            {
                ConnectorType = ChangeConnectorType.DeviceConfig,
                IntervalSeconds = 300,
            },
            Scope("network/core"),
            Token);

        Assert.True(created.Ok, created.Error);

        // Pasif connector'ın vadesi hiç kurulmuyor.
        Assert.Null(created.Connector!.NextRunAt);
        Assert.Equal(0, await Scheduler(runner, service).RunTurnAsync(Token));
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public async Task Etkinlestirilen_connector_i_zamanlayici_devraliyor()
    {
        var runner = new CountingRunner();
        var service = Service(runner);

        var created = await service.SaveAsync(
            null,
            WebhookInput(enabled: false) with
            {
                ConnectorType = ChangeConnectorType.DeviceConfig,
                IntervalSeconds = 300,
            },
            Scope("network/core"),
            Token);

        var enabled = await service.SaveAsync(
            created.Connector!.Id,
            WebhookInput(credential: null) with
            {
                ConnectorType = ChangeConnectorType.DeviceConfig,
                IntervalSeconds = 300,
                Enabled = true,
            },
            Scope("network/core"),
            Token);

        Assert.True(enabled.Ok, enabled.Error);
        Assert.Equal(Start, enabled.Connector!.NextRunAt);

        var scheduler = Scheduler(runner, service);

        Assert.Equal(1, await scheduler.RunTurnAsync(Token));
        Assert.Equal(1, runner.Runs);

        // Vade ŞİMDİ'den itibaren yeniden kuruluyor; ikinci tur boş dönüyor.
        Assert.Equal(0, await scheduler.RunTurnAsync(Token));

        _time.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(1, await scheduler.RunTurnAsync(Token));
    }

    [Fact]
    public async Task Toplayicisi_olmayan_tip_etkinlestirilemiyor()
    {
        // T26 gelene kadar cihaz config connector'ı buraya takılıyor. Etkin ama
        // koşamayan bir connector, her turda hata yazıp çalışma geçmişini
        // gerçek arızaların görülemeyeceği hâle getirirdi.
        var result = await Service().SaveAsync(
            null,
            WebhookInput() with { ConnectorType = ChangeConnectorType.DeviceConfig, IntervalSeconds = 300 },
            Scope("network/core"),
            Token);

        Assert.False(result.Ok);
        Assert.Contains("toplayıcı henüz yok", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kosum_gecmisi_ve_son_hata_yaziliyor()
    {
        var runner = new LeakyRunner();
        var service = Service(runner);

        var created = await service.SaveAsync(
            null,
            WebhookInput() with { ConnectorType = ChangeConnectorType.DeviceConfig, IntervalSeconds = 60 },
            Scope("network/core"),
            Token);

        await Scheduler(runner, service).RunTurnAsync(Token);

        await using var db = _factory.CreateDbContext();
        var run = await db.ChangeConnectorRuns.SingleAsync(Token);
        var connector = await db.ChangeConnectors.SingleAsync(Token);

        Assert.Equal(ConnectorRunState.Failed, run.State);
        Assert.Equal(ConnectorRunState.Failed, connector.LastRunState);

        // Geçmiş de son hata da redaksiyondan geçmiş.
        Assert.DoesNotContain(Credential, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, connector.LastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- doğrulamalar

    [Theory]
    [InlineData("BÜYÜK")]
    [InlineData("bosluk var")]
    [InlineData("a")]
    [InlineData("-bastan-tire")]
    public async Task Gecersiz_slug_reddediliyor(string slug)
    {
        var result = await Service().SaveAsync(
            null, WebhookInput(slug: slug), Scope("network/core"), Token);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Ayni_slug_iki_connector_a_verilemiyor()
    {
        // Slug bir URL parçası: iki connector aynı slug'ı alsaydı hangi grubun
        // aldığı çağrıya göre değişirdi.
        var service = Service();

        Assert.True((await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token)).Ok);

        var second = await service.SaveAsync(
            null, WebhookInput() with { Name = "İkinci" }, Scope("network/core"), Token);

        Assert.False(second.Ok);
        Assert.Contains("kullanılıyor", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bilinmeyen_saglayici_webhook_connector_inda_reddediliyor()
    {
        var result = await Service().SaveAsync(
            null,
            WebhookInput() with { Config = Config("""{"provider":"bitbucket"}""") },
            Scope("network/core"),
            Token);

        Assert.False(result.Ok);
    }

    // ------------------------------------------- ekrandan tanımlanan webhook

    [Fact]
    public async Task Ekrandan_tanimlanan_webhook_ucu_alicida_cozuluyor()
    {
        // K34'ün asıl vaadi: connector ekrandan tanımlanıyor ve T24'ün alıcısı
        // onu görüyor.
        var service = Service();
        await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token);

        var registry = new ControlPlaneWebhookRegistry(
            _factory,
            service,
            new ChangeWebhookRegistry(new ChangeWebhookOptions()),
            NullLogger<ControlPlaneWebhookRegistry>.Instance);

        var endpoint = await registry.FindAsync("gh-network", Token);

        Assert.NotNull(endpoint);
        Assert.Equal(ChangeWebhookProviders.GitHub, endpoint.Provider);
        Assert.Equal("network/core", endpoint.OwnerGroup);
        Assert.Equal(ChangeTargetKind.Config, endpoint.TargetKind);

        // Gizli anahtar çözülmüş hâlde geliyor — imza doğrulaması onsuz
        // çalışamaz.
        Assert.Equal(Credential, endpoint.Secret);
    }

    [Fact]
    public async Task Ekrandan_pasife_alinan_webhook_ucu_kabul_etmiyor()
    {
        var service = Service();
        var created = await service.SaveAsync(null, WebhookInput(), Scope("network/core"), Token);

        await service.SaveAsync(
            created.Connector!.Id,
            WebhookInput(enabled: false, credential: null),
            Scope("network/core"),
            Token);

        var registry = new ControlPlaneWebhookRegistry(
            _factory,
            service,
            new ChangeWebhookRegistry(new ChangeWebhookOptions()),
            NullLogger<ControlPlaneWebhookRegistry>.Instance);

        Assert.Null(await registry.FindAsync("gh-network", Token));
    }

    private ChangeConnectorScheduler Scheduler(
        IChangeConnectorRunner runner,
        ChangeConnectorService service) => new(
        _factory,
        service,
        [runner],
        new ChangeConnectorOptions(),
        _time,
        NullLogger<ChangeConnectorScheduler>.Instance);

    /// <summary>Kimlik bilgisini hata mesajının içine koyan runner — bilerek.</summary>
    private sealed class LeakyRunner : IChangeConnectorRunner
    {
        public ChangeConnectorType ConnectorType => ChangeConnectorType.DeviceConfig;

        public Task<ConnectorTestResult> TestAsync(ConnectorContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorTestResult(
                false, $"SSH kimlik doğrulaması başarısız (parola: {context.Credential})."));

        public Task<ConnectorRunResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorRunResult(
                false, 0, $"Bağlantı reddedildi, kullanılan jeton {context.Credential}."));
    }

    private sealed class ThrowingRunner : IChangeConnectorRunner
    {
        public ChangeConnectorType ConnectorType => ChangeConnectorType.DeviceConfig;

        public Task<ConnectorTestResult> TestAsync(ConnectorContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"beklenmeyen: {context.Credential}");

        public Task<ConnectorRunResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"beklenmeyen: {context.Credential}");
    }

    private sealed class CountingRunner : IChangeConnectorRunner
    {
        public int Runs { get; private set; }

        public ChangeConnectorType ConnectorType => ChangeConnectorType.DeviceConfig;

        public Task<ConnectorTestResult> TestAsync(ConnectorContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorTestResult(true, "hazır"));

        public Task<ConnectorRunResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(new ConnectorRunResult(true, 1, string.Empty));
        }
    }
}

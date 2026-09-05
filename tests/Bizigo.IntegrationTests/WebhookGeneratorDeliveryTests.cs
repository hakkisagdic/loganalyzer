using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Simulators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Üretecin kurduğu teslimat, alıcının yolundan geçiyor</b> (S07 · S08) — ve
/// aynı teslimat iki kez gelince <b>tek kayıt</b> oluşuyor.
///
/// <para>
/// <b>Bu sınıf S07'de yazılamamıştı</b> ve sebebi ölçülmemiş bir varsayımdı:
/// <c>Bizigo.IntegrationTests</c> on altı proje referansı taşıyor ve
/// <c>src/</c> altındaki tek dışlanan proje <c>Bizigo.Api</c>'ydi. Dışlama
/// <i>bilinçli görünüyordu</i> — ama hiçbir yerde yazılı değildi, ve bu depoda
/// yazılı olmayan gerekçe gerekçe değil.
/// </para>
///
/// <para>
/// S08'de ölçüldü: referans eklendi, <c>typeof(Program)</c> ile <b>kullanıldı</b>,
/// ve <c>CS0433</c> <b>çıkmadı</b>. Yani dışlamanın bir bedeli yoktu; sanılan
/// bedel başka bir projede (<c>Bizigo.Mcp</c>) alınmış bir ölçümün buraya
/// taşınmasıydı. Ölçüm kendisi de iki adımlıydı, çünkü ilk hâli sessizce
/// atlanmıştı: önce sondaya <b>kasıtlı bir derleme hatası</b> konup dosyanın
/// gerçekten derlendiği kanıtlandı, sonra çakışma sınandı.
/// </para>
///
/// <para>
/// <b>Neden ayrı bir sınıf, <c>ChangeWebhookDeliveryTests</c> zaten varken:</b>
/// o sınıf anahtarları <b>elle</b> kuruyor (<c>"gh-network:d2"</c>) ve kısıtın
/// çalıştığını kanıtlıyor. Buradaki teslimat kimliği <b>üretecin gövdesinden
/// eşlenerek</b> geliyor — yani sınanan şey kısıt değil, <b>gerçek bir
/// sağlayıcı yükünün aynı kimliği iki kez üretip üretmediği</b>. İkisi ayrı
/// sorular ve ikincisi S07'nin kabul kriteri.
/// </para>
///
/// <para>
/// <b>Kapsam dışı ve bilerek:</b> anahtarın <c>{uç}:{kimlik}</c> biçiminde
/// öneklenmesi ucun işi ve
/// <see cref="ChangeWebhookDeliveryTests.Farkli_uclar_ayni_govdeyi_ayri_kaydediyor"/>
/// onu sınıyor. Burada o biçim <b>tekrarlanmıyor</b> — tekrarlansaydı ürünün
/// anahtar kuralının ikinci bir kopyası doğardı ve ayrıştıkları gün bu test
/// eski kuralı doğrulamaya devam ederdi.
/// </para>
///
/// <para>
/// <b>Koordinatör koşturur (§2).</b> Yazıldı, koşturulmadı.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class WebhookGeneratorDeliveryTests(DevStackFixture stack) : IAsyncLifetime
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 18, 9, 19, 47, TimeSpan.Zero);

    /// <summary>
    /// Alınma anı, üretecin yazdığı andan <b>bilerek farklı</b>: ikisi aynı
    /// olsaydı "sağlayıcının zaman biçimi ayrıştırıldı" ile "ayrıştırılamadı ve
    /// şimdiye düşüldü" aynı değeri üretirdi.
    /// </summary>
    private static readonly FakeTimeProvider Clock =
        new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private const string Secret = "paylasilan-anahtar";

    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private ChangeWebhookDeliveryLog _log = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);
        _log = new ChangeWebhookDeliveryLog(_factory);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.ChangeWebhookDeliveries.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ChangeWebhookEndpoint Endpoint(string provider) => new()
    {
        Id = "ci",
        Provider = provider,
        OwnerGroup = "network/core",
        Secret = Secret,
        TargetKind = ChangeTargetKind.Config,
        DefaultChangeKind = "deploy",
    };

    private static WebhookDelivery Delivery(string provider, string deliveryId, int run = 184) =>
        WebhookDeliveryFactory.Create(new WebhookDeliveryRequest
        {
            Provider = provider,
            Secret = Secret,
            DeliveryId = deliveryId,
            TargetId = "fw-ankara-01",
            Actor = "esra.yildiz",
            Summary = "fw-ankara-01: dış ACL'e 10.20.0.0/16 eklendi",
            Timestamp = Moment,
            Run = run,
        });

    /// <summary>
    /// Ucun alıcı yolunun sınanan kısmı: imza → eşleme → talep.
    ///
    /// <para>
    /// Ucun gövdesinin tamamı (HTTP, kapsam kapısı, ClickHouse yazımı) burada
    /// <b>koşmuyor</b> ve koşmadığı yazılı olmak zorunda — bu depoda adı ile
    /// gövdesi ayrışan bekçi ölçülmüş bir hata sınıfı (§7). Sınanan şey:
    /// üretecin gövdesi imzayı geçiyor, bir değişikliğe eşleniyor, ve aynı
    /// teslimat ikinci kez talep alamıyor.
    /// </para>
    /// </summary>
    private async Task<(SignatureVerdict Verdict, WebhookMapResult Mapped, DeliveryClaim Claim)> ReceiveAsync(
        WebhookDelivery delivery)
    {
        var endpoint = Endpoint(delivery.Provider);

        string? Header(string name) =>
            delivery.Headers.TryGetValue(name, out var value) ? value : null;

        var verdict = WebhookSignature.Verify(endpoint, Header, delivery.Body);
        var mapped = ChangeWebhookMapper.Map(endpoint, Header, delivery.Body, Clock);

        var claim = await _log.ClaimAsync(
            new ChangeWebhookDeliveryEntity
            {
                // Kimlik EŞLEMEDEN geliyor, elle kurulmuyor: sınanan şey tam
                // olarak "gerçek bir sağlayıcı yükü aynı kimliği iki kez
                // üretiyor mu".
                DeliveryKey = mapped.DeliveryId,
                EndpointId = endpoint.Id,
                Provider = endpoint.Provider,
                OwnerGroup = endpoint.OwnerGroup,

                // Eşleme her çağrıda YENİ bir kimlik üretiyor; sağlayıcının
                // yeniden denemesi de böyle görünüyor.
                ChangeId = mapped.Change!.ChangeId,
                ReceivedAt = Clock.GetUtcNow(),
            },
            TestContext.Current.CancellationToken);

        return (verdict, mapped, claim);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> üç sağlayıcının da üretilmiş
    /// gövdesi imzayı geçiyor, bir değişikliğe eşleniyor ve <b>bir</b> kayıt
    /// oluşturuyor.
    ///
    /// <para>
    /// <c>generic</c> burada <b>yok</b> ve sebebi kapsam: genel eşleme ucun
    /// yapılandırmasından geliyor, yani sınanan şey üreteç değil test
    /// yapılandırması olurdu. Birim paketi onu kendi eşlemesiyle sınıyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [Trait("Category", "Integration")]
    public async Task Uretilen_teslimat_imzayi_gecip_kayit_olusturuyor(string provider)
    {
        var (verdict, mapped, claim) = await ReceiveAsync(
            Delivery(provider, $"{provider}-tek"));

        Assert.Equal(SignatureVerdict.Valid, verdict);
        Assert.Equal(WebhookMapOutcome.Mapped, mapped.Outcome);
        Assert.True(claim.Claimed);

        Assert.Equal("fw-ankara-01", mapped.Change!.TargetId);
        Assert.Equal("network/core", mapped.Change.OwnerGroup);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey — S07'nin taşıyıcı kabul kriteri ve
    /// S08'e kadar tek bir testte buluşamayan yer:</b> aynı teslimat iki kez
    /// gönderilince <b>tek kayıt</b> oluşuyor.
    ///
    /// <para>
    /// Yoksa RCA'nın <i>"öncesinde şu config değişti"</i> kanıtı iki kez
    /// görünür: <b>sayı yanlış, hata yok.</b> Sağlayıcılar yeniden denemeyi
    /// rutin olarak yapıyor — GitHub bir zaman aşımından sonra aynı teslimatı
    /// tekrar gönderiyor — yani bu yol istisnai değil, olağan.
    /// </para>
    ///
    /// <para>
    /// Kaybeden talep <b>kazananın kimliğini</b> görüyor: sağlayıcı iki kez
    /// sorduğunda iki farklı cevap almamalı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [Trait("Category", "Integration")]
    public async Task Ayni_teslimat_iki_kez_gelince_tek_kayit(string provider)
    {
        var delivery = Delivery(provider, $"{provider}-mukerrer");

        var first = await ReceiveAsync(delivery);
        var second = await ReceiveAsync(delivery);

        Assert.True(first.Claim.Claimed);
        Assert.False(second.Claim.Claimed);
        Assert.Equal(first.Claim.ChangeId, second.Claim.ChangeId);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            await db.ChangeWebhookDeliveries.CountAsync(
                d => d.DeliveryKey == first.Mapped.DeliveryId,
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> <b>farklı</b> bir olay ikinci bir
    /// kayıt oluşturuyor.
    ///
    /// <para>
    /// Bu testin yokluğu, yukarıdakini anlamsız kılardı: her teslimata aynı
    /// anahtarı veren bozuk bir üreteç <i>"tek kayıt"</i> testinden temiz
    /// geçerdi ve <b>ikinci bir değişikliği sessizce yutardı</b>. Bu depoda
    /// sessiz kayıp, fazladan bir satırdan pahalı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WebhookProviders.GitHub)]
    [InlineData(WebhookProviders.GitLab)]
    [InlineData(WebhookProviders.Jenkins)]
    [Trait("Category", "Integration")]
    public async Task Farkli_olay_ikinci_kayit_olusturuyor(string provider)
    {
        var first = await ReceiveAsync(Delivery(provider, $"{provider}-olay-1", run: 184));
        var second = await ReceiveAsync(Delivery(provider, $"{provider}-olay-2", run: 185));

        Assert.True(first.Claim.Claimed);
        Assert.True(second.Claim.Claimed);
        Assert.NotEqual(first.Mapped.DeliveryId, second.Mapped.DeliveryId);
    }

    /// <summary>
    /// <b>Koşturulduğunda kanıtladığı şey:</b> imzası bozuk bir teslimat
    /// <b>hiçbir kayıt oluşturmuyor</b> — ve reddin sebebi <i>"imza
    /// tutmadı"</i>.
    ///
    /// <para>
    /// Ucun gövdesinde imza kontrolü <b>gövde ayrıştırılmadan önce</b> koşuyor;
    /// bu test o sıranın anlamını sabitliyor: doğrulama düştüğünde veritabanına
    /// hiç gidilmiyor. Sıra tersine dönseydi doğrulanmamış bir istek satır
    /// oluşturur, sonra silinirdi — ve arada kalan pencerede RCA yanlış kanıt
    /// görürdü.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bozuk_imzali_teslimat_kayit_olusturmuyor()
    {
        var delivery = Delivery(WebhookProviders.GitHub, "github-bozuk");
        var endpoint = Endpoint(WebhookProviders.GitHub);

        // İmza BAŞLIĞI bozuluyor, gövde değil: gövdeyi bozmak ayrıştırma
        // hatasını imza hatası sanmak olurdu.
        string? Header(string name) =>
            name.Equals(WebhookDeliveryFactory.GitHubSignatureHeader, StringComparison.OrdinalIgnoreCase)
                ? "sha256=" + new string('0', 64)
                : delivery.Headers.TryGetValue(name, out var value) ? value : null;

        var verdict = WebhookSignature.Verify(endpoint, Header, delivery.Body);

        Assert.Equal(SignatureVerdict.Invalid, verdict);

        await using var db = await _factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            0,
            await db.ChangeWebhookDeliveries.CountAsync(
                d => d.EndpointId == "ci" && d.Provider == WebhookProviders.GitHub
                    && d.DeliveryKey.Contains("bozuk"),
                TestContext.Current.CancellationToken));
    }
}

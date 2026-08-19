using System.Text;
using System.Text.Json;
using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Üç sağlayıcının <b>gerçek</b> gövdeleri doğru eşleniyor mu (T24 kabul
/// kriteri).
///
/// <para>
/// Gövdeler <c>Fixtures/webhooks</c> altında dosya olarak duruyor. Uydurma bir
/// JSON eşlemeyi kendi varsayımımıza göre sınardı; buradaki alan adları ve iç
/// içe geçmeler sağlayıcıların yayımladığı yükün kendisi.
/// </para>
///
/// <para>
/// <b>Testlerin yarısı "eşlenmedi"yi sınıyor</b> ve bu kasıtlı: her sağlayıcı
/// aynı işi birden çok kez bildiriyor (GitHub <c>in_progress</c>, Jenkins
/// <c>STARTED</c>, GitLab <c>running</c>). Filtre sessizce gevşerse
/// <c>change_events</c> RCA kanıtı olmaktan çıkıp CI gürültüsüne döner ve bunu
/// hiçbir şey haber vermez.
/// </para>
/// </summary>
public sealed class ChangeWebhookMappingTests
{
    // Sabit saat: "şimdi"ye düşen alanların gerçekten oraya düştüğünü
    // görebilmek için. Duvar saatiyle ölçmek F1'in en pahalı ders başlığıydı.
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);

    private static readonly FakeTimeProvider Clock = new(Now);

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(RepositoryLayout.WebhookFixtureDirectory, name));

    private static ChangeWebhookEndpoint Endpoint(string provider) => new()
    {
        Id = "ci",
        Provider = provider,
        OwnerGroup = "network/core",
        Secret = "anahtar",
        TargetKind = ChangeTargetKind.Config,
        DefaultChangeKind = "deploy",
    };

    private static Func<string, string?> Headers(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    // ---------------------------------------------------------------- GitHub

    [Fact]
    public void Github_workflow_run_tamamlandiginda_esleniyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-GitHub-Event", "workflow_run"), ("X-GitHub-Delivery", "9f2b8c10-a5d3-11f0-9e44-6b1c2d3e4f50")),
            Fixture("github-workflow-run-completed.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, result.Outcome);
        Assert.Equal("9f2b8c10-a5d3-11f0-9e44-6b1c2d3e4f50", result.DeliveryId);

        var change = result.Change!;
        Assert.Equal("network/core", change.OwnerGroup);
        Assert.Equal(ChangeTargetKind.Config, change.TargetKind);
        Assert.Equal("bizigo/network-config", change.TargetId);
        Assert.Equal("deploy", change.ChangeKind);
        Assert.Equal("esra-yildiz", change.Actor);
        Assert.Equal("deploy-firewall-config #184 → success", change.Summary);
        Assert.Equal("github", change.Source);
        Assert.Equal(
            "https://github.com/bizigo/network-config/actions/runs/12103904188",
            change.ExternalRef);

        // Zaman gövdeden geliyor, alınma anından değil.
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 9, 19, 47, TimeSpan.Zero), change.Timestamp);

        Assert.Equal("success", change.Details["conclusion"]);
        Assert.Equal("main", change.Details["head_branch"]);
        Assert.Equal("12103904188", change.Details["run_id"]);
        Assert.Equal("9b3f2c1a7d4e8f60b25c93a1de07f4c8b6a2e5d3", change.Details["head_sha"]);
    }

    [Fact]
    public void Github_workflow_run_devam_ederken_eslenmiyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-GitHub-Event", "workflow_run")),
            Fixture("github-workflow-run-in-progress.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Ignored, result.Outcome);
        Assert.Null(result.Change);
    }

    [Fact]
    public void Github_push_config_push_olarak_esleniyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "3a7c")),
            Fixture("github-push.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, result.Outcome);

        var change = result.Change!;
        Assert.Equal("config_push", change.ChangeKind);
        Assert.Equal("bizigo/network-config", change.TargetId);
        Assert.Equal("esra-yildiz", change.Actor);

        // Özet commit mesajının YALNIZCA ilk satırı: gövdedeki ikinci paragraf
        // tabloyu okunmaz yapardı.
        Assert.Equal("fw-core-01: dış ACL'e 10.20.0.0/16 eklendi", change.Summary);
        Assert.Equal("refs/heads/main", change.Details["ref"]);

        // +03:00 ofsetli damga UTC'ye çevriliyor.
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 6, 14, 2, TimeSpan.Zero), change.Timestamp);
    }

    [Fact]
    public void Github_ping_olayi_hata_degil()
    {
        // GitHub uç kaydedilirken `ping` yolluyor; 4xx alırsa webhook'u kırmızı
        // işaretliyor ve kimse gerçek olayların gelmediğini fark etmiyor.
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-GitHub-Event", "ping")),
            Encoding.UTF8.GetBytes("""{"zen":"Non-blocking is better than blocking.","hook_id":1}"""),
            Clock);

        Assert.Equal(WebhookMapOutcome.Ignored, result.Outcome);
    }

    // --------------------------------------------------------------- Jenkins

    [Fact]
    public void Jenkins_completed_fazi_esleniyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.Jenkins),
            Headers(),
            Fixture("jenkins-completed.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, result.Outcome);

        var change = result.Change!;

        // Hedef iş adı değil, dağıtımın gerçek hedefi: bir iş birden çok cihaza
        // dağıtım yapabiliyor ve RCA'da işe yarayan alan bu.
        Assert.Equal("fw-core-01", change.TargetId);
        Assert.Equal("esra.yildiz", change.Actor);
        Assert.Equal("deploy-fw-config #842 → SUCCESS", change.Summary);
        Assert.Equal("jenkins", change.Source);
        Assert.Equal("https://jenkins.bizigo.example/job/deploy-fw-config/842/", change.ExternalRef);
        Assert.Equal("origin/main", change.Details["branch"]);

        // Jenkins epoch milisaniye gönderiyor.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1786079687000), change.Timestamp);

        // Teslimat kimliği başlıktan değil gövdeden türüyor — Notification
        // Plugin bir kimlik başlığı göndermiyor.
        Assert.Equal("deploy-fw-config#842:COMPLETED", result.DeliveryId);
    }

    [Fact]
    public void Jenkins_started_fazi_eslenmiyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.Jenkins),
            Headers(),
            Fixture("jenkins-started.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Ignored, result.Outcome);
    }

    // ---------------------------------------------------------------- GitLab

    [Fact]
    public void Gitlab_biten_pipeline_esleniyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitLab),
            Headers(("X-Gitlab-Event", "Pipeline Hook"), ("X-Gitlab-Event-UUID", "d3f1a0c9-77b4-4a12-9c8e-0b5d2f6a1e33")),
            Fixture("gitlab-pipeline-success.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, result.Outcome);
        Assert.Equal("d3f1a0c9-77b4-4a12-9c8e-0b5d2f6a1e33", result.DeliveryId);

        var change = result.Change!;
        Assert.Equal("net/fw-config", change.TargetId);
        Assert.Equal("esra.yildiz", change.Actor);
        Assert.Equal("Pipeline #470118 (main) → success", change.Summary);
        Assert.Equal("gitlab", change.Source);
        Assert.Equal("https://gitlab.bizigo.example/net/fw-config/-/pipelines/470118", change.ExternalRef);

        // GitLab'ın "2026-08-18 09:21:33 UTC" biçimi hiçbir standart
        // ayrıştırıcının tanımadığı bir şey; ayrı ele alınıyor.
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 9, 21, 33, TimeSpan.Zero), change.Timestamp);
    }

    [Fact]
    public void Gitlab_kosan_pipeline_eslenmiyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitLab),
            Headers(("X-Gitlab-Event-UUID", "abc")),
            Fixture("gitlab-pipeline-running.json"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Ignored, result.Outcome);
    }

    // --------------------------------------------------------------- Generic

    [Fact]
    public void Bilinmeyen_saglayici_json_yollariyla_esleniyor()
    {
        var endpoint = Endpoint(ChangeWebhookProviders.Generic);
        endpoint.TargetKind = ChangeTargetKind.Device;
        endpoint.Mapping.TargetId = "$.data.name";
        endpoint.Mapping.ChangeKind = "$.event";
        endpoint.Mapping.Actor = "$.username";
        endpoint.Mapping.Summary = "$.data.status.label";
        endpoint.Mapping.Timestamp = "$.timestamp";
        endpoint.Mapping.ExternalRef = "$.data.url";
        endpoint.Mapping.DeliveryId = "$.request_id";
        endpoint.Mapping.Details["site"] = "$.data.site.name";
        endpoint.Mapping.Details["vendor"] = "$.data.device_type.manufacturer";
        endpoint.Mapping.Details["onceki"] = "$.snapshots.prechange.status";
        endpoint.Mapping.Details["bulunmayan"] = "$.data.yok.bu.alan";

        var result = ChangeWebhookMapper.Map(endpoint, Headers(), Fixture("generic-netbox.json"), Clock);

        Assert.Equal(WebhookMapOutcome.Mapped, result.Outcome);
        Assert.Equal("7c1d0a3e-5b64-4a9f-8c22-19d0f4b7e6aa", result.DeliveryId);

        var change = result.Change!;
        Assert.Equal(ChangeTargetKind.Device, change.TargetKind);
        Assert.Equal("sw-edge-07", change.TargetId);
        Assert.Equal("updated", change.ChangeKind);
        Assert.Equal("esra.yildiz", change.Actor);
        Assert.Equal("İstanbul-DC1", change.Details["site"]);
        Assert.Equal("MikroTik", change.Details["vendor"]);
        Assert.Equal("staged", change.Details["onceki"]);

        // Çözülemeyen yol sessizce atlanıyor — boş anahtar ClickHouse'taki
        // `details` haritasında hem yer hem okunurluk tüketirdi.
        Assert.False(change.Details.ContainsKey("bulunmayan"));
    }

    [Fact]
    public void Hedefi_cikarilamayan_govde_reddediliyor()
    {
        // "Bir şey değişti, neyin olduğu belli değil" satırı RCA'da gürültüden
        // başka bir şey değil.
        var endpoint = Endpoint(ChangeWebhookProviders.Generic);
        endpoint.Mapping.TargetId = "$.olmayan.alan";

        var result = ChangeWebhookMapper.Map(endpoint, Headers(), Fixture("generic-netbox.json"), Clock);

        Assert.Equal(WebhookMapOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Zamansiz_govde_alinma_anina_dusuyor()
    {
        var endpoint = Endpoint(ChangeWebhookProviders.Generic);
        endpoint.Mapping.TargetId = "$.data.name";

        var result = ChangeWebhookMapper.Map(endpoint, Headers(), Fixture("generic-netbox.json"), Clock);

        // Kaydı atmak alternatif değildi: zamanı şaşan bir satır, hiç olmayan
        // bir satırdan iyi.
        Assert.Equal(Now, result.Change!.Timestamp);
    }

    [Fact]
    public void JSON_olmayan_govde_reddediliyor()
    {
        var result = ChangeWebhookMapper.Map(
            Endpoint(ChangeWebhookProviders.GitHub),
            Headers(("X-GitHub-Event", "push")),
            Encoding.UTF8.GetBytes("bu bir JSON degil"),
            Clock);

        Assert.Equal(WebhookMapOutcome.Invalid, result.Outcome);
    }

    // ------------------------------------------------------------- yol dili

    [Theory]
    [InlineData("$.commits[0].id", "9b3f2c1a7d4e8f60b25c93a1de07f4c8b6a2e5d3")]
    [InlineData("commits[0].author.username", "esra-yildiz")]
    [InlineData("$.repository.id", "715489581")]
    [InlineData("$.repository.private", "true")]
    [InlineData("$.commits[9].id", null)]
    [InlineData("$.repository", null)]
    [InlineData("$.commits[-1].id", null)]
    [InlineData("$.commits[abc].id", null)]
    public void Yol_dili_dar_ve_ongorulebilir(string path, string? expected)
    {
        using var document = JsonDocument.Parse(Fixture("github-push.json"));

        Assert.Equal(expected, JsonPathReader.Read(document.RootElement, path));
    }
}

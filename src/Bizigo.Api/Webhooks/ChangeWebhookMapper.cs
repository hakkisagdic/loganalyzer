using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Api.Webhooks;

public enum WebhookMapOutcome
{
    /// <summary>Gövde bir değişiklik olayına çevrildi.</summary>
    Mapped = 0,

    /// <summary>
    /// Geçerli ama <b>ilgisiz</b> bildirim: GitHub <c>ping</c>'i, henüz bitmemiş
    /// bir pipeline, Jenkins'in <c>STARTED</c> fazı. Hata değil — 202.
    /// </summary>
    Ignored = 1,

    /// <summary>Gövde JSON değil ya da zorunlu alanı çıkarılamıyor.</summary>
    Invalid = 2,
}

/// <param name="DeliveryId">
/// Sağlayıcının verdiği teslimat kimliği. Boşsa çağıran gövdenin sha256'sına
/// düşüyor.
/// </param>
public sealed record WebhookMapResult(
    WebhookMapOutcome Outcome,
    ChangeEvent? Change,
    string DeliveryId,
    string Reason);

/// <summary>
/// Sağlayıcı yükünü <see cref="ChangeEvent"/>'e çeviren eşleme (T24).
///
/// <para>
/// <b>Neden sağlayıcı başına elle yazılmış eşleme:</b> üç sağlayıcının gövdesi
/// yalnızca alan adlarında değil <b>anlamda</b> ayrışıyor — GitHub bir
/// <c>workflow_run</c>'ı hem başlarken hem biterken yolluyor, Jenkins aynı yapıyı
/// üç fazda üç kez gönderiyor, GitLab pipeline'ı her durum geçişinde bildiriyor.
/// Genel bir yol eşlemesi bunların hepsini kaydeder ve <c>change_events</c> RCA'ya
/// yarayan bir tablo olmaktan çıkıp CI gürültüsüne dönerdi. <b>Hangi bildirimin
/// bir değişiklik sayıldığı</b> her sağlayıcıda ayrı bir karar; aşağıdaki
/// filtreler o kararlar.
/// </para>
///
/// <para>
/// Bilinmeyen sağlayıcı için genel yol eşlemesi var
/// (<see cref="GenericWebhookMapping"/>) — ama orada filtre kurma sorumluluğu
/// yapılandırmayı yazana geçiyor.
/// </para>
/// </summary>
public static class ChangeWebhookMapper
{
    public static WebhookMapResult Map(
        ChangeWebhookEndpoint endpoint,
        Func<string, string?> header,
        ReadOnlySpan<byte> body,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(clock);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return Failed("Gövde geçerli JSON değil.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed("Gövdenin kökü bir JSON nesnesi olmalı.");
            }

            return endpoint.Provider switch
            {
                ChangeWebhookProviders.GitHub => MapGitHub(endpoint, header, root, clock),
                ChangeWebhookProviders.Jenkins => MapJenkins(endpoint, root, clock),
                ChangeWebhookProviders.GitLab => MapGitLab(endpoint, header, root, clock),
                _ => MapGeneric(endpoint, root, clock),
            };
        }
    }

    // ---------------------------------------------------------------- GitHub

    /// <summary>
    /// GitHub Actions. Olay türü gövdede değil <c>X-GitHub-Event</c> başlığında;
    /// aynı alan adları farklı olaylarda farklı anlama geldiği için önce o
    /// okunuyor.
    /// </summary>
    private static WebhookMapResult MapGitHub(
        ChangeWebhookEndpoint endpoint,
        Func<string, string?> header,
        JsonElement root,
        TimeProvider clock)
    {
        var eventName = header("X-GitHub-Event")?.Trim() ?? string.Empty;
        var delivery = header("X-GitHub-Delivery")?.Trim() ?? string.Empty;
        var repository = Read(root, "$.repository.full_name");

        switch (eventName)
        {
            case "workflow_run":
            {
                // Yalnızca `completed`. `requested`/`in_progress` de aynı gövdeyi
                // taşıyor; üçünü de yazmak her koşu için üç satır demekti.
                if (!string.Equals(Read(root, "$.action"), "completed", StringComparison.Ordinal))
                {
                    return Ignored(delivery, "workflow_run yalnızca 'completed' aşamasında kaydediliyor.");
                }

                var conclusion = Read(root, "$.workflow_run.conclusion");

                var change = Build(endpoint, clock,
                    targetId: repository,
                    changeKind: endpoint.DefaultChangeKind,
                    actor: First(Read(root, "$.workflow_run.actor.login"), Read(root, "$.sender.login")),
                    summary: $"{Read(root, "$.workflow_run.name")} #{Read(root, "$.workflow_run.run_number")} → {conclusion}",
                    timestamp: Read(root, "$.workflow_run.updated_at"),
                    externalRef: Read(root, "$.workflow_run.html_url"),
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["event"] = "workflow_run",
                        ["conclusion"] = conclusion,
                        ["workflow"] = Read(root, "$.workflow_run.name"),
                        ["run_id"] = Read(root, "$.workflow_run.id"),
                        ["head_branch"] = Read(root, "$.workflow_run.head_branch"),
                        ["head_sha"] = Read(root, "$.workflow_run.head_sha"),
                    });

                return Require(change, delivery, "repository.full_name");
            }

            case "deployment_status":
            {
                var state = Read(root, "$.deployment_status.state");

                // Ara durumlar bir değişiklik değil, bir değişikliğin devamı.
                if (state is "pending" or "queued" or "in_progress" or "")
                {
                    return Ignored(delivery, $"deployment_status durumu '{state}' — nihai değil.");
                }

                var environment = Read(root, "$.deployment_status.environment");

                var change = Build(endpoint, clock,
                    targetId: repository,
                    changeKind: endpoint.DefaultChangeKind,
                    actor: First(Read(root, "$.deployment_status.creator.login"), Read(root, "$.sender.login")),
                    summary: $"{environment} dağıtımı → {state}",
                    timestamp: Read(root, "$.deployment_status.updated_at"),
                    externalRef: First(Read(root, "$.deployment_status.target_url"), Read(root, "$.deployment.url")),
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["event"] = "deployment_status",
                        ["state"] = state,
                        ["environment"] = environment,
                        ["ref"] = Read(root, "$.deployment.ref"),
                        ["sha"] = Read(root, "$.deployment.sha"),
                    });

                return Require(change, delivery, "repository.full_name");
            }

            case "push":
            {
                // Dal silme de `push` olarak geliyor ve `head_commit` null oluyor.
                if (JsonPathReader.Read(root, "$.head_commit.id") is null)
                {
                    return Ignored(delivery, "push olayında head_commit yok (dal silme).");
                }

                var change = Build(endpoint, clock,
                    targetId: repository,
                    changeKind: "config_push",
                    actor: First(Read(root, "$.pusher.name"), Read(root, "$.sender.login")),
                    summary: FirstLine(Read(root, "$.head_commit.message")),
                    timestamp: Read(root, "$.head_commit.timestamp"),
                    externalRef: First(Read(root, "$.compare"), Read(root, "$.head_commit.url")),
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["event"] = "push",
                        ["ref"] = Read(root, "$.ref"),
                        ["before"] = Read(root, "$.before"),
                        ["after"] = Read(root, "$.after"),
                    });

                return Require(change, delivery, "repository.full_name");
            }

            default:
                // `ping` dahil. GitHub uç kaydedilirken ping yolluyor ve 4xx
                // alırsa webhook'u kırmızı işaretliyor.
                return Ignored(delivery, $"Eşlenmeyen GitHub olayı: '{eventName}'.");
        }
    }

    // --------------------------------------------------------------- Jenkins

    /// <summary>
    /// Jenkins Notification Plugin gövdesi. Sağlayıcının imza standardı yok;
    /// <see cref="WebhookSignature.DefaultHeader"/> kullanılıyor.
    ///
    /// <para>
    /// Eklenti aynı yapıyı <c>STARTED</c>, <c>COMPLETED</c> ve <c>FINALIZED</c>
    /// fazlarında üç kez gönderiyor. <b>Yalnızca <c>COMPLETED</c> kabul
    /// ediliyor:</b> orada <c>status</c> dolu ve iş bitmiş oluyor.
    /// <c>FINALIZED</c> aynı bilgiyi ikinci kez taşıyor — idempotans anahtarı
    /// fazı içerdiği için ikisini de kabul etmek her koşuya iki satır yazardı.
    /// </para>
    /// </summary>
    private static WebhookMapResult MapJenkins(
        ChangeWebhookEndpoint endpoint,
        JsonElement root,
        TimeProvider clock)
    {
        var phase = Read(root, "$.build.phase");
        var job = Read(root, "$.name");
        var number = Read(root, "$.build.number");
        var delivery = $"{job}#{number}:{phase}";

        if (!string.Equals(phase, "COMPLETED", StringComparison.Ordinal))
        {
            return Ignored(delivery, $"Jenkins fazı '{phase}' — yalnızca COMPLETED kaydediliyor.");
        }

        var status = Read(root, "$.build.status");

        var change = Build(endpoint, clock,
            // Bir iş birden çok hedefe dağıtım yapabiliyor; hedef parametreden
            // geliyorsa o, yoksa işin kendi adı.
            targetId: First(Read(root, "$.build.parameters.TARGET"), job),
            changeKind: endpoint.DefaultChangeKind,
            actor: Read(root, "$.build.parameters.BUILD_USER_ID"),
            summary: $"{job} #{number} → {status}",
            // Notification Plugin zaman damgası göndermiyor. `Build` boş zamanı
            // "şimdi"ye çeviriyor — yani alınma anı. Kaydın kendisini atmaktan iyi.
            timestamp: Read(root, "$.build.timestamp"),
            externalRef: Read(root, "$.build.full_url"),
            details: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = status,
                ["phase"] = phase,
                ["job"] = job,
                ["build"] = number,
                ["branch"] = Read(root, "$.build.scm.branch"),
                ["commit"] = Read(root, "$.build.scm.commit"),
            });

        return Require(change, delivery, "name");
    }

    // ---------------------------------------------------------------- GitLab

    private static WebhookMapResult MapGitLab(
        ChangeWebhookEndpoint endpoint,
        Func<string, string?> header,
        JsonElement root,
        TimeProvider clock)
    {
        var delivery = header("X-Gitlab-Event-UUID")?.Trim() ?? string.Empty;
        var project = Read(root, "$.project.path_with_namespace");
        var kind = Read(root, "$.object_kind");

        switch (kind)
        {
            case "pipeline":
            {
                var status = Read(root, "$.object_attributes.status");

                // GitLab her durum geçişini bildiriyor; yalnızca biten koşular.
                if (status is not ("success" or "failed"))
                {
                    return Ignored(delivery, $"Pipeline durumu '{status}' — nihai değil.");
                }

                var change = Build(endpoint, clock,
                    targetId: project,
                    changeKind: endpoint.DefaultChangeKind,
                    actor: Read(root, "$.user.username"),
                    summary: $"Pipeline #{Read(root, "$.object_attributes.id")} ({Read(root, "$.object_attributes.ref")}) → {status}",
                    timestamp: Read(root, "$.object_attributes.finished_at"),
                    externalRef: First(Read(root, "$.object_attributes.url"), Read(root, "$.project.web_url")),
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["object_kind"] = kind,
                        ["status"] = status,
                        ["ref"] = Read(root, "$.object_attributes.ref"),
                        ["sha"] = Read(root, "$.object_attributes.sha"),
                        ["pipeline_id"] = Read(root, "$.object_attributes.id"),
                    });

                return Require(
                    change,
                    Fallback(delivery, $"pipeline:{Read(root, "$.object_attributes.id")}:{status}"),
                    "project.path_with_namespace");
            }

            case "deployment":
            {
                var status = Read(root, "$.status");

                if (status is not ("success" or "failed"))
                {
                    return Ignored(delivery, $"Dağıtım durumu '{status}' — nihai değil.");
                }

                var environment = Read(root, "$.environment");

                var change = Build(endpoint, clock,
                    targetId: project,
                    changeKind: endpoint.DefaultChangeKind,
                    actor: Read(root, "$.user.username"),
                    summary: $"{environment} dağıtımı → {status}",
                    timestamp: Read(root, "$.status_changed_at"),
                    externalRef: First(Read(root, "$.deployable_url"), Read(root, "$.project.web_url")),
                    details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["object_kind"] = kind,
                        ["status"] = status,
                        ["environment"] = environment,
                        ["ref"] = Read(root, "$.ref"),
                        ["commit_url"] = Read(root, "$.commit_url"),
                    });

                return Require(
                    change,
                    Fallback(delivery, $"deployment:{Read(root, "$.deployment_id")}:{status}"),
                    "project.path_with_namespace");
            }

            default:
                return Ignored(delivery, $"Eşlenmeyen GitLab olayı: '{kind}'.");
        }
    }

    // --------------------------------------------------------------- Generic

    private static WebhookMapResult MapGeneric(
        ChangeWebhookEndpoint endpoint,
        JsonElement root,
        TimeProvider clock)
    {
        var mapping = endpoint.Mapping;
        var details = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, path) in mapping.Details)
        {
            var value = JsonPathReader.Read(root, path);

            if (!string.IsNullOrEmpty(value))
            {
                details[key] = value;
            }
        }

        var change = Build(endpoint, clock,
            targetId: Read(root, mapping.TargetId),
            changeKind: First(Read(root, mapping.ChangeKind), endpoint.DefaultChangeKind),
            actor: Read(root, mapping.Actor),
            summary: Read(root, mapping.Summary),
            timestamp: Read(root, mapping.Timestamp),
            externalRef: Read(root, mapping.ExternalRef),
            details: details);

        return Require(change, Read(root, mapping.DeliveryId), "Mapping:TargetId");
    }

    // ------------------------------------------------------------- yardımcı

    private static ChangeEvent Build(
        ChangeWebhookEndpoint endpoint,
        TimeProvider clock,
        string targetId,
        string changeKind,
        string actor,
        string summary,
        string timestamp,
        string externalRef,
        Dictionary<string, string> details) => new()
        {
            ChangeId = Guid.CreateVersion7(),
            Timestamp = ParseTimestamp(timestamp) ?? clock.GetUtcNow(),
            OwnerGroup = endpoint.OwnerGroup,
            TargetKind = endpoint.TargetKind,
            TargetId = targetId,
            ChangeKind = string.IsNullOrWhiteSpace(changeKind) ? endpoint.DefaultChangeKind : changeKind,
            Actor = actor,
            Summary = summary,
            // Boş değerler taşınmıyor: ClickHouse'ta `details` bir Map ve boş
            // anahtarlar hem yeri hem okunurluğu tüketiyor.
            Details = details.Where(p => !string.IsNullOrEmpty(p.Value))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Source = endpoint.Provider,
            ExternalRef = externalRef,
        };

    /// <summary>
    /// Hedefsiz kayıt RCA'da işe yaramaz — "bir şey değişti, neyin olduğu belli
    /// değil" satırı gürültüden başka bir şey değil. Bu yüzden eksik
    /// <c>target_id</c> sessizce geçilmiyor, istek 400 alıyor.
    /// </summary>
    private static WebhookMapResult Require(ChangeEvent change, string delivery, string field) =>
        string.IsNullOrWhiteSpace(change.TargetId)
            ? Failed($"Hedef kimliği çıkarılamadı ('{field}' boş).")
            : new WebhookMapResult(WebhookMapOutcome.Mapped, change, delivery, string.Empty);

    private static WebhookMapResult Ignored(string delivery, string reason) =>
        new(WebhookMapOutcome.Ignored, null, delivery, reason);

    private static WebhookMapResult Failed(string reason) =>
        new(WebhookMapOutcome.Invalid, null, string.Empty, reason);

    private static string Read(JsonElement root, string? path) =>
        JsonPathReader.Read(root, path) ?? string.Empty;

    private static string First(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    private static string Fallback(string preferred, string derived) =>
        string.IsNullOrEmpty(preferred) ? derived : preferred;

    private static string FirstLine(string text)
    {
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    /// <summary>
    /// Üç sağlayıcı üç ayrı biçim kullanıyor ve hiçbiri diğerinin ayrıştırıcısına
    /// uymuyor: GitHub ISO-8601, Jenkins epoch milisaniye, GitLab
    /// <c>"2026-08-18 10:00:00 UTC"</c>.
    ///
    /// <para>
    /// Çözülemeyen zaman <see langword="null"/> dönüyor ve çağıran alınma anına
    /// düşüyor. Kaydı atmak alternatif değildi: T24'ün amacı tablonun dolması ve
    /// zamanı birkaç saniye şaşan bir satır, hiç olmayan bir satırdan iyi.
    /// </para>
    /// </summary>
    internal static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var epochMillis))
        {
            // Jenkins `build.timestamp` epoch milisaniye. Aralık kontrolü
            // saniye/milisaniye karışıklığını yakalıyor.
            return epochMillis is > 946_684_800_000 and < 4_102_444_800_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epochMillis)
                : null;
        }

        // GitLab'ın " UTC" son eki hiçbir standart ayrıştırıcının tanımadığı bir
        // biçim; Z'ye çevirmek yerine atılıyor ve evrensel varsayımı aşağıdaki
        // stil bayrağı sağlıyor.
        if (text.EndsWith(" UTC", StringComparison.Ordinal))
        {
            text = text[..^4];
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : null;
    }
}

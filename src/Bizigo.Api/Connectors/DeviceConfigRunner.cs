using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Devices;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Connectors;

/// <summary>
/// Cihaz config connector'ının gizli <b>olmayan</b> yapılandırması —
/// <c>change_connectors.config_json</c>'un cihaz tarafındaki şekli.
/// </summary>
public sealed record DeviceConnectorConfig
{
    /// <summary>F1 kataloğundaki parser kimliğiyle aynı: <c>fortinet.fortigate</c>.</summary>
    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary><c>Password</c> ya da <c>PrivateKey</c>.</summary>
    [JsonPropertyName("authMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<DeviceAuthMode>))]
    public DeviceAuthMode AuthMode { get; init; } = DeviceAuthMode.Password;

    /// <summary>
    /// Değişikliğin yazılacağı hedef kimliği. Boşsa <see cref="Host"/>
    /// kullanılıyor; envanterdeki <c>source_id</c> verilirse RCA değişikliği
    /// olaylarla aynı kimlik üzerinden eşleştirebiliyor.
    /// </summary>
    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Cihazdan config çekip farkı <c>change_events</c>'e yazan toplayıcı (T26).
///
/// <para>
/// <b>T25'in kapısını açan parça burası.</b> O ticket "toplayıcısı olmayan tip
/// etkinleştirilemiyor" kuralını koymuştu ve <c>DeviceConfig</c> connector'ı
/// kaydedilebiliyor ama açılamıyordu. Bu sınıf <c>IChangeConnectorRunner</c>
/// olarak kaydedildiği an kapı kendiliğinden açılıyor — bir test bunu sınıyor,
/// yoksa kapının açıldığını kimse görmez.
/// </para>
///
/// <para>
/// <b>Yazma kapsam kapısından geçiyor.</b> Kapsam connector'ın kendi
/// yapılandırmasından (<c>ConnectorContext.Scope</c>) geliyor, sistem kapsamı
/// değil: cihaz kimlik bilgisi ele geçse bile yazılabilecek tek yer o
/// connector'ın grubu.
/// </para>
/// </summary>
public sealed class DeviceConfigRunner(
    DeviceConfigService devices,
    IDbContextFactory<ControlPlaneDbContext> factory,
    IScopedQuery query,
    SecretProtector protector,
    TimeProvider clock,
    ILogger<DeviceConfigRunner> log) : IChangeConnectorRunner
{
    public ChangeConnectorType ConnectorType => ChangeConnectorType.DeviceConfig;

    public async Task<ConnectorTestResult> TestAsync(
        ConnectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryTarget(context, out var target, out var error))
        {
            return new ConnectorTestResult(false, error);
        }

        var capture = await devices.CaptureAsync(target, cancellationToken);

        // Mesaj satır SAYISINI söylüyor, içeriğini değil: bağlantı testinin
        // çıktısı ekranda gösteriliyor ve config satırları sır taşıyor.
        return capture.Ok
            ? new ConnectorTestResult(true, $"{target} erişilebilir — {capture.Lines.Count} anlamlı satır okundu.")
            : new ConnectorTestResult(false, capture.Error);
    }

    public async Task<ConnectorRunResult> RunAsync(
        ConnectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryTarget(context, out var target, out var error))
        {
            return new ConnectorRunResult(false, 0, error);
        }

        var capture = await devices.CaptureAsync(target, cancellationToken);

        if (!capture.Ok)
        {
            // Erişilemeyen cihaz bir istisna DEĞİL bir sonuç: zamanlayıcı hatayı
            // kaydediyor ve çekim döngüsü yaşamaya devam ediyor (kabul kriteri).
            return new ConnectorRunResult(false, 0, capture.Error);
        }

        var body = ConfigDiff.Serialize(capture.Lines);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var previous = await db.ChangeConfigSnapshots
            .AsNoTracking()
            .Where(s => s.ConnectorId == context.Connector.Id)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Aynı özet, aynı config: satır satır karşılaştırmaya hiç girilmiyor ve
        // yeni bir anlık görüntü de yazılmıyor. Değişmeyen bir cihaz saatte bir
        // çekilse bile tabloya tek satır eklemiyor.
        if (previous is not null && string.Equals(previous.Sha256, digest, StringComparison.Ordinal))
        {
            return new ConnectorRunResult(true, 0, string.Empty);
        }

        var written = 0;

        if (previous is not null)
        {
            var diff = ConfigDiff.Compare(Decrypt(previous), capture.Lines);

            if (diff.HasChanges)
            {
                await WriteChangeAsync(context, target, diff, cancellationToken);
                written = 1;
            }
        }
        else
        {
            // İlk çekim bir DEĞİŞİKLİK değil, bir taban çizgisi. Onu değişiklik
            // saymak, her yeni connector'ın kurulduğu gün sahte bir olay
            // düşürmesi olurdu — ve RCA o günü gerçek bir olay sanardı.
            log.LogInformation(
                "Config taban çizgisi alındı: {Target} — {Lines} satır.", target, capture.Lines.Count);
        }

        db.ChangeConfigSnapshots.Add(new ChangeConfigSnapshotEntity
        {
            ConnectorId = context.Connector.Id,
            CapturedAt = clock.GetUtcNow(),
            Sha256 = digest,
            Body = protector.IsConfigured ? protector.Protect(body) : body,
            LineCount = capture.Lines.Count,
        });

        await db.SaveChangesAsync(cancellationToken);

        return new ConnectorRunResult(true, written, string.Empty);
    }

    private async Task WriteChangeAsync(
        ConnectorContext context,
        DeviceTarget target,
        ConfigDiffResult diff,
        CancellationToken cancellationToken)
    {
        var targetId = TargetId(context, target);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vendor"] = target.Vendor,
            ["host"] = target.Host,
            ["added_lines"] = diff.Added.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["removed_lines"] = diff.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Bölüm ADLARI giriyor, satır içerikleri GİRMİYOR: `details` bir
            // ClickHouse Map'i ve config satırları sır taşıyabiliyor.
            ["sections"] = string.Join(", ", diff.Sections.Take(10).Select(s => s.Section)),
        };

        await query.WriteChangeAsync(
            new ChangeEvent
            {
                ChangeId = Guid.CreateVersion7(),
                Timestamp = clock.GetUtcNow(),
                OwnerGroup = context.Connector.OwnerGroup,
                TargetKind = ChangeTargetKind.Config,
                TargetId = targetId,
                ChangeKind = "config_push",
                Actor = string.Empty,
                Summary = diff.Describe(targetId),
                Details = details,
                Source = "device",
                ExternalRef = string.Empty,
            },
            context.Scope,
            cancellationToken);
    }

    private IReadOnlyList<ConfigLine> Decrypt(ChangeConfigSnapshotEntity snapshot)
    {
        if (!protector.IsConfigured)
        {
            return ConfigDiff.Deserialize(snapshot.Body);
        }

        try
        {
            return ConfigDiff.Deserialize(protector.Unprotect(snapshot.Body));
        }
        catch (InvalidOperationException ex)
        {
            // Anahtar döndürülmüş ya da kayıt kurcalanmış. Boş taban çizgisiyle
            // devam etmek, config'in tamamını "yeni eklendi" diye raporlardı —
            // sessizce yanlış olmaktansa gürültülü şekilde atlıyoruz.
            log.LogError(ex, "Config anlık görüntüsü çözülemedi: {Connector}", snapshot.ConnectorId);
            throw;
        }
    }

    private static string TargetId(ConnectorContext context, DeviceTarget target)
    {
        var config = Parse(context.Connector.ConfigJson);

        return string.IsNullOrWhiteSpace(config?.TargetId) ? target.Host : config.TargetId;
    }

    /// <summary>
    /// Yapılandırma + çözülmüş kimlik bilgisi → bağlanılacak hedef.
    /// </summary>
    private bool TryTarget(ConnectorContext context, out DeviceTarget target, out string error)
    {
        target = null!;
        error = string.Empty;

        var config = Parse(context.Connector.ConfigJson);

        if (config is null)
        {
            error = "Cihaz yapılandırması okunamadı.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.Username))
        {
            error = "'host' ve 'username' zorunlu.";
            return false;
        }

        if (!devices.SupportedVendors.Contains(config.Vendor, StringComparer.Ordinal))
        {
            error = $"'{config.Vendor}' için toplayıcı yok. " +
                $"Desteklenenler: {string.Join(", ", devices.SupportedVendors)}.";
            return false;
        }

        if (string.IsNullOrEmpty(context.Credential))
        {
            error = "Kimlik bilgisi çözülemedi; connector kaydını yenileyin.";
            return false;
        }

        target = new DeviceTarget
        {
            Vendor = config.Vendor,
            Host = config.Host,
            Port = config.Port,
            Username = config.Username,
            Credential = context.Credential,
            AuthMode = config.AuthMode,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 300)),
        };

        return true;
    }

    private static DeviceConnectorConfig? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceConnectorConfig>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Connectors;

/// <param name="Credential">
/// Yeni kimlik bilgisi. <b>Boş bırakmak "sil" değil "değiştirme" demek</b> —
/// ekran mevcut değeri hiç görmediği için (maskeli dönüyor) her kaydetmede geri
/// göndermesi imkânsız; boşu silme saysaydık her ad düzeltmesi kimlik bilgisini
/// uçururdu.
/// </param>
public sealed record ConnectorInput
{
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ChangeConnectorType ConnectorType { get; init; } = ChangeConnectorType.Webhook;
    public string OwnerGroup { get; init; } = string.Empty;
    public JsonElement? Config { get; init; }
    public string? Credential { get; init; }
    public int? IntervalSeconds { get; init; }
    public bool Enabled { get; init; }
}

public sealed record ConnectorSaveResult(bool Ok, ChangeConnectorEntity? Connector, string Error)
{
    public static ConnectorSaveResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Connector CRUD'u, kimlik bilgisi saklama ve bağlantı testi (T25).
///
/// <para>
/// <b>Bu sınıfın en önemli işi bir kapı tutmak:</b> bir runner'dan ya da bir
/// istisnadan gelen <b>her</b> metin, veritabanına yazılmadan ve kullanıcıya
/// gösterilmeden önce <see cref="SecretRedactor"/>'dan geçiyor. Runner
/// yazarının dikkatine bırakılmadı çünkü sızıntının en sık gerçekleştiği yer
/// tam olarak bağlantı hatasının mesajı, ve orada gizli bilgi çoğu zaman
/// kimsenin yazmadığı bir yerden — kütüphanenin istisna metninden — geliyor.
/// </para>
///
/// <para>
/// Kapsam kapısı her metotta: çağıran yalnızca kendi kapsamındaki grubun
/// connector'ını görebiliyor, yazabiliyor ve deneyebiliyor (K17).
/// </para>
/// </summary>
public sealed partial class ChangeConnectorService(
    IDbContextFactory<ControlPlaneDbContext> factory,
    SecretProtector protector,
    IEnumerable<IChangeConnectorRunner> runners,
    TimeProvider clock,
    ILogger<ChangeConnectorService> log)
{
    /// <summary>
    /// Kimlik bilgisinin yerine dönen değer. Uzunluk bile sızdırılmıyor: sabit
    /// bir maske, "parola kaç karakter" sorusunu da kapatıyor.
    /// </summary>
    public const string CredentialMask = "••••••••";

    private readonly Dictionary<ChangeConnectorType, IChangeConnectorRunner> _runners =
        runners.ToDictionary(r => r.ConnectorType);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex SlugPattern { get; }

    public async Task<IReadOnlyList<ChangeConnectorEntity>> ListAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsEmpty)
        {
            return [];
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var query = db.ChangeConnectors.AsNoTracking();

        // Filtre sorguda, bellekte değil: kapsam dışı satır hiç okunmamalı.
        if (!scope.IsUnrestricted)
        {
            var groups = scope.OwnerGroups.ToArray();
            query = query.Where(c => groups.Contains(c.OwnerGroup));
        }

        return await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
    }

    public async Task<ChangeConnectorEntity?> GetAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var connector = await db.ChangeConnectors
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        // Kapsam dışı kayıt "yok" görünüyor: 403 dönmek, o kimlikte bir
        // connector bulunduğunu doğrulardı.
        return connector is not null && scope.Allows(connector.OwnerGroup) ? connector : null;
    }

    public async Task<ConnectorSaveResult> SaveAsync(
        Guid? id,
        ConnectorInput input,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        var invalid = Validate(input);

        if (invalid is not null)
        {
            return ConnectorSaveResult.Fail(invalid);
        }

        if (!scope.Allows(input.OwnerGroup))
        {
            return ConnectorSaveResult.Fail(
                $"'{input.OwnerGroup}' grubu kapsamınızda değil.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        ChangeConnectorEntity connector;

        if (id is { } existingId)
        {
            var found = await db.ChangeConnectors
                .FirstOrDefaultAsync(c => c.Id == existingId, cancellationToken);

            if (found is null || !scope.Allows(found.OwnerGroup))
            {
                return ConnectorSaveResult.Fail("Connector bulunamadı.");
            }

            connector = found;
        }
        else
        {
            connector = new ChangeConnectorEntity
            {
                Slug = input.Slug,
                Name = input.Name,
                OwnerGroup = input.OwnerGroup,
            };

            db.ChangeConnectors.Add(connector);
        }

        if (await db.ChangeConnectors
                .AnyAsync(c => c.Slug == input.Slug && c.Id != connector.Id, cancellationToken))
        {
            return ConnectorSaveResult.Fail($"'{input.Slug}' kimliği başka bir connector'da kullanılıyor.");
        }

        if (!string.IsNullOrWhiteSpace(input.Credential))
        {
            if (!protector.IsConfigured)
            {
                // Anahtar yoksa düz metne DÜŞMÜYORUZ. "Şifreli saklanıyor"
                // iddiasının sessizce yanlışlanacağı tek yer burasıydı.
                return ConnectorSaveResult.Fail(
                    "Security:SecretKey tanımlı değil; kimlik bilgisi şifrelenemediği için kaydedilmedi.");
            }

            connector.CredentialCipher = protector.Protect(input.Credential);
        }

        if (input.Enabled)
        {
            var blocked = WhyCannotEnable(input.ConnectorType, connector.CredentialCipher);

            if (blocked is not null)
            {
                return ConnectorSaveResult.Fail(blocked);
            }
        }

        connector.Slug = input.Slug;
        connector.Name = input.Name.Trim();
        connector.ConnectorType = input.ConnectorType;
        connector.OwnerGroup = input.OwnerGroup;
        connector.ConfigJson = input.Config?.GetRawText() ?? "{}";
        connector.IntervalSeconds = Schedulable(input.ConnectorType) ? input.IntervalSeconds : null;
        connector.Enabled = input.Enabled;
        connector.UpdatedAt = clock.GetUtcNow();

        // Pasife alınan connector zamanlayıcıdan DÜŞÜYOR; etkinleştirilen hemen
        // vadeye giriyor. Kabul kriteri "etkinleştirildiğinde zamanlayıcı
        // devralıyor" bu iki satırda.
        connector.NextRunAt = connector.Enabled && connector.IntervalSeconds is > 0
            ? clock.GetUtcNow()
            : null;

        await db.SaveChangesAsync(cancellationToken);

        return new ConnectorSaveResult(true, connector, string.Empty);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var connector = await db.ChangeConnectors
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (connector is null || !scope.Allows(connector.OwnerGroup))
        {
            return false;
        }

        // Geçmiş de gidiyor: connector silindikten sonra kime ait olduğu
        // anlaşılamayan koşu kayıtları yığılırdı.
        await db.ChangeConnectorRuns
            .Where(r => r.ConnectorId == id)
            .ExecuteDeleteAsync(cancellationToken);

        db.ChangeConnectors.Remove(connector);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<ChangeConnectorRunEntity>> RunsAsync(
        Guid id,
        AccessScope scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, scope, cancellationToken) is null)
        {
            return [];
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        return await db.ChangeConnectorRuns
            .AsNoTracking()
            .Where(r => r.ConnectorId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Bağlantı testi. Sonuç <b>kaydedilmiyor</b> — bir deneme, bir koşu değil;
    /// çalışma geçmişini denemelerle doldurmak "bu connector dün gece çalıştı
    /// mı" sorusunu cevaplanamaz yapardı.
    /// </summary>
    public async Task<ConnectorTestResult> TestAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        var connector = await GetAsync(id, scope, cancellationToken);

        if (connector is null)
        {
            return new ConnectorTestResult(false, "Connector bulunamadı.");
        }

        var context = BuildContext(connector);

        if (!_runners.TryGetValue(connector.ConnectorType, out var runner))
        {
            return new ConnectorTestResult(
                false, $"{connector.ConnectorType} tipi için bir toplayıcı kayıtlı değil.");
        }

        try
        {
            var result = await runner.TestAsync(context, cancellationToken);

            return result with { Message = Clean(result.Message, context.Credential, connector) };
        }
        catch (Exception ex)
        {
            // İstisna mesajı doğrudan dışarı VERİLMİYOR: kütüphanelerin ürettiği
            // bağlantı hataları kimlik bilgisini metnin içinde taşıyabiliyor ve
            // buradaki temizlik onların dikkatine bırakılamaz.
            log.LogWarning(
                "Connector bağlantı testi hata verdi: {Connector} ({Type})",
                connector.Slug, connector.ConnectorType);

            return new ConnectorTestResult(false, Clean(ex.Message, context.Credential, connector));
        }
    }

    /// <summary>
    /// Zamanlayıcının ve testin ortak yolu: kimlik bilgisini çözer ve
    /// connector'ın <b>kendi</b> kapsamını kurar.
    ///
    /// <para>
    /// Kapsam <c>AccessScope.System</c> DEĞİL — T24'teki webhook kararının
    /// aynısı. Bir connector'ın kimlik bilgisi ele geçse bile yazabileceği tek
    /// yer kendi grubu; sınırsız kapsam, tek bir kaydın her ekibin zaman
    /// çizelgesini kirletebilmesi demekti.
    /// </para>
    /// </summary>
    public ConnectorContext BuildContext(ChangeConnectorEntity connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        var credential = string.Empty;

        if (!string.IsNullOrEmpty(connector.CredentialCipher) && protector.IsConfigured)
        {
            try
            {
                credential = protector.Unprotect(connector.CredentialCipher);
            }
            catch (InvalidOperationException ex)
            {
                // Anahtar döndürülmüş ya da kayıt kurcalanmış. Sessizce boş
                // kimlik bilgisiyle devam etmek, arızayı "yetkisiz" hatası gibi
                // gösterirdi.
                log.LogError(ex, "Connector kimlik bilgisi çözülemedi: {Connector}", connector.Slug);
            }
        }

        return new ConnectorContext(
            connector,
            credential,
            AccessScope.ForGroups($"connector:{connector.Slug}", [connector.OwnerGroup]));
    }

    /// <summary>
    /// Dışarı çıkan her metnin geçtiği tek kapı. Kimlik bilgisinin yanı sıra
    /// yapılandırmadaki adres/kullanıcı alanları da maskeleniyor: bir bağlantı
    /// hatası çoğu zaman parolayı değil, <b>nereye</b> bağlanıldığını sızdırır.
    /// </summary>
    internal static string Clean(string? text, string credential, ChangeConnectorEntity connector) =>
        SecretRedactor.Redact(text, credential, connector.CredentialCipher);

    private static bool Schedulable(ChangeConnectorType type) =>
        type == ChangeConnectorType.DeviceConfig;

    private string? WhyCannotEnable(ChangeConnectorType type, string credentialCipher)
    {
        if (!_runners.ContainsKey(type))
        {
            // T26 gelene kadar cihaz config connector'ı buraya takılıyor. Bu
            // bilinçli: etkin ama koşamayan bir connector, her turda hata yazıp
            // çalışma geçmişini gerçek arızaların görülemeyeceği hâle getirirdi.
            return $"{type} tipi için bir toplayıcı henüz yok; connector etkinleştirilemiyor.";
        }

        if (type != ChangeConnectorType.Manual && string.IsNullOrEmpty(credentialCipher))
        {
            return "Kimlik bilgisi olmadan connector etkinleştirilemiyor.";
        }

        return null;
    }

    private static string? Validate(ConnectorInput input)
    {
        if (!SlugPattern.IsMatch(input.Slug))
        {
            // Slug bir URL parçası; serbest metin bırakmak kaçış kuralı
            // gerektiren bir yol üretirdi.
            return "'slug' yalnızca küçük harf, rakam ve tire içerebilir (2-63 karakter).";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "'name' zorunlu.";
        }

        if (string.IsNullOrWhiteSpace(input.OwnerGroup))
        {
            return "'ownerGroup' zorunlu — connector'ın kapsamı buradan geliyor.";
        }

        if (!Enum.IsDefined(input.ConnectorType))
        {
            return "Bilinmeyen connector tipi.";
        }

        if (Schedulable(input.ConnectorType))
        {
            if (input.IntervalSeconds is not > 0)
            {
                return "Cihaz config connector'ı için 'intervalSeconds' zorunlu.";
            }

            // Alt sınır cihazı korumak için: 30 saniyede bir SSH oturumu açan
            // bir toplayıcı, izlediği cihazı kendisi yorar.
            if (input.IntervalSeconds < 60)
            {
                return "'intervalSeconds' en az 60 olmalı.";
            }
        }

        if (input.ConnectorType == ChangeConnectorType.Webhook)
        {
            return ValidateWebhookConfig(input.Config);
        }

        return null;
    }

    /// <summary>
    /// Webhook yapılandırması T24'ün eşleyicisine besleniyor; burada
    /// doğrulanmayan bir alan orada sessizce varsayılana düşerdi.
    /// </summary>
    private static string? ValidateWebhookConfig(JsonElement? config)
    {
        var provider = config is { } element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("provider", out var value)
                ? value.GetString()
                : null;

        return string.IsNullOrWhiteSpace(provider)
            || !ChangeWebhookProviders.All.Contains(provider, StringComparer.Ordinal)
                ? $"'config.provider' şunlardan biri olmalı: {string.Join(", ", ChangeWebhookProviders.All)}."
                : null;
    }
}

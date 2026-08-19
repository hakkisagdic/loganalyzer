using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Alerting;

/// <summary>
/// Kanal kaydetme sonucu.
///
/// <para>
/// Adı <c>ChannelResult</c> DEĞİL: o ad <see cref="Notifications.ChannelResult"/>
/// tarafından kullanılıyor ve ikisi bambaşka şeyler — biri "kayıt yazıldı mı",
/// diğeri "mesaj teslim edildi mi". Aynı adı taşısalardı iki farklı using
/// bildiriminin hangi tipi getirdiği okuyanın dikkatine kalırdı.
/// </para>
/// </summary>
public sealed record ChannelSaveResult(bool Ok, NotificationChannelEntity? Channel, string Error)
{
    public static ChannelSaveResult Fail(string error) => new(false, null, error);

    public static ChannelSaveResult Success(NotificationChannelEntity channel) => new(true, channel, string.Empty);
}

/// <param name="Secret">
/// Webhook URL'i ya da SMTP parolası. <b>Yalnızca yazılıyor</b>; boş bırakılırsa
/// mevcut gizli bilgi korunuyor, böylece kanal adını değiştirmek için parolayı
/// yeniden girmek gerekmiyor.
/// </param>
public sealed record ChannelInput
{
    public required string Name { get; init; }
    public required NotificationChannelType ChannelType { get; init; }
    public required string OwnerGroup { get; init; }
    public ChannelSettings Settings { get; init; } = new();
    public string? Secret { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Kanal yapılandırmasının yaşam döngüsü (T22).
///
/// <para>
/// <b>Gizli bilgi bu sınıftan dışarı çıkmıyor.</b> Okuma metotları
/// <see cref="NotificationChannelEntity"/> döndürüyor ve o tipin
/// <c>SecretCipher</c> alanı şifreli — API katmanı çözemez, çünkü
/// <see cref="SecretProtector"/> orada yok. Bu bir sözleşme değil yapı: uç
/// katmanının gizli bilgiyi yanlışlıkla yanıta koyması için önce bir bağımlılık
/// eklemesi gerekir.
/// </para>
/// </summary>
public sealed class NotificationChannelService(
    IDbContextFactory<ControlPlaneDbContext> factory,
    SecretProtector protector,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ChannelSaveResult> SaveAsync(
        Guid? id,
        ChannelInput input,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return ChannelSaveResult.Fail("Kanal adı boş olamaz.");
        }

        if (string.IsNullOrWhiteSpace(input.OwnerGroup))
        {
            return ChannelSaveResult.Fail("Kanalın owner_group'u olmalı.");
        }

        if (!scope.Allows(input.OwnerGroup))
        {
            return ChannelSaveResult.Fail($"'{input.OwnerGroup}' grubu kapsamınızda değil.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        NotificationChannelEntity channel;

        if (id is { } existing)
        {
            var found = await db.NotificationChannels
                .FirstOrDefaultAsync(c => c.Id == existing, cancellationToken)
                .ConfigureAwait(false);

            if (found is null || !scope.Allows(found.OwnerGroup))
            {
                return ChannelSaveResult.Fail("Kanal bulunamadı.");
            }

            channel = found;
        }
        else
        {
            channel = new NotificationChannelEntity
            {
                Name = input.Name,
                OwnerGroup = input.OwnerGroup,
            };

            db.NotificationChannels.Add(channel);
        }

        if (!string.IsNullOrWhiteSpace(input.Secret))
        {
            if (!protector.IsConfigured)
            {
                // Anahtar yoksa düz metne DÜŞMÜYORUZ. "Şifreli saklanıyor"
                // iddiasının sessizce yanlışlanacağı tek yer burasıydı.
                return ChannelSaveResult.Fail(
                    "Alerting:SecretKey tanımlı değil; gizli bilgi şifrelenemediği için kaydedilmedi.");
            }

            channel.SecretCipher = protector.Protect(input.Secret);
        }
        else if (id is null && RequiresSecret(input.ChannelType))
        {
            return ChannelSaveResult.Fail(
                $"{input.ChannelType} kanalı için hedef adres (gizli) zorunlu.");
        }

        channel.Name = input.Name.Trim();
        channel.ChannelType = input.ChannelType;
        channel.OwnerGroup = input.OwnerGroup.Trim();
        channel.ConfigJson = input.Settings.Serialize();
        channel.Enabled = input.Enabled;
        channel.UpdatedAt = _time.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ChannelSaveResult.Success(channel);
    }

    /// <summary>Hedef adresi gizli olan kanallar; e-postada parola isteğe bağlı (açık röle).</summary>
    private static bool RequiresSecret(NotificationChannelType type) =>
        type is NotificationChannelType.Slack
            or NotificationChannelType.Teams
            or NotificationChannelType.Webhook;

    public async Task<IReadOnlyList<NotificationChannelEntity>> ListAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var all = await db.NotificationChannels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. all.Where(c => scope.Allows(c.OwnerGroup))];
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var channel = await db.NotificationChannels
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null || !scope.Allows(channel.OwnerGroup))
        {
            return false;
        }

        db.AlertRuleChannels.RemoveRange(db.AlertRuleChannels.Where(c => c.ChannelId == id));
        db.NotificationChannels.Remove(channel);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Kanala örnek bir mesaj gönderir (T22 kabul kriteri: "dört kanalın her biri
    /// gerçek bir hedefe ulaşıyor").
    ///
    /// <para>
    /// Sonuç <see cref="Notifications.ChannelResult.Error"/> alanını olduğu gibi
    /// döndürmüyor; gönderici zaten redaksiyondan geçiriyor ama burada bir kez
    /// daha geçiriliyor — API yanıtı, gizli bilginin sızabileceği en görünür yer.
    /// </para>
    /// </summary>
    public async Task<(bool Ok, string Error)> TestAsync(
        Guid id,
        IEnumerable<INotificationChannel> channels,
        AlertingOptions options,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entity = await db.NotificationChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null || !scope.Allows(entity.OwnerGroup))
        {
            return (false, "Kanal bulunamadı.");
        }

        var channel = channels.FirstOrDefault(c => c.Type == entity.ChannelType);
        if (channel is null)
        {
            return (false, $"Kanal tipi desteklenmiyor: {entity.ChannelType}.");
        }

        string secret;
        try
        {
            secret = string.IsNullOrEmpty(entity.SecretCipher)
                ? string.Empty
                : protector.Unprotect(entity.SecretCipher);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

        var now = _time.GetUtcNow();
        var settings = ChannelSettings.Parse(entity.ConfigJson);

        var message = new NotificationMessage(
            $"Sınama — {entity.Name}",
            AlertRuleType.Threshold,
            entity.OwnerGroup,
            now - TimeSpan.FromMinutes(5),
            now,
            [new NotificationLine(string.Empty, 0, 0, "Bu bir sınama bildirimidir; gerçek bir alarm değil.")],
            null);

        var result = await channel
            .SendAsync(message, new ResolvedChannel(entity, settings, secret), cancellationToken)
            .ConfigureAwait(false);

        return (result.Ok, SecretRedactor.Redact(result.Error, secret, settings.User));
    }
}

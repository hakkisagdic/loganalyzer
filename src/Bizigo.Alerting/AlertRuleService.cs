using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Alerting;

/// <param name="Ok">İşlem gerçekleşti mi.</param>
/// <param name="Rule">Sonuçtaki kayıt; başarısızsa <see langword="null"/>.</param>
public sealed record AlertRuleResult(bool Ok, AlertRuleEntity? Rule, string Error)
{
    public static AlertRuleResult Fail(string error) => new(false, null, error);

    public static AlertRuleResult Success(AlertRuleEntity rule) => new(true, rule, string.Empty);
}

/// <summary>Kural yazma/güncelleme isteğinin kontrol düzleminden bağımsız hâli.</summary>
public sealed record AlertRuleInput
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public AlertRuleType RuleType { get; init; } = AlertRuleType.Threshold;
    public required IReadOnlyList<string> OwnerGroups { get; init; }
    public AlertSearch Search { get; init; } = new();
    public int WindowSeconds { get; init; } = 300;
    public int IntervalSeconds { get; init; } = 60;
    public double Threshold { get; init; }
    public AlertComparison Comparison { get; init; } = AlertComparison.GreaterThan;
    public int SilenceSeconds { get; init; } = 900;
    public int RepeatIntervalSeconds { get; init; } = 3600;
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<Guid> ChannelIds { get; init; } = [];
}

/// <summary>
/// Kural yaşam döngüsü ve <b>kapsam kapısı</b> (T21).
///
/// <para>
/// <b>Kapsam zorlaması neden burada ve neden tek yerde:</b> alarm kuralı
/// ClickHouse'ta değil kontrol düzleminde duruyor, dolayısıyla
/// <see cref="Bizigo.Query.IScopedQuery"/> onu kendiliğinden korumuyor. K17'nin
/// dersi "kapsamı ikinci bir yere koyma" değil, "kapsamı <i>dağıtma</i>" —
/// kuralın kapsamı bu sınıfta, sadece bu sınıfta doğrulanıyor. Uçlar
/// <c>AccessScope</c>'u geçiriyor ve başka hiçbir karar vermiyor.
/// </para>
///
/// <para>
/// Değişmez: <b>bir kullanıcı, kendi kapsamı dışında bir grup için kural
/// yazamaz.</b> Yazabilseydi kapsam ayrımı tek bir POST isteğiyle delinirdi —
/// üstelik kural arka planda koştuğu için sonucu da kimse görmezdi.
/// </para>
/// </summary>
public sealed class AlertRuleService(
    IDbContextFactory<ControlPlaneDbContext> factory,
    AlertingOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// İstenen grupların kapsamla uyumu — saf fonksiyon.
    /// <see langword="null"/> dönerse geçerli.
    /// </summary>
    public static string? ValidateGroups(IReadOnlyList<string> requested, AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(scope);

        var groups = requested
            .Select(static g => g.Trim())
            .Where(static g => g.Length > 0)
            .ToArray();

        if (groups.Length == 0)
        {
            // Boş grup listesi "her şey" anlamına GELMİYOR. Boş kapsamın
            // "her şey"e düşmesi bu üründe yapılabilecek en pahalı hata (K17).
            return "Kuralın en az bir owner_group'u olmalı.";
        }

        if (groups.Any(static g => g.Contains(',', StringComparison.Ordinal)))
        {
            // Gruplar tek kolonda virgülle saklandığı için virgül taşıyan bir ad
            // kaydı bölerdi ve kural sessizce başka bir grubu da kapsardı.
            return "owner_group adı virgül içeremez.";
        }

        // Sınırsız kapsam (admin) grupları açıkça saymak zorunda: sınırsız bir
        // kural, "bir ekibin kuralı başka ekibin olaylarını saymıyor"
        // değişmezini tek satırda delerdi.
        if (scope.IsUnrestricted)
        {
            return null;
        }

        var outside = groups.Where(g => !scope.OwnerGroups.Contains(g)).ToArray();

        return outside.Length == 0
            ? null
            : $"Kapsamınız dışındaki gruplar için kural yazamazsınız: {string.Join(", ", outside)}.";
    }

    /// <summary>Sınırların ihlali — saf fonksiyon. <see langword="null"/> dönerse geçerli.</summary>
    public static string? ValidateLimits(AlertRuleInput input, AlertingOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Kural adı boş olamaz.";
        }

        if (input.IntervalSeconds < options.MinIntervalSeconds)
        {
            return $"Değerlendirme aralığı en az {options.MinIntervalSeconds} saniye olmalı.";
        }

        if (input.RuleType is AlertRuleType.Threshold or AlertRuleType.Ratio)
        {
            if (input.WindowSeconds <= 0)
            {
                return "Değerlendirme penceresi sıfırdan büyük olmalı.";
            }

            // Oran tipi iki pencere okuyor; sınır ikisinin toplamına uygulanmalı,
            // yoksa "24 saatlik sınır" oran kuralında fiilen 48 saat olurdu.
            var effective = input.RuleType == AlertRuleType.Ratio
                ? input.WindowSeconds * 2L
                : input.WindowSeconds;

            if (effective > options.MaxWindowSeconds)
            {
                return $"Değerlendirme penceresi en fazla {options.MaxWindowSeconds} saniye olabilir " +
                    "(oran tipinde taban penceresi de sayılıyor).";
            }
        }

        if (input.RuleType == AlertRuleType.Silence && input.SilenceSeconds <= 0)
        {
            return "Sessizlik eşiği sıfırdan büyük olmalı.";
        }

        return null;
    }

    public async Task<AlertRuleResult> SaveAsync(
        Guid? id,
        AlertRuleInput input,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);

        if (ValidateLimits(input, options) is { } limitError)
        {
            return AlertRuleResult.Fail(limitError);
        }

        if (ValidateGroups(input.OwnerGroups, scope) is { } scopeError)
        {
            return AlertRuleResult.Fail(scopeError);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AlertRuleEntity rule;

        if (id is { } existing)
        {
            var found = await db.AlertRules
                .FirstOrDefaultAsync(r => r.Id == existing, cancellationToken)
                .ConfigureAwait(false);

            if (found is null || !CanSee(found, scope))
            {
                // Görünmeyen bir kural için "yetkiniz yok" demek, o kuralın var
                // olduğunu söylerdi. Bulunamadı demek bilgi sızdırmıyor.
                return AlertRuleResult.Fail("Kural bulunamadı.");
            }

            rule = found;
        }
        else
        {
            rule = new AlertRuleEntity
            {
                Name = input.Name,
                OwnerSubject = scope.Subject,
                OwnerGroups = string.Empty,
            };

            db.AlertRules.Add(rule);
        }

        var now = _time.GetUtcNow();

        rule.Name = input.Name.Trim();
        rule.Description = input.Description;
        rule.RuleType = input.RuleType;
        rule.OwnerGroups = string.Join(',', input.OwnerGroups.Select(static g => g.Trim()).Where(static g => g.Length > 0));
        rule.SearchJson = AlertSearchCodec.Serialize(input.Search);
        rule.WindowSeconds = input.WindowSeconds;
        rule.IntervalSeconds = input.IntervalSeconds;
        rule.Threshold = input.Threshold;
        rule.Comparison = input.Comparison;
        rule.SilenceSeconds = input.SilenceSeconds;
        rule.RepeatIntervalSeconds = input.RepeatIntervalSeconds;
        rule.Enabled = input.Enabled;
        rule.UpdatedAt = now;

        // Kural değiştiyse bir sonraki turda koşsun: değişikliğin etkisini
        // görmek için aralık kadar beklemek, kural yazan kişiyi kör bırakır.
        rule.NextRunAt = null;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (await BindChannelsAsync(db, rule, input.ChannelIds, scope, cancellationToken).ConfigureAwait(false)
            is { } channelError)
        {
            return AlertRuleResult.Fail(channelError);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AlertRuleResult.Success(rule);
    }

    /// <summary>
    /// Kanal bağlantıları. Kanal da kapsamlı: başka ekibin kanalına alarm
    /// yollamak, o ekibe görmemesi gereken kaynak adlarını göndermek demek.
    /// </summary>
    private static async Task<string?> BindChannelsAsync(
        ControlPlaneDbContext db,
        AlertRuleEntity rule,
        IReadOnlyList<Guid> channelIds,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var existing = await db.AlertRuleChannels
            .Where(c => c.RuleId == rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        db.AlertRuleChannels.RemoveRange(existing);

        if (channelIds.Count == 0)
        {
            return null;
        }

        var channels = await db.NotificationChannels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var missing = channelIds.Where(id => channels.TrueForAll(c => c.Id != id)).ToArray();
        if (missing.Length > 0)
        {
            return $"Kanal bulunamadı: {string.Join(", ", missing)}.";
        }

        var forbidden = channels
            .Where(c => !scope.Allows(c.OwnerGroup))
            .Select(c => c.Name)
            .ToArray();

        if (forbidden.Length > 0)
        {
            return $"Kapsamınız dışındaki kanallara bağlanamazsınız: {string.Join(", ", forbidden)}.";
        }

        foreach (var channel in channels)
        {
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity { RuleId = rule.Id, ChannelId = channel.Id });
        }

        return null;
    }

    public async Task<IReadOnlyList<AlertRuleEntity>> ListAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var all = await db.AlertRules
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filtre bellekte: kural sayısı onlarca-yüzlerce ve gruplar tek kolonda
        // virgülle duruyor, yani SQL tarafında yapılabilecek şey `LIKE` olurdu —
        // `core` araması `core-edge`'i de yakalayan bir filtre. Önemli olan
        // filtrenin BU sınıfta olması, uç katmanında değil.
        return [.. all.Where(r => CanSee(r, scope))];
    }

    public async Task<AlertRuleEntity?> GetAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rule = await db.AlertRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return rule is not null && CanSee(rule, scope) ? rule : null;
    }

    /// <summary>
    /// Kuralın bağlı olduğu kanallar.
    ///
    /// <para>
    /// Liste ucunda <b>bilerek</b> yok, yalnızca tek kural okunurken var: kural
    /// başına ayrı sorgu, elli kurallık bir listede elli sorgu demekti. Tek
    /// kural okunurken gerekiyor çünkü düzenleme formu bağlantıları geri
    /// yazmak zorunda — göstermeyen bir form, kaydettiği anda kanalları siler.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetChannelIdsAsync(
        Guid ruleId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.RuleId == ruleId)
            .Select(c => c.ChannelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rule = await db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (rule is null || !CanSee(rule, scope))
        {
            return false;
        }

        // Tetiklenme geçmişi KALIYOR. Kuralı silmek, o kuralın geçmişte
        // haklı olduğunu da silmek olmamalı — olay incelemesi çoğunlukla
        // kuralın silinmesinden sonra yapılıyor.
        db.AlertRuleChannels.RemoveRange(db.AlertRuleChannels.Where(c => c.RuleId == id));
        db.AlertRules.Remove(rule);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Kural bu kapsamdan görülebilir mi.
    ///
    /// <para>
    /// <b>Tüm</b> grupları kapsamda olmalı, kesişim yetmiyor: yarısı görünen bir
    /// kural, kullanıcıya kendi göremediği veriyle hesaplanmış bir sayı
    /// gösterirdi.
    /// </para>
    /// </summary>
    public static bool CanSee(AlertRuleEntity rule, AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsUnrestricted)
        {
            return true;
        }

        var groups = rule.OwnerGroups
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return groups.Length > 0 && Array.TrueForAll(groups, scope.OwnerGroups.Contains);
    }
}

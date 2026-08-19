using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.ControlPlane;

namespace Bizigo.Api;

/// <param name="Secret">
/// Webhook URL'i ya da SMTP parolası. <b>Yalnızca yazılıyor</b> — hiçbir yanıt
/// bu alanı geri döndürmüyor. Güncellemede boş bırakılırsa mevcut değer korunuyor.
/// </param>
public sealed record NotificationChannelRequest
{
    public required string Name { get; init; }

    /// <summary><c>slack</c> | <c>teams</c> | <c>email</c> | <c>webhook</c>.</summary>
    public required string ChannelType { get; init; }

    public required string OwnerGroup { get; init; }
    public string? Secret { get; init; }
    public bool Enabled { get; init; } = true;

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 25;
    public string From { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public string User { get; init; } = string.Empty;
    public bool UseStartTls { get; init; } = true;
}

/// <summary>
/// Bildirim kanalı yönetimi (T22).
///
/// <para>
/// <b>Uçlar <c>admin</c> istiyor</b>, kural uçlarından bir kademe yukarısı.
/// Sebebi kanalın gizli bilgi taşıması değil sadece: kanal, ürünün dışarıya
/// veri gönderdiği yer. Yanlış bir webhook adresi, alarm mesajlarının — yani
/// hangi cihazın ne zaman sustuğunun — üçüncü bir tarafa akması demek.
/// </para>
///
/// <para>
/// <b>Bu dosyada gizli bilgiyi çözebilen hiçbir şey yok.</b> Yanıtlar
/// <c>secret_set</c> boolean'ı taşıyor, şifreli metni bile değil: şifreli metin
/// de bir bilgi (uzunluğu ipucu verir) ve zaten hiçbir istemcinin işine yaramaz.
/// </para>
/// </summary>
public static class NotificationChannelEndpoints
{
    public static IEndpointRouteBuilder MapNotificationChannels(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/alerts/channels")
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithTags("alerts");

        group.MapGet("/", ListAsync).WithName("ListNotificationChannels");
        group.MapPost("/", CreateAsync).WithName("CreateNotificationChannel");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateNotificationChannel");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteNotificationChannel");
        group.MapPost("/{id:guid}/test", TestAsync).WithName("TestNotificationChannel");

        return routes;
    }

    private static async Task<IResult> ListAsync(
        NotificationChannelService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var channels = await service.ListAsync(user.Scope, cancellationToken);
        return Results.Ok(new { count = channels.Count, channels = channels.Select(Describe).ToArray() });
    }

    private static Task<IResult> CreateAsync(
        NotificationChannelRequest request,
        NotificationChannelService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(null, request, service, user, cancellationToken);

    private static Task<IResult> UpdateAsync(
        Guid id,
        NotificationChannelRequest request,
        NotificationChannelService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(id, request, service, user, cancellationToken);

    private static async Task<IResult> SaveAsync(
        Guid? id,
        NotificationChannelRequest request,
        NotificationChannelService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        NotificationChannelType type;

        try
        {
            type = ParseType(request.ChannelType);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var result = await service.SaveAsync(
            id,
            new ChannelInput
            {
                Name = request.Name,
                ChannelType = type,
                OwnerGroup = request.OwnerGroup,
                Secret = request.Secret,
                Enabled = request.Enabled,
                Settings = new ChannelSettings
                {
                    Headers = request.Headers,
                    Host = request.Host,
                    Port = request.Port,
                    From = request.From,
                    To = request.To,
                    User = request.User,
                    UseStartTls = request.UseStartTls,
                },
            },
            user.Scope,
            cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result.Channel!))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        NotificationChannelService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        await service.DeleteAsync(id, user.Scope, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> TestAsync(
        Guid id,
        NotificationChannelService service,
        IEnumerable<INotificationChannel> channels,
        AlertingOptions options,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var (ok, error) = await service.TestAsync(id, channels, options, user.Scope, cancellationToken);

        // Hata metni servis tarafında redaksiyondan geçti; burada bir daha
        // dokunulmuyor ki "redaksiyon nerede" sorusunun tek bir cevabı olsun.
        return ok ? Results.Ok(new { ok = true }) : Results.UnprocessableEntity(new { ok = false, error });
    }

    private static NotificationChannelType ParseType(string value) => value?.ToLowerInvariant() switch
    {
        "slack" => NotificationChannelType.Slack,
        "teams" => NotificationChannelType.Teams,
        "email" or "eposta" => NotificationChannelType.Email,
        "webhook" => NotificationChannelType.Webhook,
        _ => throw new ArgumentException(
            $"Bilinmeyen kanal tipi: '{value}'. Beklenen: slack, teams, email, webhook.", nameof(value)),
    };

    /// <summary>
    /// Yanıt gövdesi. <c>ConfigJson</c> olduğu gibi dönebiliyor, çünkü içine
    /// gizli bilgi girmesi <b>yapısal olarak</b> mümkün değil — gizli olan tek
    /// alan ayrı bir kolonda ve burada yalnızca "dolu mu" olarak görünüyor.
    /// </summary>
    private static object Describe(NotificationChannelEntity channel)
    {
        var settings = ChannelSettings.Parse(channel.ConfigJson);

        return new
        {
            id = channel.Id,
            name = channel.Name,
            channel_type = channel.ChannelType.ToString().ToLowerInvariant(),
            owner_group = channel.OwnerGroup,
            enabled = channel.Enabled,
            secret_set = !string.IsNullOrEmpty(channel.SecretCipher),
            settings = new
            {
                headers = settings.Headers,
                host = settings.Host,
                port = settings.Port,
                from = settings.From,
                to = settings.To,
                user = settings.User,
                use_start_tls = settings.UseStartTls,
            },
            created_at = channel.CreatedAt,
            updated_at = channel.UpdatedAt,
        };
    }
}

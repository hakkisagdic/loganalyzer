using System.Text.Json;
using Bizigo.ControlPlane;

namespace Bizigo.Api.Connectors;

public sealed record ConnectorRequest
{
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ConnectorType { get; init; } = nameof(ChangeConnectorType.Webhook);
    public string OwnerGroup { get; init; } = string.Empty;
    public JsonElement? Config { get; init; }

    /// <summary>
    /// Yeni kimlik bilgisi. Boş bırakmak "değiştirme" demek — ekran mevcut
    /// değeri hiç görmediği için geri gönderemiyor.
    /// </summary>
    public string? Credential { get; init; }

    public int? IntervalSeconds { get; init; }
    public bool Enabled { get; init; }
}

/// <param name="CredentialSet">
/// Kimlik bilgisinin <b>varlığı</b>. Değeri, şifreli hâli ve uzunluğu bu
/// sözleşmede bilerek YOK — şifreli metni döndürmek de sızıntıdır, anahtarı
/// bir gün ele geçiren için offline çözülecek bir hedef bırakır.
/// </param>
/// <param name="Credential">Sabit maske. Uzunluk bile bilgi taşımıyor.</param>
/// <param name="ReceivePath">
/// Webhook connector'ının CI tarafına yazılacak adresi; diğer tiplerde
/// <see langword="null"/>. Sunucudan geliyor çünkü ekranın kendi kurması, yol
/// değiştiğinde iki yerde düzeltme demekti.
/// </param>
public sealed record ConnectorView(
    Guid Id,
    string Slug,
    string Name,
    string ConnectorType,
    string OwnerGroup,
    JsonElement Config,
    bool CredentialSet,
    string Credential,
    int? IntervalSeconds,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastRunState,
    string LastError,
    string? ReceivePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConnectorListResponse(int Count, IReadOnlyList<ConnectorView> Connectors);

public sealed record ConnectorTestResponse(bool Ok, string Message);

public sealed record ConnectorRunView(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string State,
    int ChangesWritten,
    string Error);

public sealed record ConnectorRunListResponse(int Count, IReadOnlyList<ConnectorRunView> Runs);

/// <summary>
/// Connector yönetim uçları (T25, K34).
///
/// <para>
/// <b>Yanıtta kimlik bilgisi hiç yok.</b> Ne düz metin, ne şifreli metin, ne
/// uzunluk: yalnızca <c>credential_set</c> boolean'ı ve sabit bir maske. Şifreli
/// metni döndürmek de sızıntıdır — anahtarı bir gün ele geçiren için offline
/// çözülecek bir hedef bırakır.
/// </para>
///
/// <para>
/// Yetki <c>author</c>: connector tanımlamak boru hattının davranışını
/// değiştiriyor ve okuma yetkisinden ayrı tutulması gerekiyor. Okuma da
/// <c>author</c> — bir connector'ın varlığı, hedefi ve zamanlaması tek başına
/// altyapı bilgisi.
/// </para>
/// </summary>
public static class ChangeConnectorEndpoints
{
    public static IEndpointRouteBuilder MapChangeConnectors(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/changes/connectors").WithTags("changes");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("ListChangeConnectors")
            .Produces<ConnectorListResponse>();

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("GetChangeConnector")
            .Produces<ConnectorView>();

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateChangeConnector")
            .Produces<ConnectorView>(StatusCodes.Status201Created);

        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("UpdateChangeConnector")
            .Produces<ConnectorView>();

        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            // 204: gövde YOK. Yanıt tipi bildirilmemesi bir eksiklik değil —
            // uydurulmuş bir gövde tipi, olmayan bir sözleşme vaat ederdi.
            .WithName("DeleteChangeConnector")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/{id:guid}/test", TestAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("TestChangeConnector")
            .Produces<ConnectorTestResponse>();

        group.MapGet("/{id:guid}/runs", RunsAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("ListChangeConnectorRuns")
            .Produces<ConnectorRunListResponse>();

        return routes;
    }

    private static async Task<IResult> ListAsync(
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var rows = await service.ListAsync(user.Scope, cancellationToken);

        return Results.Ok(new ConnectorListResponse(rows.Count, [.. rows.Select(Describe)]));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var connector = await service.GetAsync(id, user.Scope, cancellationToken);

        return connector is null ? Results.NotFound() : Results.Ok(Describe(connector));
    }

    private static Task<IResult> CreateAsync(
        ConnectorRequest request,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(null, request, service, user, cancellationToken);

    private static Task<IResult> UpdateAsync(
        Guid id,
        ConnectorRequest request,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(id, request, service, user, cancellationToken);

    private static async Task<IResult> SaveAsync(
        Guid? id,
        ConnectorRequest request,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ChangeConnectorType>(request.ConnectorType, ignoreCase: true, out var type))
        {
            return Results.BadRequest(new
            {
                error = $"Bilinmeyen connector tipi: '{request.ConnectorType}'.",
                allowed = Enum.GetNames<ChangeConnectorType>(),
            });
        }

        var result = await service.SaveAsync(
            id,
            new ConnectorInput
            {
                Slug = request.Slug?.Trim() ?? string.Empty,
                Name = request.Name,
                ConnectorType = type,
                OwnerGroup = request.OwnerGroup,
                Config = request.Config,
                Credential = request.Credential,
                IntervalSeconds = request.IntervalSeconds,
                Enabled = request.Enabled,
            },
            user.Scope,
            cancellationToken);

        if (!result.Ok)
        {
            return Results.BadRequest(new { error = result.Error });
        }

        return id is null
            ? Results.Created($"/v1/changes/connectors/{result.Connector!.Id}", Describe(result.Connector))
            : Results.Ok(Describe(result.Connector!));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        await service.DeleteAsync(id, user.Scope, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> TestAsync(
        Guid id,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var result = await service.TestAsync(id, user.Scope, cancellationToken);

        // Başarısız test 200 dönüyor, 4xx değil: istek doğruydu, sonuç olumsuz.
        // 4xx, ekranın "istek gitmedi" ile "bağlanamadı"yı ayırt etmesini
        // zorlaştırırdı.
        return Results.Ok(new ConnectorTestResponse(result.Ok, result.Message));
    }

    private static async Task<IResult> RunsAsync(
        Guid id,
        int? limit,
        ChangeConnectorService service,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var runs = await service.RunsAsync(id, user.Scope, limit ?? 50, cancellationToken);

        return Results.Ok(new ConnectorRunListResponse(
            runs.Count,
            [.. runs.Select(r => new ConnectorRunView(
                r.StartedAt, r.FinishedAt, r.State.ToString(), r.ChangesWritten, r.Error))]));
    }

    /// <summary>
    /// Yanıt şekli. <b>Kimlik bilgisi burada yok</b> — ne değeri, ne şifreli
    /// hâli, ne uzunluğu; yalnızca kayıtlı olup olmadığı.
    /// </summary>
    private static ConnectorView Describe(ChangeConnectorEntity connector) => new(
        Id: connector.Id,
        Slug: connector.Slug,
        Name: connector.Name,
        ConnectorType: connector.ConnectorType.ToString(),
        OwnerGroup: connector.OwnerGroup,
        Config: JsonDocument.Parse(connector.ConfigJson).RootElement,
        CredentialSet: !string.IsNullOrEmpty(connector.CredentialCipher),
        Credential: ChangeConnectorService.CredentialMask,
        IntervalSeconds: connector.IntervalSeconds,
        Enabled: connector.Enabled,
        NextRunAt: connector.NextRunAt,
        LastRunAt: connector.LastRunAt,
        LastRunState: connector.LastRunState?.ToString(),
        LastError: connector.LastError,
        ReceivePath: connector.ConnectorType == ChangeConnectorType.Webhook
            ? $"/v1/changes/webhooks/{connector.Slug}"
            : null,
        CreatedAt: connector.CreatedAt,
        UpdatedAt: connector.UpdatedAt);
}

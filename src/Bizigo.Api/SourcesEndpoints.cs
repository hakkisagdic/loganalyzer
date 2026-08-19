using System.Globalization;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api;

public sealed record SourceUpsertRequest
{
    public required string SourceId { get; init; }
    public required string OwnerGroup { get; init; }
    public string? PeerAddress { get; init; }
    public string? Hostname { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string? ParserId { get; init; }
    public string Encoding { get; init; } = "auto";
    public string SourceClass { get; init; } = "default";
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Envanter uçları (T10; T06'dan devredildi).
///
/// <para>
/// Envanter bu üründe sıradan bir CRUD değil: <c>owner_group</c> buradan geliyor
/// ve <c>source_id → parser_id</c> bağı dispatcher'ın en hızlı kademesi. Yazma
/// bu yüzden <c>admin</c> rolüne kapalı — bir kaynağın grubunu değiştirmek, o
/// kaynağın verisini başka bir ekibe göstermek demek.
/// </para>
/// </summary>
public static class SourcesEndpoints
{
    public static IEndpointRouteBuilder MapSources(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/sources").WithTags("sources");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListSources")
            // Tüketicisi T15'in kaynak filtresi. Yazma uçları hâlâ tipsiz;
            // onların tüketicisi T17 (bkz. ProducesContractTests).
            .Produces<SourceListResponse>();

        group.MapPost("/", UpsertAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("UpsertSource");

        group.MapPost("/csv", ImportCsvAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("ImportSourcesCsv");

        return routes;
    }

    /// <summary>
    /// Envanter listesi kapsamla <b>filtreleniyor</b>: bir ekip başka bir ekibin
    /// cihaz envanterini görmemeli. Yönetici (sınırsız kapsam) hepsini görüyor.
    /// </summary>
    private static async Task<IResult> ListAsync(
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        // Kapsam filtresi burada DEĞİL, IScopedQuery içinde. Uç katmanında
        // uygulamak zorlamayı ikinci bir yere koymak olurdu (K17).
        var visible = await query.SearchSourcesAsync(scope, cancellationToken);

        return Results.Ok(new SourceListResponse(
            visible.Count,
            [.. visible.Select(SourceResponse.From)]));
    }

    private static async Task<IResult> UpsertAsync(
        SourceUpsertRequest request,
        ControlPlaneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceId) || string.IsNullOrWhiteSpace(request.OwnerGroup))
        {
            return Results.BadRequest(new { error = "'sourceId' ve 'ownerGroup' zorunlu." });
        }

        var existing = await db.Sources
            .FirstOrDefaultAsync(s => s.SourceId == request.SourceId, cancellationToken);

        var created = existing is null;

        if (existing is null)
        {
            existing = new SourceEntity
            {
                SourceId = request.SourceId,
                OwnerGroup = request.OwnerGroup,
            };
            db.Sources.Add(existing);
        }

        existing.OwnerGroup = request.OwnerGroup;
        existing.PeerAddress = string.IsNullOrWhiteSpace(request.PeerAddress) ? null : request.PeerAddress;
        existing.Hostname = string.IsNullOrWhiteSpace(request.Hostname) ? null : request.Hostname;
        existing.Vendor = request.Vendor;
        existing.Product = request.Product;
        existing.ParserId = string.IsNullOrWhiteSpace(request.ParserId) ? null : request.ParserId;
        existing.Encoding = request.Encoding;
        existing.SourceClass = request.SourceClass;
        existing.Enabled = request.Enabled;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return created
            ? Results.Created($"/v1/sources/{existing.SourceId}", existing)
            : Results.Ok(existing);
    }

    /// <summary>
    /// CSV ile toplu yükleme. Başlık satırı zorunlu ve <b>tamamı ya hep ya hiç</b>:
    /// yarı yüklenmiş bir envanter, hangi cihazın hangi gruba düştüğünü belirsiz
    /// bırakır ve o belirsizlik doğrudan kapsam hatasına dönüşür.
    /// </summary>
    private static async Task<IResult> ImportCsvAsync(
        HttpRequest request,
        ControlPlaneDbContext db,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        if (lines.Length < 2)
        {
            return Results.BadRequest(new { error = "CSV en az bir başlık ve bir veri satırı içermeli." });
        }

        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var required = new[] { "source_id", "owner_group" };

        foreach (var column in required)
        {
            if (!header.Contains(column, StringComparer.Ordinal))
            {
                return Results.BadRequest(new { error = $"Zorunlu sütun eksik: '{column}'." });
            }
        }

        var errors = new List<string>();
        var parsed = new List<SourceUpsertRequest>();

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length != header.Length)
            {
                errors.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"satır {i + 1}: {header.Length} sütun bekleniyordu, {cells.Length} bulundu"));
                continue;
            }

            string Cell(string name)
            {
                var index = Array.IndexOf(header, name);
                return index < 0 ? string.Empty : cells[index].Trim();
            }

            var sourceId = Cell("source_id");
            var ownerGroup = Cell("owner_group");

            if (sourceId.Length == 0 || ownerGroup.Length == 0)
            {
                errors.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"satır {i + 1}: source_id ve owner_group boş olamaz"));
                continue;
            }

            parsed.Add(new SourceUpsertRequest
            {
                SourceId = sourceId,
                OwnerGroup = ownerGroup,
                PeerAddress = Cell("peer_address"),
                Hostname = Cell("hostname"),
                Vendor = Cell("vendor"),
                Product = Cell("product"),
                ParserId = Cell("parser_id"),
                Encoding = Cell("encoding") is { Length: > 0 } enc ? enc : "auto",
                SourceClass = Cell("source_class") is { Length: > 0 } cls ? cls : "default",
            });
        }

        if (errors.Count > 0)
        {
            // Hiçbir satır yazılmıyor. Kısmi yükleme, envanteri sessizce tutarsız
            // bırakır ve hatayı fark etmek için kimsenin bakmadığı bir yere bakmak
            // gerekir.
            return Results.BadRequest(new { error = "CSV geçersiz, hiçbir satır yazılmadı.", details = errors });
        }

        var existing = await db.Sources.ToDictionaryAsync(s => s.SourceId, cancellationToken);
        var created = 0;
        var updated = 0;

        foreach (var row in parsed)
        {
            if (existing.TryGetValue(row.SourceId, out var entity))
            {
                updated++;
            }
            else
            {
                entity = new SourceEntity { SourceId = row.SourceId, OwnerGroup = row.OwnerGroup };
                db.Sources.Add(entity);
                created++;
            }

            entity.OwnerGroup = row.OwnerGroup;
            entity.PeerAddress = string.IsNullOrWhiteSpace(row.PeerAddress) ? null : row.PeerAddress;
            entity.Hostname = string.IsNullOrWhiteSpace(row.Hostname) ? null : row.Hostname;
            entity.Vendor = row.Vendor;
            entity.Product = row.Product;
            entity.ParserId = string.IsNullOrWhiteSpace(row.ParserId) ? null : row.ParserId;
            entity.Encoding = row.Encoding;
            entity.SourceClass = row.SourceClass;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { created, updated, total = parsed.Count });
    }
}

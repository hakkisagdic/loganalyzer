using System.Globalization;
using Bizigo.Contracts;
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
            .Produces<SourceListResponse>();

        // Etkinlik ayrı bir uç, listeye gömülü değil: bu ClickHouse'a gidiyor,
        // liste ise kontrol düzlemine. T15'in kaynak filtresi listeyi her açılışta
        // çağırıyor ve o çağrının olay tablosuna dokunması için sebep yok.
        group.MapGet("/activity", ActivityAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("SourceActivity")
            .Produces<SourceActivityListResponse>();

        group.MapPost("/", UpsertAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("UpsertSource")
            .Produces<SourceResponse>()
            .Produces<SourceResponse>(StatusCodes.Status201Created);

        group.MapPost("/csv", ImportCsvAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("ImportSourcesCsv")
            .Produces<SourceCsvImportResponse>()
            .Produces<SourceCsvErrorResponse>(StatusCodes.Status400BadRequest);

        return routes;
    }

    /// <summary>Etkinlik penceresinin üst sınırı — açık uçlu sorgu yok.</summary>
    private const int MaxActivityHours = 24 * 30;

    /// <summary>
    /// Kaynak başına son görülme ve olay sayısı.
    ///
    /// <para>
    /// Sorgu <c>IScopedQuery.GetSourceActivityAsync</c>'te ve <b>T21'in sessizlik
    /// alarmıyla ortak</b>. İkinci bir kopya, iki farklı zaman kolonu ve iki
    /// farklı kapsam davranışı demek olurdu.
    /// </para>
    /// </summary>
    private static async Task<IResult> ActivityAsync(
        int? hours,
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        var window = hours ?? 24;

        if (window is < 1 or > MaxActivityHours)
        {
            return Results.BadRequest(new
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"'hours' 1 ile {MaxActivityHours} arasında olmalı."),
            });
        }

        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-window);

        var rows = await query.GetSourceActivityAsync(
            new SourceActivityWindow { From = from, To = to },
            scope,
            cancellationToken);

        return Results.Ok(new SourceActivityListResponse(
            from,
            to,
            rows.Count,
            [.. rows.Select(r => new SourceActivityResponse(
                r.SourceId, r.OwnerGroup, r.LastEventAt, r.LastIngestedAt, r.EventCount))]));
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

        // Kapsam kontrolü. Bugün bu uç yalnızca `admin` rolüne açık ve admin
        // sınırsız kapsamlı, yani pratikte no-op. Yine de var: rol tablosu bir
        // gün grup yöneticisi tanırsa, kontrol olmadan o kullanıcı başka bir
        // ekibin cihazını kendi grubuna taşıyabilirdi — ve bu, o ekibin verisini
        // görmek demek.
        if (!user.Scope.Allows(request.OwnerGroup))
        {
            return Results.Forbid();
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

        // EF varlığı değil, sözleşme tipi dönüyor: varlık şeması bir gün
        // değiştiğinde API gövdesinin sessizce değişmesi, T14'ün ürettiği
        // tiplerin tam olarak yakalayamayacağı bir kırılma olurdu.
        var body = SourceResponse.From(new SourceSummary(
            existing.SourceId, existing.OwnerGroup, existing.PeerAddress, existing.Hostname,
            existing.Vendor, existing.Product, existing.ParserId, existing.Encoding,
            existing.SourceClass, existing.Enabled,
            !string.IsNullOrWhiteSpace(existing.ParserId), existing.CreatedAt));

        return created
            ? Results.Created($"/v1/sources/{existing.SourceId}", body)
            : Results.Ok(body);
    }

    /// <summary>
    /// CSV ile toplu yükleme. Başlık satırı zorunlu ve <b>tamamı ya hep ya hiç</b>:
    /// yarı yüklenmiş bir envanter, hangi cihazın hangi gruba düştüğünü belirsiz
    /// bırakır ve o belirsizlik doğrudan kapsam hatasına dönüşür.
    /// </summary>
    private static async Task<IResult> ImportCsvAsync(
        HttpRequest request,
        ControlPlaneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var content = await reader.ReadToEndAsync(cancellationToken);

        // Ayrıştırma, doğrulama ve kapsam kontrolü `SourceCsvImport`'ta —
        // veritabanından bağımsız, dolayısıyla konteyner gerektirmeden sınanıyor.
        var result = SourceCsvImport.Parse(content, user.Scope);

        if (!result.Ok)
        {
            // Hiçbir satır yazılmıyor. Kısmi yükleme, envanteri sessizce tutarsız
            // bırakır ve hatayı fark etmek için kimsenin bakmadığı bir yere bakmak
            // gerekir.
            return Results.BadRequest(new SourceCsvErrorResponse(
                "CSV geçersiz, hiçbir satır yazılmadı.",
                result.Errors));
        }

        var parsed = result.Rows;
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

        return Results.Ok(new SourceCsvImportResponse(created, updated, parsed.Count));
    }
}

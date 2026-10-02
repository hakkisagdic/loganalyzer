using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Api;

/// <summary>The seven public topology reads use the same scoped query gate.</summary>
public static partial class TopologyReadEndpoints
{
    private const int MaximumPageSize = 200;
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\A(?<second>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestamp();

    public static IEndpointRouteBuilder MapTopologyReads(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/topology")
            .RequireAuthorization(BizigoAuthPolicies.Read).WithTags("topology-read");

        static void Errors(RouteHandlerBuilder route) => route.Produces<TopologyProblemDto>(400)
            .Produces<TopologyProblemDto>(409).Produces<TopologyProblemDto>(422)
            .Produces<TopologyProblemDto>(503).Produces<TopologyProblemDto>(504)
            .Produces(401).Produces(403);

        var nodes = group.MapGet("/nodes", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "nodes"));
        Errors(nodes); nodes.WithName("ListTopologyNodes").Produces<TopologyNodePageDto>();

        var node = group.MapGet("/nodes/{nodeId}", (string nodeId, HttpContext http, IScopedQuery query,
            ICurrentUser user, TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "node", nodeId));
        Errors(node); node.WithName("GetTopologyNode").Produces<TopologyNodeDetailDto>().Produces(404);

        var edges = group.MapGet("/edges", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "edges"));
        Errors(edges); edges.WithName("ListTopologyEdges").Produces<TopologyEdgePageDto>();

        var edge = group.MapGet("/edges/{edgeId}", (string edgeId, HttpContext http, IScopedQuery query,
            ICurrentUser user, TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "edge", edgeId));
        Errors(edge); edge.WithName("GetTopologyEdge").Produces<TopologyEdgeDetailDto>().Produces(404);

        var neighbors = group.MapGet("/nodes/{nodeId}/neighbors", (string nodeId, HttpContext http, IScopedQuery query,
            ICurrentUser user, TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "neighbors", nodeId));
        Errors(neighbors); neighbors.WithName("GetTopologyNeighbors").Produces<TopologyNeighborhoodDto>().Produces(404);

        var path = group.MapGet("/path", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "path"));
        Errors(path); path.WithName("GetTopologyPath").Produces<TopologyPathDto>().Produces(404);

        var ancestors = group.MapGet("/ancestors", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence) => HandleAsync(http, query, user.Scope, fence, "ancestors"));
        Errors(ancestors); ancestors.WithName("GetTopologyAncestors").Produces<TopologyAncestorsDto>().Produces(404);
        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpContext http, IScopedQuery query, AccessScope scope,
        TopologyPublicationFence fence, string route, string? routeId = null)
    {
        if (scope.IsEmpty) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var request = Parse(http.Request.Query, route);
            return await fence.ExecuteAsync(async (_, token) =>
            {
                switch (route)
                {
                    case "nodes":
                    {
                        var page = await query.SearchTopologyNodesAsync(new(request.AsOfNano, request.Limit,
                            request.Cursor, request.Kind), scope, token);
                        return Json(new TopologyNodePageDto(page.Items.Select(Node).ToArray(), page.Cursor,
                            page.Cursor is not null, null, Number(page.PublishedSequence)));
                    }
                    case "node":
                    {
                        var found = await query.GetTopologyNodeAsync(RequiredId(routeId), request.AsOfNano, scope, token);
                        return found is null ? Results.NotFound() : Json(new TopologyNodeDetailDto(Node(found)));
                    }
                    case "edges":
                    {
                        var page = await query.SearchTopologyEdgesAsync(new(request.AsOfNano, request.Limit,
                            request.Cursor, request.Relation, request.Provenance), scope, token);
                        return Json(new TopologyEdgePageDto(page.Items.Select(Edge).ToArray(), page.Cursor,
                            page.Cursor is not null, null, Number(page.PublishedSequence)));
                    }
                    case "edge":
                    {
                        var found = await query.GetTopologyEdgeAsync(RequiredId(routeId), request.AsOfNano, scope, token);
                        return found is null ? Results.NotFound() : Json(new TopologyEdgeDetailDto(Edge(found.Edge),
                            found.Evidence.Select(Evidence).ToArray(), found.EvidenceCursor));
                    }
                    case "neighbors":
                    {
                        var nodeId = RequiredId(routeId);
                        if (await query.GetTopologyNodeAsync(nodeId, request.AsOfNano, scope, token) is null)
                            return Results.NotFound();
                        var result = await query.GetTopologyNeighborhoodAsync(new(nodeId, request.AsOfNano,
                            request.Limit, request.Cursor, request.Relation), scope, token);
                        return Json(new TopologyNeighborhoodDto(result.Neighbors.Select(n => new TopologyNeighborDto(
                            n.NodeId, n.EdgeId, n.Relation.ToString(), n.Provenance.ToString(), n.Direction.ToString())).ToArray(),
                            result.ExternalNeighborCount?.ToString(CultureInfo.InvariantCulture), result.ExternalNeighborReason,
                            result.Cursor, result.Cursor is not null, result.ExternalNeighborReason, Number(result.PublishedSequence)));
                    }
                    case "path":
                    {
                        var from = RequiredId(request.FromNode);
                        var to = RequiredId(request.ToNode);
                        if (await query.GetTopologyNodeAsync(from, request.AsOfNano, scope, token) is null
                            || await query.GetTopologyNodeAsync(to, request.AsOfNano, scope, token) is null)
                            return Results.NotFound();
                        var result = await query.GetTopologyPathAsync(new(from, to, request.AsOfNano,
                            request.Limit, request.Cursor), scope, token);
                        return Json(new TopologyPathDto(result.Status.ToString(), result.Nodes, result.EdgeIds,
                            result.Cursor, result.Cursor is not null, result.Reason, Number(result.PublishedSequence)));
                    }
                    case "ancestors":
                    {
                        foreach (var target in request.Targets)
                            if (await query.GetTopologyNodeAsync(target, request.AsOfNano, scope, token) is null)
                                return Results.NotFound();
                        var result = await query.GetTopologyCommonAncestorAsync(new(request.Targets, request.AsOfNano),
                            scope, token);
                        return Json(new TopologyAncestorsDto(result.Status.ToString(), result.NodeId,
                            result.Paths.Select(p => new TopologyPathProofDto(p.TargetNodeId, p.Nodes, p.EdgeIds)).ToArray(),
                            result.Status == TopologyGraphResultStatus.NotVerified, result.Reason,
                            Number(result.PublishedSequence)));
                    }
                    default: throw new InvalidOperationException("Unknown topology read route.");
                }
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Problem(504, "Timeout"); }
        catch (TimeoutException) { return Problem(504, "Timeout"); }
        catch (TopologyRestartRequiredException) { return Problem(409, "SnapshotChanged"); }
        catch (TopologySnapshotUnavailableException) { return Problem(409, "SnapshotChanged"); }
        catch (TopologyCursorException) { return Problem(400, "InvalidCursor"); }
        catch (ArgumentException) { return Problem(400, "InvalidQuery"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Problem(503, "QueryUnavailable"); }
    }

    private static IResult Json<T>(T body) => JsonSerializer.SerializeToUtf8Bytes(body, WireOptions).Length > MaximumResponseBytes
        ? Problem(422, "RecordTooLarge") : Results.Json(body, WireOptions);
    private static IResult Problem(int status, string reason) => Results.Json(new TopologyProblemDto(reason, reason),
        WireOptions, statusCode: status);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(decimal value) => value.ToString("0", CultureInfo.InvariantCulture);
    private static string RequiredId(string? id) => !string.IsNullOrWhiteSpace(id) && !id.Contains(',')
        ? id : throw new ArgumentException("Missing or invalid topology node/edge ID.");

    private static TopologyNodeDto Node(TopologyNodeProjection node) => new(node.Id, node.Kind.ToString(),
        node.DisplayName, node.OwnerGroup, node.Enabled, Number(node.Version), Number(node.ValidFromUnixNano),
        node.ValidToUnixNano is decimal end ? Number(end) : null);
    private static TopologyEdgeDto Edge(TopologyEdgeProjection edge) => new(edge.Id, edge.FromNode, edge.ToNode,
        edge.Relation.ToString(), edge.Provenance.ToString(), edge.Directed, (double)edge.Confidence,
        edge.FromOwnerGroup, edge.ToOwnerGroup, edge.Visibility.ToString(), Number(edge.FirstSeenUnixNano),
        Number(edge.LastSeenUnixNano), edge.EffectiveExpiry is decimal expiry ? Number(expiry) : null,
        Number(edge.Sequence), Number(edge.Version));
    private static TopologyEvidenceDto Evidence(TopologyEvidenceReference evidence) => new(evidence.Id,
        evidence.TraceLogicalId, evidence.SpanLogicalId, Number(evidence.EventTimeUnixNano));

    private sealed record ReadRequest(decimal AsOfNano, int Limit, string? Cursor, TopologyNodeKind? Kind,
        TopologyRelation? Relation, TopologyProvenance? Provenance, string? FromNode, string? ToNode,
        IReadOnlyList<string> Targets);

    private static ReadRequest Parse(IQueryCollection input, string route)
    {
        var allowed = new HashSet<string>(["asOf"], StringComparer.Ordinal);
        if (route is "nodes" or "edges" or "neighbors" or "path") allowed.UnionWith(["limit", "cursor"]);
        if (route == "nodes") allowed.Add("kind");
        if (route is "edges" or "neighbors") allowed.Add("relation");
        if (route == "edges") allowed.Add("provenance");
        if (route == "path") allowed.UnionWith(["fromNode", "toNode"]);
        if (route == "ancestors") allowed.Add("nodeId");
        if (input.Any(p => !allowed.Contains(p.Key) || (p.Key != "nodeId" && p.Value.Count != 1)))
            throw new ArgumentException("Unknown or repeated topology query parameter.");

        string? Get(string key) => input.TryGetValue(key, out var values) ? values[0] : null;
        var asOf = Get("asOf") is string supplied ? ParseUtcNano(supplied) : NowNano();
        if (asOf > NowNano()) throw new ArgumentException("Future asOf is not allowed.");
        var limit = 100;
        if (Get("limit") is string rawLimit && (!int.TryParse(rawLimit, NumberStyles.None,
                CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaximumPageSize))
            throw new ArgumentException("Invalid topology page limit.");
        var cursor = Get("cursor");
        if (cursor is { Length: 0 or > 4096 }) throw new ArgumentException("Invalid topology cursor.");

        static T? ParseEnum<T>(string? raw) where T : struct, Enum => raw is null ? null
            : Enum.TryParse<T>(raw, false, out var value) && Enum.IsDefined(value)
                && string.Equals(raw, value.ToString(), StringComparison.Ordinal) ? value
                : throw new ArgumentException("Unknown topology filter value.");
        var targets = route == "ancestors" ? input["nodeId"].Select(v => RequiredId(v)).ToArray() : [];
        if (route == "ancestors" && (targets.Length is < 2 or > 20
            || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length))
            throw new ArgumentException("Topology ancestors require 2 to 20 unique nodeId parameters.");
        return new(asOf, limit, cursor, ParseEnum<TopologyNodeKind>(Get("kind")),
            ParseEnum<TopologyRelation>(Get("relation")), ParseEnum<TopologyProvenance>(Get("provenance")),
            Get("fromNode"), Get("toNode"), targets);
    }

    private static decimal ParseUtcNano(string value)
    {
        var match = UtcTimestamp().Match(value);
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["second"].Value + "Z",
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var second))
            throw new ArgumentException("asOf must be a UTC timestamp with at most nine fractional digits.");
        var fractional = match.Groups["fraction"].Value;
        return checked((decimal)second.ToUnixTimeSeconds() * 1_000_000_000m
            + (fractional.Length == 0 ? 0m : decimal.Parse(fractional.PadRight(9, '0'), CultureInfo.InvariantCulture)));
    }

    private static decimal NowNano() => checked((decimal)DateTimeOffset.UtcNow.Ticks * 100m
        - 62135596800000000000m);
}

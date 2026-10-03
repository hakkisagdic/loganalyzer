using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.AspNetCore.Mvc;

namespace Bizigo.Api;

/// <summary>The seven public topology reads use the same scoped query gate.</summary>
public static partial class TopologyReadEndpoints
{
    private const int MaximumPageSize = 200;
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
            TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "nodes"));
        Errors(nodes); nodes.WithName("ListTopologyNodes").Produces<TopologyNodePageDto>();

        var node = group.MapGet("/nodes/{nodeId}", (string nodeId, HttpContext http, IScopedQuery query,
            ICurrentUser user, TopologyPublicationFence fence, TopologyReadCursorCodec cursors,
            ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "node", nodeId));
        Errors(node); node.WithName("GetTopologyNode").Produces<TopologyNodeDetailDto>().Produces(404);

        var edges = group.MapGet("/edges", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "edges"));
        Errors(edges); edges.WithName("ListTopologyEdges").Produces<TopologyEdgePageDto>();

        var edge = group.MapGet("/edges/{edgeId}", (string edgeId,
            [FromQuery(Name = "from")] string? from, [FromQuery(Name = "to")] string? to,
            HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence, TopologyReadCursorCodec cursors,
            ITopologyExpiryNanoClock clock) =>
            HandleEdgeAsync(http, query, user.Scope, fence, cursors, clock, edgeId, from, to));
        Errors(edge); edge.WithName("GetTopologyEdge").Produces<TopologyEdgeDetailDto>().Produces(404);

        var neighbors = group.MapGet("/nodes/{nodeId}/neighbors", (string nodeId, HttpContext http, IScopedQuery query,
            ICurrentUser user, TopologyPublicationFence fence, TopologyReadCursorCodec cursors,
            ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "neighbors", nodeId));
        Errors(neighbors); neighbors.WithName("GetTopologyNeighbors").Produces<TopologyNeighborhoodDto>().Produces(404);

        var path = group.MapGet("/path", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "path"));
        Errors(path); path.WithName("GetTopologyPath").Produces<TopologyPathDto>().Produces(404);

        var ancestors = group.MapGet("/ancestors", (HttpContext http, IScopedQuery query, ICurrentUser user,
            TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock) =>
            HandleAsync(http, query, user.Scope, fence, cursors, clock, "ancestors"));
        Errors(ancestors); ancestors.WithName("GetTopologyAncestors").Produces<TopologyAncestorsDto>().Produces(404);
        return routes;
    }

    private static Task<IResult> HandleEdgeAsync(HttpContext http, IScopedQuery query, AccessScope scope,
        TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock, string edgeId,
        string? from, string? to)
    {
        // These typed parameters expose the optional pair in OpenAPI; Parse
        // remains the strict source of truth for duplicates, UTC nanos and
        // cursor-bound windows.
        if ((from is null) != (to is null)) return Task.FromResult(Problem(400, "InvalidQuery"));
        return HandleAsync(http, query, scope, fence, cursors, clock, "edge", edgeId);
    }

    private static async Task<IResult> HandleAsync(HttpContext http, IScopedQuery query, AccessScope scope,
        TopologyPublicationFence fence, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock,
        string route, string? routeId = null)
    {
        if (scope.IsEmpty) return Results.Forbid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await fence.ExecuteAsync(async (revision, token) =>
            {
                var request = Parse(http.Request.Query, route, routeId, scope, revision, cursors, clock);
                switch (route)
                {
                    case "nodes":
                    {
                        var body = await TopologyResponseBudget.FitPageAsync(request.Limit, async size =>
                        {
                            var page = await query.SearchTopologyNodesAsync(new(request.AsOfNano, size,
                                request.Cursor, request.Kind), scope, token);
                            EnsurePublished(page.PublishedSequence, revision);
                            return new TopologyNodePageDto(page.Items.Select(Node).ToArray(),
                                Wrap(page.Cursor, route, routeId, scope, revision, request, cursors, clock, null),
                                page.Cursor is not null, null, Number(page.PublishedSequence));
                        }, WireOptions);
                        return Json(body);
                    }
                    case "node":
                    {
                        var found = await query.GetTopologyNodeAsync(RequiredId(routeId), request.AsOfNano, scope, token);
                        return found is null ? Results.NotFound() : Json(new TopologyNodeDetailDto(Node(found)));
                    }
                    case "edges":
                    {
                        var body = await TopologyResponseBudget.FitPageAsync(request.Limit, async size =>
                        {
                            var page = await query.SearchTopologyEdgesAsync(new(request.AsOfNano, size,
                                request.Cursor, request.Relation, request.Provenance, request.FromNano, request.ToNano), scope, token);
                            EnsurePublished(page.PublishedSequence, revision);
                            return new TopologyEdgePageDto(page.Items.Select(Edge).ToArray(),
                                Wrap(page.Cursor, route, routeId, scope, revision, request, cursors, clock,
                                    page.EarliestEvidenceExpiryUnixNano),
                                page.Cursor is not null, null, Number(page.PublishedSequence));
                        }, WireOptions);
                        return Json(body);
                    }
                    case "edge":
                    {
                        var body = await TopologyResponseBudget.FitPageAsync(request.Limit, async size =>
                        {
                            var edgeId = RequiredId(routeId);
                            var found = request.FromNano is decimal from && request.ToNano is decimal to
                                ? await query.GetTopologyEdgeAsync(edgeId, request.AsOfNano, from, to, scope,
                                    request.Cursor, size, token)
                                : await query.GetTopologyEdgeAsync(edgeId, request.AsOfNano, scope,
                                    request.Cursor, size, token);
                            if (found is null) return null;
                            return new TopologyEdgeDetailDto(Edge(found.Edge),
                                found.Evidence.Select(Evidence).ToArray(),
                                Wrap(found.EvidenceCursor, route, routeId, scope, revision, request, cursors, clock,
                                    TopologyReadCursorCodec.EvidenceExpiry(found.Edge)));
                        }, WireOptions);
                        return body is null ? Results.NotFound() : Json(body);
                    }
                    case "neighbors":
                    {
                        var nodeId = RequiredId(routeId);
                        if (await query.GetTopologyNodeAsync(nodeId, request.AsOfNano, scope, token) is null)
                            return Results.NotFound();
                        var body = await TopologyResponseBudget.FitPageAsync(request.Limit, async size =>
                        {
                            var result = await query.GetTopologyNeighborhoodAsync(new(nodeId, request.AsOfNano,
                                size, request.Cursor, request.Relation, request.FromNano, request.ToNano), scope, token);
                            EnsurePublished(result.PublishedSequence, revision);
                            return new TopologyNeighborhoodDto(result.Neighbors.Select(n => new TopologyNeighborDto(
                                n.NodeId, n.EdgeId, RelationWire(n.Relation), ProvenanceWire(n.Provenance),
                                n.Direction == TopologyNeighborDirection.Incoming ? "incoming" : "outgoing")).ToArray(),
                                result.ExternalNeighborCount?.ToString(CultureInfo.InvariantCulture), result.ExternalNeighborReason,
                                Wrap(result.Cursor, route, routeId, scope, revision, request, cursors, clock,
                                    result.EarliestEvidenceExpiryUnixNano),
                                result.Cursor is not null, result.ExternalNeighborReason, Number(result.PublishedSequence));
                        }, WireOptions);
                        return Json(body);
                    }
                    case "path":
                    {
                        var from = RequiredId(request.FromNode);
                        var to = RequiredId(request.ToNode);
                        if (await query.GetTopologyNodeAsync(from, request.AsOfNano, scope, token) is null
                            || await query.GetTopologyNodeAsync(to, request.AsOfNano, scope, token) is null)
                            return Results.NotFound();
                        var result = await query.GetTopologyPathAsync(new(from, to, request.AsOfNano,
                            request.Limit, request.Cursor, request.FromNano, request.ToNano), scope, token);
                        EnsurePublished(result.PublishedSequence, revision);
                        return Json(new TopologyPathDto(result.Status.ToString(), result.Nodes, result.EdgeIds,
                            Wrap(result.Cursor, route, routeId, scope, revision, request, cursors, clock,
                                result.EarliestEvidenceExpiryUnixNano),
                            result.Cursor is not null, result.Reason, Number(result.PublishedSequence)));
                    }
                    case "ancestors":
                    {
                        foreach (var target in request.Targets)
                            if (await query.GetTopologyNodeAsync(target, request.AsOfNano, scope, token) is null)
                                return Results.NotFound();
                        var result = await query.GetTopologyCommonAncestorAsync(new(request.Targets, request.AsOfNano,
                                request.FromNano, request.ToNano),
                            scope, token);
                        EnsurePublished(result.PublishedSequence, revision);
                        EnsureEvidenceCurrent(result.EarliestEvidenceExpiryUnixNano, clock.NowUnixNano());
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
        catch (TopologyCursorWireException) { return Problem(400, "InvalidCursor"); }
        catch (TopologyRecordTooLargeException) { return Problem(422, "RecordTooLarge"); }
        catch (ArgumentException) { return Problem(400, "InvalidQuery"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Problem(503, "QueryUnavailable"); }
    }

    private static IResult Json<T>(T body) => !TopologyResponseBudget.Fits(body, WireOptions)
        ? Problem(422, "RecordTooLarge") : Results.Json(body, WireOptions);
    private static IResult Problem(int status, string reason) => Results.Json(new TopologyProblemDto(reason, reason),
        WireOptions, statusCode: status);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(decimal value) => value.ToString("0", CultureInfo.InvariantCulture);
    private static void EnsurePublished(long actual, TopologyPublicationRevision revision)
    {
        if (actual < 0 || (ulong)actual != revision.ClickHouseWatermark)
            throw new TopologyRestartRequiredException("Topology snapshot publication is not current.");
    }

    private static string? Wrap(string? inner, string route, string? routeId, AccessScope scope,
        TopologyPublicationRevision revision, ReadRequest request, TopologyReadCursorCodec cursors,
        ITopologyExpiryNanoClock clock,
        decimal? earliestEvidenceExpiry)
    {
        var now = clock.NowUnixNano();
        EnsureEvidenceCurrent(earliestEvidenceExpiry, now);
        if (inner is null) return null;
        var normalExpiry = checked(now + 300_000_000_000m);
        var validUntil = earliestEvidenceExpiry is decimal evidenceExpiry
            ? Math.Min(normalExpiry, evidenceExpiry) : normalExpiry;
        return cursors.Encode(new TopologyReadCursorState(route,
            TopologyReadCursorCodec.ScopeBinding(route, routeId, scope.Subject, scope.IsUnrestricted, scope.OwnerGroups),
            inner, revision.PostgresEpoch, revision.ClickHouseWatermark, request.AsOfNano, validUntil,
            request.Limit, request.Kind is null ? null : KindWire(request.Kind.Value),
            request.Relation is null ? null : RelationWire(request.Relation.Value),
            request.Provenance is null ? null : ProvenanceWire(request.Provenance.Value),
            request.FromNode, request.ToNode, request.Targets, request.FromNano, request.ToNano));
    }

    private static void EnsureEvidenceCurrent(decimal? expiry, decimal now)
    {
        if (expiry is decimal deadline && now >= deadline)
            throw new TopologyRestartRequiredException("Topology evidence expired while building the response.");
    }
    private static string RequiredId(string? id) => !string.IsNullOrWhiteSpace(id) && !id.Contains(',', StringComparison.Ordinal)
        ? id : throw new ArgumentException("Missing or invalid topology node/edge ID.");

    private static TopologyNodeDto Node(TopologyNodeProjection node) => new(node.Id, KindWire(node.Kind),
        node.DisplayName, node.OwnerGroup, node.Enabled, Number(node.Version), Number(node.ValidFromUnixNano),
        node.ValidToUnixNano is decimal end ? Number(end) : null);
    private static TopologyEdgeDto Edge(TopologyEdgeProjection edge) => new(edge.Id, edge.FromNode, edge.ToNode,
        RelationWire(edge.Relation), ProvenanceWire(edge.Provenance), edge.Directed, (double)edge.Confidence,
        edge.FromOwnerGroup, edge.ToOwnerGroup,
        edge.Visibility == TopologyEdgeVisibility.CrossOwner ? "cross_owner" : "same_owner", Number(edge.FirstSeenUnixNano),
        Number(edge.LastSeenUnixNano), edge.EffectiveExpiry is decimal expiry ? Number(expiry) : null,
        Number(edge.Sequence), Number(edge.Version));
    private static TopologyEvidenceDto Evidence(TopologyEvidenceReference evidence) => new(evidence.Id,
        evidence.TraceLogicalId, evidence.SpanLogicalId, Number(evidence.EventTimeUnixNano));

    private sealed record ReadRequest(decimal AsOfNano, int Limit, string? Cursor, TopologyNodeKind? Kind,
        TopologyRelation? Relation, TopologyProvenance? Provenance, string? FromNode, string? ToNode,
        IReadOnlyList<string> Targets, decimal? FromNano, decimal? ToNano);

    private static ReadRequest Parse(IQueryCollection input, string route, string? routeId, AccessScope scope,
        TopologyPublicationRevision revision, TopologyReadCursorCodec cursors, ITopologyExpiryNanoClock clock)
    {
        var now = clock.NowUnixNano();
        var allowed = new HashSet<string>(["asOf"], StringComparer.Ordinal);
        if (route is "nodes" or "edges" or "neighbors" or "path") allowed.UnionWith(["limit", "cursor"]);
        if (route == "edge") allowed.UnionWith(["evidenceCursor", "evidencePageSize"]);
        if (route == "nodes") allowed.Add("kind");
        if (route is "edges" or "neighbors") allowed.Add("relation");
        if (route == "edges") allowed.Add("provenance");
        if (route == "path") allowed.UnionWith(["fromNode", "toNode"]);
        if (route == "ancestors") allowed.Add("nodeId");
        if (route is "edge" or "edges" or "neighbors" or "path" or "ancestors") allowed.UnionWith(["from", "to"]);
        if (input.Any(p => !allowed.Contains(p.Key) || (p.Key != "nodeId" && p.Value.Count != 1)))
            throw new ArgumentException("Unknown or repeated topology query parameter.");

        string? Get(string key) => input.TryGetValue(key, out var values) ? values[0] : null;
        var cursorKey = route == "edge" ? "evidenceCursor" : "cursor";
        var limitKey = route == "edge" ? "evidencePageSize" : "limit";
        var state = Get(cursorKey) is string encoded ? cursors.Decode(encoded) : null;
        if (state is not null)
        {
            TopologyReadCursorCodec.EnsureBound(state, route, routeId, scope);
            TopologyCursorFence.EnsureCurrent(new(state.PostgresEpoch, state.ClickHouseWatermark,
                DateTimeOffset.MaxValue) { ExactValidUntilUnixNano = state.ValidUntilUnixNano }, revision, now);
        }

        string? Bound(string key, string? stored)
        {
            var supplied = Get(key);
            if (state is not null && supplied is not null && !string.Equals(supplied, stored, StringComparison.Ordinal))
                throw new TopologyCursorWireException();
            return supplied ?? stored;
        }

        var asOf = Get("asOf") is string supplied ? ParseUtcNano(supplied) : state?.AsOfUnixNano ?? now;
        if (state is not null && asOf != state.AsOfUnixNano) throw new TopologyCursorWireException();
        if (asOf > now) throw new ArgumentException("Future asOf is not allowed.");
        var hasFrom = Get("from") is string;
        var hasTo = Get("to") is string;
        if (hasFrom != hasTo) throw new ArgumentException("Topology from and to must be supplied together.");
        var fromNano = hasFrom ? ParseUtcNano(Get("from")!) : state?.FromUnixNano;
        var toNano = hasTo ? ParseUtcNano(Get("to")!) : state?.ToUnixNano;
        if (state is not null && (fromNano != state.FromUnixNano || toNano != state.ToUnixNano))
            throw new TopologyCursorWireException();
        if (fromNano is decimal from && toNano is decimal to && (from < 0 || from >= to || to > asOf))
            throw new ArgumentException("Invalid topology observed window.");
        var limit = state?.Limit ?? (route == "edge" ? 200 : 100);
        if (Get(limitKey) is string rawLimit)
        {
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var suppliedLimit)
                || suppliedLimit is < 1 or > MaximumPageSize)
                throw new ArgumentException("Invalid topology page limit.");
            if (state is not null && suppliedLimit != state.Limit) throw new TopologyCursorWireException();
            limit = suppliedLimit;
        }

        var targets = route == "ancestors" ? input["nodeId"].Select(v => RequiredId(v)).ToArray() : [];
        if (state is not null && targets.Length == 0) targets = state.Targets.ToArray();
        else if (state is not null && !targets.SequenceEqual(state.Targets, StringComparer.Ordinal))
            throw new TopologyCursorWireException();
        if (route == "ancestors" && (targets.Length is < 2 or > 20
            || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length))
            throw new ArgumentException("Topology ancestors require 2 to 20 unique nodeId parameters.");
        return new(asOf, limit, state?.Inner, ParseKind(Bound("kind", state?.Kind)),
            ParseRelation(Bound("relation", state?.Relation)),
            ParseProvenance(Bound("provenance", state?.Provenance)),
            Bound("fromNode", state?.FromNode), Bound("toNode", state?.ToNode), targets, fromNano, toNano);
    }

    private static TopologyNodeKind? ParseKind(string? value) => value switch
    {
        null => null,
        "source" => TopologyNodeKind.Source,
        "service" => TopologyNodeKind.Service,
        "service_instance" => TopologyNodeKind.ServiceInstance,
        "network" => TopologyNodeKind.Network,
        _ => throw new ArgumentException("Unknown topology node kind."),
    };

    private static TopologyRelation? ParseRelation(string? value) => value switch
    {
        null => null,
        "depends_on" => TopologyRelation.DependsOn,
        "contains" => TopologyRelation.Contains,
        "connects_to" => TopologyRelation.ConnectsTo,
        _ => throw new ArgumentException("Unknown topology relation."),
    };

    private static TopologyProvenance? ParseProvenance(string? value) => value switch
    {
        null => null,
        "declared" => TopologyProvenance.Declared,
        "observed" => TopologyProvenance.Observed,
        _ => throw new ArgumentException("Unknown topology provenance."),
    };

    private static string KindWire(TopologyNodeKind value) => value switch
    {
        TopologyNodeKind.Source => "source",
        TopologyNodeKind.Service => "service",
        TopologyNodeKind.ServiceInstance => "service_instance",
        TopologyNodeKind.Network => "network",
        _ => throw new InvalidDataException("Unknown topology node kind."),
    };

    private static string RelationWire(TopologyRelation value) => value switch
    {
        TopologyRelation.DependsOn => "depends_on",
        TopologyRelation.Contains => "contains",
        TopologyRelation.ConnectsTo => "connects_to",
        _ => throw new InvalidDataException("Unknown topology relation."),
    };

    private static string ProvenanceWire(TopologyProvenance value) => value switch
    {
        TopologyProvenance.Declared => "declared",
        TopologyProvenance.Observed => "observed",
        _ => throw new InvalidDataException("Unknown topology provenance."),
    };

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

}

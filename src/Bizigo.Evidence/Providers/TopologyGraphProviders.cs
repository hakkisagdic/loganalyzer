using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

public sealed class TopologyGraphPathProvider : IEvidenceProvider
{
    private readonly IScopedQuery _query;
    private readonly TopologyProviderBudget _topologyBudget;

    public TopologyGraphPathProvider(IScopedQuery query) : this(query, TopologyProviderBudget.Default) { }
    public TopologyGraphPathProvider(IScopedQuery query, TopologyProviderBudget topologyBudget)
    { _query = query; _topologyBudget = topologyBudget; }

    public string Id => "topology.graph-path";
    public EvidenceKind Kind => EvidenceKind.Topology;
    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget,
        CancellationToken cancellationToken) =>
        TopologyGraphEvidence.GatherAsync(_query, false, Id, window, scope, budget, _topologyBudget, cancellationToken);
}

public sealed class TopologyCommonAncestorProvider : IEvidenceProvider
{
    private readonly IScopedQuery _query;
    private readonly TopologyProviderBudget _topologyBudget;

    public TopologyCommonAncestorProvider(IScopedQuery query) : this(query, TopologyProviderBudget.Default) { }
    public TopologyCommonAncestorProvider(IScopedQuery query, TopologyProviderBudget topologyBudget)
    { _query = query; _topologyBudget = topologyBudget; }

    public string Id => "topology.common-ancestor";
    public EvidenceKind Kind => EvidenceKind.Topology;
    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget,
        CancellationToken cancellationToken) =>
        TopologyGraphEvidence.GatherAsync(_query, true, Id, window, scope, budget, _topologyBudget, cancellationToken);
}

internal static class TopologyGraphEvidence
{
    internal static async Task<EvidenceSlice> GatherAsync(IScopedQuery query, bool ancestor, string providerId,
        RcaWindow window, AccessScope scope, GatherBudget budget, TopologyProviderBudget topologyBudget,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(topologyBudget); topologyBudget.Validate();
        if (budget.MaxItems < 1 || budget.MaxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(budget.MaxDuration);
        try
        {
            var feed = await query.GetTelemetryFeedAsync(TelemetrySignal.Traces, null, scope, timeout.Token);
            if (feed.Status == TelemetryResultStatus.Failed)
                return Slice(providerId, EvidenceStatus.Failed, feed.Error ?? "QueryUnavailable", "Failed");
            if (feed.Status == TelemetryResultStatus.NeverFed)
                return Slice(providerId, EvidenceStatus.NeverFed, "Yetkili trace beslemesi hiç alınmamış.", "NotRun");

            var correlation = new CorrelationWindow
            {
                From = window.From, To = window.To, BaselineFrom = window.BaselineFrom, BaselineTo = window.BaselineTo,
                OwnerGroups = window.OwnerGroups, SourceIds = window.SourceIds,
            };
            var onsets = await query.GetPropagationAsync(correlation, scope, 3, 1001, timeout.Token);
            var affectedSourceIds = onsets.Select(static onset => onset.SourceId).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Take(21).ToArray();
            if (affectedSourceIds.Length < 2)
                return Slice(providerId, EvidenceStatus.Empty, "Ortak graph değerlendirmesi için en az iki etkilenen kaynak gerekir.", "Evaluated");
            if (affectedSourceIds.Length > 20)
                return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: affected source count exceeds 20.", "NotComparable", true);
            var resolved = await query.ResolveTopologySourceNodesAsync(affectedSourceIds, scope, timeout.Token);
            var nodes = resolved.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (nodes.Length != affectedSourceIds.Length)
                return Slice(providerId, EvidenceStatus.Unavailable,
                    "Affected source topology mapping is absent or outside scope; identity was not guessed.", "NotComparable");
            if (topologyBudget.Measure(nodes.Length, 0, 0, 0) != TopologyProviderCompleteness.Complete)
                return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: node limit.", "NotComparable", true);
            return ancestor
                ? await AncestorAsync(query, providerId, nodes, window, scope, topologyBudget, timeout.Token)
                : await PathsAsync(query, providerId, nodes, window, scope, budget, topologyBudget, timeout.Token);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { return Slice(providerId, EvidenceStatus.Failed, "Timeout", "Failed", true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        { return Slice(providerId, EvidenceStatus.Failed, "QueryUnavailable", "Failed", true); }
    }

    private static async Task<EvidenceSlice> PathsAsync(IScopedQuery query, string providerId, string[] nodes,
        RcaWindow window, AccessScope scope, GatherBudget budget, TopologyProviderBudget topologyBudget,
        CancellationToken token)
    {
        var items = new List<EvidenceItem>(); var partial = false; var edges = 0; var pages = 0; var bytes = 0;
        var clock = TopologyIdentity.Nano(window.To);
        for (var i = 0; i < nodes.Length && items.Count < budget.MaxItems; i++)
        for (var j = i + 1; j < nodes.Length && items.Count < budget.MaxItems; j++)
        {
            var result = await query.GetTopologyPathAsync(new(nodes[i], nodes[j], clock, 200), scope, token);
            pages++;
            if (result.Status == TopologyGraphResultStatus.NotVerified) { partial = true; continue; }
            if (result.Status != TopologyGraphResultStatus.Found) continue;
            var item = Item(providerId, result.EdgeIds, result.Nodes, null, window);
            edges += result.EdgeIds.Count;
            bytes += JsonSerializer.SerializeToUtf8Bytes(item, BundleSerializer.Options).Length;
            if (topologyBudget.Measure(nodes.Length, edges, pages, bytes) != TopologyProviderCompleteness.Complete)
                return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: graph path proof truncated.", "NotComparable", true);
            items.Add(item);
        }
        var status = items.Count > 0 ? EvidenceStatus.Gathered : partial ? EvidenceStatus.Unavailable : EvidenceStatus.Empty;
        return new EvidenceSlice
        {
            ProviderId = providerId, Kind = EvidenceKind.Topology, Status = status, Items = items,
            Detail = partial ? "HiddenBoundary; graph path not verified." : items.Count == 0 ? "Visible graph path not found." :
                "Shortest directed depends_on paths; observed edges are correlation, not causal proof.",
            Truncated = partial, Telemetry = new(TelemetryResultStatus.Data, partial ? "NotComparable" : "Evaluated", []),
        };
    }

    private static async Task<EvidenceSlice> AncestorAsync(IScopedQuery query, string providerId, string[] nodes,
        RcaWindow window, AccessScope scope, TopologyProviderBudget topologyBudget, CancellationToken token)
    {
        var result = await query.GetTopologyCommonAncestorAsync(new(nodes, TopologyIdentity.Nano(window.To)), scope, token);
        if (result.Status == TopologyGraphResultStatus.NotVerified)
            return Slice(providerId, EvidenceStatus.Unavailable, result.Reason ?? "HiddenBoundary", "NotComparable", true);
        if (result.Status != TopologyGraphResultStatus.Found || result.NodeId is null)
            return Slice(providerId, EvidenceStatus.Empty, "Visible strict common ancestor not found.", "Evaluated");
        var edgeIds = result.Paths.SelectMany(static path => path.EdgeIds).Distinct(StringComparer.Ordinal).ToArray();
        var nodesInProof = result.Paths.SelectMany(static path => path.Nodes).Distinct(StringComparer.Ordinal).ToArray();
        var item = Item(providerId, edgeIds, nodesInProof, result.NodeId, window);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, BundleSerializer.Options).Length;
        if (topologyBudget.Measure(nodesInProof.Length, edgeIds.Length, 1, bytes) != TopologyProviderCompleteness.Complete)
            return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: common ancestor proof truncated.", "NotComparable", true);
        return new EvidenceSlice
        {
            ProviderId = providerId, Kind = EvidenceKind.Topology, Status = EvidenceStatus.Gathered,
            Items = [item],
            Detail = "Strict common ancestor selected by max-hop, sum-hop, ordinal ID.",
            Telemetry = new(TelemetryResultStatus.Data, "Evaluated", []),
        };
    }

    private static EvidenceItem Item(string providerId, IReadOnlyList<string> edgeIds, IReadOnlyList<string> nodes,
        string? ancestor, RcaWindow window)
    {
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["edge_ids"] = JsonSerializer.Serialize(edgeIds, BundleSerializer.Options),
            ["node_ids"] = JsonSerializer.Serialize(nodes, BundleSerializer.Options),
            ["provenance"] = "declared-or-observed",
            ["confidence"] = "provider-preserved",
            ["window_from"] = window.From.ToString("O", CultureInfo.InvariantCulture),
            ["window_to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
        };
        if (ancestor is not null) payload["ancestor_node_id"] = ancestor;
        var canonical = providerId + "|" + string.Join('|', edgeIds) + "|" + ancestor;
        return new(RawSignalEnvelope.Hash(System.Text.Encoding.UTF8.GetBytes(canonical)), providerId,
            EvidenceKind.Topology, window.To, 1, ancestor is null ? "Directed topology path" : "Strict common topology ancestor", payload);
    }

    private static EvidenceSlice Slice(string providerId, EvidenceStatus status, string detail, string evaluation,
        bool truncated = false) => new()
    {
        ProviderId = providerId, Kind = EvidenceKind.Topology, Status = status, Detail = detail, Truncated = truncated,
        Telemetry = new(status == EvidenceStatus.NeverFed ? TelemetryResultStatus.NeverFed : status == EvidenceStatus.Failed
            ? TelemetryResultStatus.Failed : TelemetryResultStatus.Empty, evaluation, []),
    };
}

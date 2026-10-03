namespace Bizigo.Evidence;

public enum TopologyProviderCompleteness { Complete = 1, Partial = 2, Failed = 3 }

public sealed record TopologyProviderBudget(int MaxNodes, int MaxEdges, int MaxPages, int MaxSerializedBytes)
{
    public static TopologyProviderBudget Default { get; } = new(1000, 4000, 20, 1024 * 1024);

    public void Validate()
    {
        if (MaxNodes < 1 || MaxEdges < 1 || MaxPages < 1 || MaxSerializedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(TopologyProviderBudget));
    }

    public TopologyProviderCompleteness Measure(int nodes, int edges, int pages, int serializedBytes)
    {
        Validate();
        if (nodes < 0 || edges < 0 || pages < 0 || serializedBytes < 0) throw new ArgumentOutOfRangeException(nameof(nodes));
        return nodes > MaxNodes || edges > MaxEdges || pages > MaxPages || serializedBytes > MaxSerializedBytes
            ? TopologyProviderCompleteness.Partial : TopologyProviderCompleteness.Complete;
    }
}

public static class TopologyProviderFailure
{
    public static TopologyProviderCompleteness Classify(string outcome) => outcome switch
    {
        "partial" => TopologyProviderCompleteness.Partial,
        "timeout" or "exception" => TopologyProviderCompleteness.Failed,
        "caller-cancel" => throw new OperationCanceledException("Caller cancellation propagates."),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

internal sealed class TopologyProviderUsage(TopologyProviderBudget budget)
{
    private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _edges = new(StringComparer.Ordinal);
    private int _pages;
    private int _serializedBytes;

    public bool IncludeNodes(IEnumerable<string> nodes)
    {
        foreach (var node in nodes) _nodes.Add(node);
        return WithinBudget();
    }

    public bool IncludeEdges(IEnumerable<string> edges)
    {
        foreach (var edge in edges) _edges.Add(edge);
        return WithinBudget();
    }

    public bool NextPage()
    {
        _pages = checked(_pages + 1);
        return WithinBudget();
    }

    public bool IncludeSerializedBytes(int length)
    {
        _serializedBytes = checked(_serializedBytes + length);
        return WithinBudget();
    }

    private bool WithinBudget() => budget.Measure(_nodes.Count, _edges.Count, _pages, _serializedBytes)
        == TopologyProviderCompleteness.Complete;
}

namespace Bizigo.Contracts;

/// <summary>Explicit authoritative aliases; these strings are lookup keys, never node identity.</summary>
public sealed record TopologyAliasInput(string SourceId, string ServiceNamespace, string ServiceName,
    string? ServiceNodeId = null, string? InstanceId = null);

public sealed record TopologyNodeInput(TopologyNodeKind Kind, string DisplayName, string OwnerGroup,
    bool Enabled, TopologyAliasInput[] Bindings, string? SourceId = null, long? Version = null);

public sealed record TopologyNodeVersion(string Id, TopologyNodeKind Kind, string DisplayName, string OwnerGroup,
    bool Enabled, bool Deleted, long Version, string ValidFromUnixNano);

public sealed record TopologyRegistryResult(int Status, TopologyNodeVersion? Node = null, string? Error = null);

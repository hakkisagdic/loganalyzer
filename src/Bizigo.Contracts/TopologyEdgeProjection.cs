namespace Bizigo.Contracts;

public enum TopologyEdgeVisibility { SameOwner = 1, CrossOwner = 2 }

/// <summary>
/// Declared ve observed depoların ortak, salt okunur projection'ı. İki owner
/// fiziksel tutulur; tek owner'a indirgemek cross-owner sızıntısını gizlerdi.
/// </summary>
public sealed record TopologyEdgeProjection(
    string Id,
    string FromNode,
    string ToNode,
    TopologyRelation Relation,
    TopologyProvenance Provenance,
    bool Directed,
    decimal Confidence,
    string FromOwnerGroup,
    string ToOwnerGroup,
    TopologyEdgeVisibility Visibility,
    decimal FirstSeenUnixNano,
    decimal LastSeenUnixNano,
    decimal? EffectiveExpiry,
    long Sequence,
    long Version,
    bool Deleted);

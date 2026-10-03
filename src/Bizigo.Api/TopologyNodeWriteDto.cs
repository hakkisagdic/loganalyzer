using System.Globalization;
using Bizigo.Contracts;

namespace Bizigo.Api;

/// <summary>Write response version is lossless JSON text; registry arithmetic remains Int64.</summary>
public sealed record TopologyNodeWriteDto(string Id, TopologyNodeKind Kind, string DisplayName, string OwnerGroup,
    bool Enabled, bool Deleted, string Version, string ValidFromUnixNano)
{
    public static TopologyNodeWriteDto From(TopologyNodeVersion node) => new(node.Id, node.Kind, node.DisplayName,
        node.OwnerGroup, node.Enabled, node.Deleted, node.Version.ToString(CultureInfo.InvariantCulture), node.ValidFromUnixNano);
}

/// <summary>Errors cannot reintroduce a numeric domain version in OpenAPI or JSON.</summary>
public sealed record TopologyNodeWriteResultDto(int Status, TopologyNodeWriteDto? Node, string? Error);

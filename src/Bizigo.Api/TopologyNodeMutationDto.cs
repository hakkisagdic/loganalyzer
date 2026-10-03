using System.Globalization;
using Bizigo.Contracts;

namespace Bizigo.Api;

/// <summary>The public mutation version is decimal text; registry arithmetic stays Int64.</summary>
public sealed record TopologyNodeMutationDto(TopologyNodeKind Kind, string DisplayName, string OwnerGroup,
    bool Enabled, TopologyAliasInput[] Bindings, string? SourceId = null, string? Version = null)
{
    public bool TryToDomain(out TopologyNodeInput? input)
    {
        input = null;
        long? version = null;
        if (Version is not null)
        {
            if (!TopologyVersionWire.TryParse(Version, out var parsed)) return false;
            version = parsed;
        }
        input = new(Kind, DisplayName, OwnerGroup, Enabled, Bindings, SourceId, version);
        return true;
    }
}

internal static class TopologyVersionWire
{
    public static bool TryParse(string? text, out long version)
    {
        version = 0;
        if (string.IsNullOrEmpty(text) || text[0] is < '1' or > '9') return false;
        for (var index = 1; index < text.Length; index++)
            if (text[index] is < '0' or > '9') return false;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}

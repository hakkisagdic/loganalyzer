using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Api;

public static class TopologyWriteEndpoints
{
    public static IEndpointRouteBuilder MapTopologyWrites(this IEndpointRouteBuilder routes)
    {
        var nodes = routes.MapGroup("/v1/topology/nodes").WithTags("topology").RequireAuthorization(BizigoAuthPolicies.Admin);
        nodes.MapPost("", async (TopologyNodeMutationDto input, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
        {
            if (!input.TryToDomain(out var domain)) return Result(new(400, Error: "Invalid topology version."));
            return Result(await registry.CreateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, domain!, token));
        })
            .WithName("CreateTopologyNode").Produces<TopologyNodeWriteDto>(201).Produces<TopologyNodeWriteResultDto>(400)
            .Produces<TopologyNodeWriteResultDto>(403).Produces<TopologyNodeWriteResultDto>(409)
            .Produces<TopologyNodeWriteResultDto>(503).Produces<TopologyNodeWriteResultDto>(504);
        nodes.MapPut("/{nodeId}", async (string nodeId, TopologyNodeMutationDto input, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
        {
            if (!input.TryToDomain(out var domain)) return Result(new(400, Error: "Invalid topology version."));
            return Result(await registry.UpdateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, nodeId, domain!, token));
        })
            .WithName("UpdateTopologyNode").Produces<TopologyNodeWriteDto>().Produces<TopologyNodeWriteResultDto>(400)
            .Produces<TopologyNodeWriteResultDto>(403).Produces<TopologyNodeWriteResultDto>(404)
            .Produces<TopologyNodeWriteResultDto>(409).Produces<TopologyNodeWriteResultDto>(503)
            .Produces<TopologyNodeWriteResultDto>(504);
        nodes.MapDelete("/{nodeId}", async (string nodeId, string version, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
        {
            if (!TopologyVersionWire.TryParse(version, out var parsed)) return Result(new(400, Error: "Invalid topology version."));
            return Result(await registry.DeleteAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, nodeId, parsed, token));
        })
            .WithName("DeleteTopologyNode").Produces(204).Produces<TopologyNodeWriteResultDto>(400)
            .Produces<TopologyNodeWriteResultDto>(403).Produces<TopologyNodeWriteResultDto>(404)
            .Produces<TopologyNodeWriteResultDto>(409).Produces<TopologyNodeWriteResultDto>(503)
            .Produces<TopologyNodeWriteResultDto>(504);
        var edges = routes.MapGroup("/v1/topology/edges").WithTags("topology").RequireAuthorization(BizigoAuthPolicies.Admin);
        edges.MapPost("", async (TopologyDeclaredEdgeInput input, TopologyEdgeRegistry registry, ICurrentUser user, CancellationToken token) =>
            EdgeResult(await registry.CreateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, input, token)))
            .WithName("CreateTopologyDeclaredEdge").Produces<TopologyDeclaredEdgeVersion>(201)
            .Produces<TopologyDeclaredEdgeResult>(400).Produces<TopologyDeclaredEdgeResult>(403)
            .Produces<TopologyDeclaredEdgeResult>(409).Produces<TopologyDeclaredEdgeResult>(503).Produces<TopologyDeclaredEdgeResult>(504);
        edges.MapPut("/{edgeId:guid}", async (Guid edgeId, TopologyDeclaredEdgeInput input, TopologyEdgeRegistry registry, ICurrentUser user, CancellationToken token) =>
        {
            if (input.Version is not null && !TopologyVersionWire.TryParse(input.Version, out _))
                return EdgeResult(new(400, Error: "Invalid topology version."));
            return EdgeResult(await registry.UpdateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, edgeId, input, token));
        })
            .WithName("UpdateTopologyDeclaredEdge").Produces<TopologyDeclaredEdgeVersion>()
            .Produces<TopologyDeclaredEdgeResult>(400).Produces<TopologyDeclaredEdgeResult>(403)
            .Produces<TopologyDeclaredEdgeResult>(404).Produces<TopologyDeclaredEdgeResult>(409)
            .Produces<TopologyDeclaredEdgeResult>(503).Produces<TopologyDeclaredEdgeResult>(504);
        edges.MapDelete("/{edgeId:guid}", async (Guid edgeId, string version, TopologyEdgeRegistry registry, ICurrentUser user, CancellationToken token) =>
        {
            if (!TopologyVersionWire.TryParse(version, out var parsed))
                return EdgeResult(new(400, Error: "Invalid topology version."));
            return EdgeResult(await registry.DeleteAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, edgeId, parsed, token));
        })
            .WithName("DeleteTopologyDeclaredEdge").Produces(204).Produces<TopologyDeclaredEdgeResult>(400)
            .Produces<TopologyDeclaredEdgeResult>(403).Produces<TopologyDeclaredEdgeResult>(404)
            .Produces<TopologyDeclaredEdgeResult>(409).Produces<TopologyDeclaredEdgeResult>(503).Produces<TopologyDeclaredEdgeResult>(504);
        return routes;
    }

    private static IResult Result(TopologyRegistryResult result) => result.Status switch
    {
        201 => Results.Created("/v1/topology/nodes/" + result.Node!.Id, TopologyNodeWriteDto.From(result.Node)),
        200 => Results.Ok(TopologyNodeWriteDto.From(result.Node!)),
        204 => Results.NoContent(),
        _ => Results.Json(new TopologyNodeWriteResultDto(result.Status,
            result.Node is null ? null : TopologyNodeWriteDto.From(result.Node), result.Error), statusCode: result.Status),
    };

    private static IResult EdgeResult(TopologyDeclaredEdgeResult result) => result.Status switch
    {
        201 => Results.Created("/v1/topology/edges/" + result.Edge!.Id, result.Edge),
        200 => Results.Ok(result.Edge),
        204 => Results.NoContent(),
        _ => Results.Json(result, statusCode: result.Status),
    };
}

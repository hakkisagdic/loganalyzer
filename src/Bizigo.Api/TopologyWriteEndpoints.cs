using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Api;

public static class TopologyWriteEndpoints
{
    public static IEndpointRouteBuilder MapTopologyWrites(this IEndpointRouteBuilder routes)
    {
        var nodes = routes.MapGroup("/v1/topology/nodes").WithTags("topology").RequireAuthorization(BizigoAuthPolicies.Admin);
        nodes.MapPost("", async (TopologyNodeInput input, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
            Result(await registry.CreateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, input, token)))
            .WithName("CreateTopologyNode").Produces<TopologyNodeWriteDto>(201).Produces<TopologyRegistryResult>(400)
            .Produces<TopologyRegistryResult>(403).Produces<TopologyRegistryResult>(409).Produces<TopologyRegistryResult>(503).Produces<TopologyRegistryResult>(504);
        nodes.MapPut("/{nodeId}", async (string nodeId, TopologyNodeInput input, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
            Result(await registry.UpdateAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, nodeId, input, token)))
            .WithName("UpdateTopologyNode").Produces<TopologyNodeWriteDto>().Produces<TopologyRegistryResult>(400)
            .Produces<TopologyRegistryResult>(403).Produces<TopologyRegistryResult>(404).Produces<TopologyRegistryResult>(409)
            .Produces<TopologyRegistryResult>(503).Produces<TopologyRegistryResult>(504);
        nodes.MapDelete("/{nodeId}", async (string nodeId, long version, TopologyRegistry registry, ICurrentUser user, CancellationToken token) =>
            Result(await registry.DeleteAsync(user.Scope, user.Principal?.IsInRole(BizigoRoles.Admin) == true, nodeId, version, token)))
            .WithName("DeleteTopologyNode").Produces(204).Produces<TopologyRegistryResult>(400)
            .Produces<TopologyRegistryResult>(403).Produces<TopologyRegistryResult>(404).Produces<TopologyRegistryResult>(409)
            .Produces<TopologyRegistryResult>(503).Produces<TopologyRegistryResult>(504);
        return routes;
    }

    private static IResult Result(TopologyRegistryResult result) => result.Status switch
    {
        201 => Results.Created("/v1/topology/nodes/" + result.Node!.Id, TopologyNodeWriteDto.From(result.Node)),
        200 => Results.Ok(TopologyNodeWriteDto.From(result.Node!)),
        204 => Results.NoContent(),
        _ => Results.Json(result, statusCode: result.Status),
    };
}

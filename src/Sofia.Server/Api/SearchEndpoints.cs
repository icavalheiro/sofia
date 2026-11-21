using Sofia.Core.Agent;
using Sofia.Core.Data;
using Sofia.Contracts.Domain;
using Sofia.Core.Search;

namespace Sofia.Server.Api;

public static class SearchEndpoints
{
    public static void MapSearchApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/search", (string? q, string? workspaceId, bool? includeArchived, bool? includeSubagents, int? limit, SofiaDb db, ISessionSearch search) =>
        {
            if (string.IsNullOrWhiteSpace(q)) throw new ArgumentException("Query is required.");
            if (workspaceId is not null && db.Workspaces.FindById(workspaceId) is null) throw new NotFoundException("Workspace not found.");
            var results = search.Search(db, q!, workspaceId, includeArchived ?? false, includeSubagents ?? false, limit ?? 20);
            return Results.Ok(new { results });
        });
    }
}

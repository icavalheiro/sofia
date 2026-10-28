using Sofia.Server.Infrastructure;

namespace Sofia.Server.Api;

public static class ProcessEndpoints
{
    public static void MapProcessApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/processes");
        api.MapGet("", (string? sessionId, ProcessManager processes) => Results.Ok(processes.List(sessionId)));
        api.MapGet("/{id}/output", (string id, int? tail, ProcessManager processes) =>
        {
            var (info, output) = processes.Output(id, tail ?? 200);
            return Results.Ok(new { info, output });
        });
        api.MapPost("/{id}/kill", (string id, ProcessManager processes) => Results.Ok(processes.Kill(id)));
    }
}

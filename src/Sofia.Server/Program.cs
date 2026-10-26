using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Sofia.Server.Agent;
using Sofia.Server.Api;
using Sofia.Server.Browser;
using Sofia.Server.Changes;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;
using Sofia.Server.Providers;
using Sofia.Server.Security;
using Sofia.Server.Vcs;

var builder = WebApplication.CreateBuilder(args);

// Loopback by default; SOFIA_URLS / --urls / ASPNETCORE_URLS expose it elsewhere (remote clients must pair).
var urls = Environment.GetEnvironmentVariable("SOFIA_URLS");
if (!string.IsNullOrWhiteSpace(urls)) builder.WebHost.UseUrls(urls);
else if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://127.0.0.1:5199");

var options = builder.Configuration.GetSection("Sofia").Get<SofiaOptions>() ?? new SofiaOptions();
if (string.IsNullOrWhiteSpace(options.DataDir)) options.DataDir = SofiaOptions.DefaultDataDir();
if (string.IsNullOrWhiteSpace(options.UiPath))
{
    var candidates = new[]
    {
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Sofia.UI", "dist")),
    };
    options.UiPath = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html"))) ?? "";
}
options.EnsureDirectories();

builder.Services.AddSingleton(options);
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(o => Json.Configure(o.PayloadSerializerOptions));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.IsLoopback || options.AllowedOrigins.Contains(origin)))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// Providers that gate subscription traffic (OpenCode Go) reject a generic or missing user agent, so both LLM
// clients identify the harness; a per-request override in a provider client wins over these defaults.
builder.Services.AddHttpClient("llm", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderIdentity.UserAgent);
});
builder.Services.AddHttpClient("probe", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderIdentity.UserAgent);
});
builder.Services.AddHttpClient("fetch", c =>
{
    c.Timeout = TimeSpan.FromSeconds(40);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SofiaHarness/0.2)");
});

builder.Services.AddSingleton<SofiaDb>();
builder.Services.AddSingleton<IEventBus, EventBus>();
builder.Services.AddSingleton<ProviderService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChangeTracker>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<SkillService>();
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<KnowledgeService>();
builder.Services.AddSingleton<KeepAwakeService>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<ContextFiles>();
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<McpManager>();
builder.Services.AddSingleton<BrowserService>();
builder.Services.AddSingleton<IEventHandler, BrowserCleanupHandler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<McpManager>());
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<RunServices>();
builder.Services.AddSingleton<AgentService>();

// Event-driven pieces: the dispatcher feeds handlers; the broadcaster feeds SignalR clients.
builder.Services.AddSingleton<IEventHandler, TitleHandler>();
builder.Services.AddHostedService<EventDispatcher>();
builder.Services.AddHostedService<HubBroadcaster>();
builder.Services.AddHostedService<MaintenanceService>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = error switch
    {
        NotFoundException e => (StatusCodes.Status404NotFound, e.Message),
        ConflictException e => (StatusCodes.Status409Conflict, e.Message),
        ArgumentException e => (StatusCodes.Status400BadRequest, e.Message),
        BadHttpRequestException e => (StatusCodes.Status400BadRequest, e.Message),
        _ => (StatusCodes.Status500InternalServerError, error?.Message ?? "Unexpected error."),
    };
    if (status == 500)
        context.RequestServices.GetRequiredService<ILogger<Program>>().LogError(error, "Unhandled error on {Path}", context.Request.Path);
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseCors();
app.UseMiddleware<AccessMiddleware>();
app.UseWebSockets();

if (options.UiPath.Length > 0)
{
    var files = new PhysicalFileProvider(options.UiPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapSessionApi();
app.MapPlatformApi();
app.MapBrowserApi();
app.MapHub<SofiaHub>("/hubs/events");
app.MapFallback("/api/{**rest}", () => Results.Json(new { error = "Not found." }, statusCode: 404));
if (options.UiPath.Length > 0)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(options.UiPath) });

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    log.LogInformation("Sofia.Server {Version} · data {DataDir} · UI {Ui}", SessionEndpoints.Version, options.DataDir,
        options.UiPath.Length > 0 ? options.UiPath : "not bundled (run the Vite dev server)");
});

app.Run();

public partial class Program;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Sofia.Core;
using Sofia.Core.Agent;
using Sofia.Core.Infrastructure;
using Sofia.Core.Security;
using Sofia.Server.Api;
using Sofia.Core.Events;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;
using Sofia.Server.Security;

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
    // UI hosts, in order: the published bundle (next to the executable or the content root), the Avalonia browser
    // publish output of this checkout (so `dotnet run` serves the native UI without a copy step), then — until the
    // cutover step of spec 044 — the Vue dist.
    var browserBundle = new[]
    {
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Sofia.App.Browser", "bin", "Release", "net10.0-browser1.0", "browser-wasm", "AppBundle")),
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Sofia.App.Browser", "bin", "Debug", "net10.0-browser1.0", "browser-wasm", "AppBundle")),
    };
    string[] candidates =
    [
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
        .. browserBundle,
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Sofia.UI", "dist")),
    ];
    options.UiPath = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html"))) ?? "";
}
options.EnsureDirectories();

builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(o => Json.Configure(o.PayloadSerializerOptions));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.IsLoopback || options.AllowedOrigins.Contains(origin)))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// The engine: store, providers, agent loop, tools, MCP, LSP, browser, processes and the event pipeline.
// The server only warns when another process holds the data directory (two servers on one store keep working).
builder.Services.AddSofiaCore(options);
// Where this host answers: tools that hand the model a REST endpoint need the real listening address.
builder.Services.AddSingleton<IBaseUrlProvider, ServerAddressBaseUrl>();
// Every bus event goes out to SignalR clients.
builder.Services.AddHostedService<HubBroadcaster>();

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
    // The .NET WASM runtime is served from _framework/: .wasm needs the MIME type the streaming instantiation expects.
    var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    contentTypes.Mappings[".wasm"] = "application/wasm";
    contentTypes.Mappings[".dat"] = "application/octet-stream";
    contentTypes.Mappings[".webcil"] = "application/octet-stream";
    contentTypes.Mappings[".symbols"] = "application/octet-stream";
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes });
}

app.MapSessionApi();
app.MapSearchApi();
app.MapPlatformApi();
app.MapBrowserApi();
app.MapProcessApi();
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

// Diagnostics: say why the host is going down (a Ctrl+C/SIGTERM delivered by a child process sharing the console looks identical to a manual one).
Console.CancelKeyPress += (_, e) =>
    app.Services.GetService<ILogger<Program>>()?.LogWarning("Console signal {Signal} received; shutting down", e.SpecialKey);
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetService<ILogger<Program>>()?.LogWarning("Application is stopping (shutdown requested)"));

app.Run();

public partial class Program;

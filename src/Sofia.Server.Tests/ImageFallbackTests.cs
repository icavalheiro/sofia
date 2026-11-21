using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sofia.Contracts.Domain;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Tests;

/// <summary>
/// OpenAI-compatible endpoint with two models on one provider: `gpt-oss` (catalog says text-only) and `gpt-4o`
/// (catalog says vision). Requests to the vision model answer with an image description, requests to the text model
/// answer plainly. With <see cref="RefuseImages"/> set, the text model rejects any request that carries an image part —
/// the shape of a runtime whose metadata claims vision but refuses pictures.
/// </summary>
internal sealed class VisionFallbackHandler : HttpMessageHandler
{
    /// <summary>Every chat-completion body received (shared: handlers are created per client).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Bodies = [];

    /// <summary>Model id of each chat-completion request, in arrival order.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Models = [];

    /// <summary>When true, requests carrying an image part are refused with the provider's wording — but only for
    /// <see cref="RefusedModel"/>, so the fallback model's own analysis request still goes through.</summary>
    public static volatile bool RefuseImages;

    /// <summary>When true, refusals arrive as a 200 SSE stream carrying an `error` chunk (no HTTP status code), which is
    /// how llama.cpp-family runtimes (Unsloth Studio, Ollama, LM Studio) report an image to a text-only GGUF.</summary>
    public static volatile bool RefuseInStream;

    /// <summary>The model whose image-carrying requests get refused (the chat model in the refusal test).</summary>
    public const string RefusedModel = "gpt-4o";

    /// <summary>How many times image-carrying requests were refused (so a test can prove it happened once).</summary>
    public static int Refusals;

    /// <summary>The wording the runtime uses: Unsloth Studio's real message, which mentions vision rather than images.</summary>
    public const string RefusalMessage = "Image provided but current GGUF model does not support vision.";

    public const string Description = "A terminal window showing the error: CS8978 method group cannot be made nullable.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (!url.StartsWith("http://vision.llm/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.EndsWith("/models", StringComparison.Ordinal))
            return Json("""{"data":[{"id":"gpt-oss","context_length":16384},{"id":"gpt-4o","context_length":16384},{"id":"llava-v1.6","context_length":16384}]}""");

        var body = await request.Content!.ReadAsStringAsync(ct);
        Bodies.Add(body);
        var model = ModelOf(body);
        Models.Add(model);

        var hasImage = body.Contains("image_url", StringComparison.Ordinal);
        if (RefuseImages && hasImage && model == RefusedModel && Interlocked.Increment(ref Refusals) == 1)
            return RefuseInStream
                // A 200 stream whose error chunk carries the refusal: the client sees no status code at all.
                ? Sse(["{\"error\":{\"message\":" + JsonSerializer.Serialize(RefusalMessage) + "}}"])
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":" + JsonSerializer.Serialize(RefusalMessage) + "}}", Encoding.UTF8, "application/json"),
                };

        // Any vision request (the fallback describing an image) answers with the description; the knowledge review gets [].
        if (hasImage) return Sse(Delta(Description));
        if (body.Contains("curate a small knowledge base", StringComparison.Ordinal)) return Sse(Delta("[]"));
        return Sse([.. Delta("I can read the error: CS8978.")]);
    }

    private static string[] Delta(string text) =>
    [
        "{\"choices\":[{\"index\":0,\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
    ];

    private static string ModelOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private static HttpResponseMessage Sse(string[] chunks) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class ImageFallbackTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sofia-vision-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly WebApplicationFactory<Program> _factory;

    public ImageFallbackTests()
    {
        _project = Path.Combine(_home, "project");
        Directory.CreateDirectory(_project);
        VisionFallbackHandler.Bodies.Clear();
        VisionFallbackHandler.Models.Clear();
        VisionFallbackHandler.RefuseImages = false;
        VisionFallbackHandler.RefuseInStream = false;
        VisionFallbackHandler.Refusals = 0;
        // Data dir through host configuration, not the SOFIA_HOME env var: xUnit runs classes in parallel and the
        // env var is process-wide, so setting it would make another test class open the wrong database.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Sofia:DataDir", Path.Combine(_home, "data"))
            .ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
            services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new VisionFallbackHandler());
            services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new VisionFallbackHandler());
        }));
    }

    private async Task<(string ChatModel, string FallbackModel)> SetupAsync(System.Net.Http.HttpClient http)
    {
        var provider = await Post(http, "/api/providers", new { name = "VisionFake", type = "openai", baseUrl = "http://vision.llm/v1", apiKey = "test" });
        return ($"{provider.GetProperty("id").GetString()}/gpt-oss", $"{provider.GetProperty("id").GetString()}/gpt-4o");
    }

    private async Task<string> UploadPngAsync(System.Net.Http.HttpClient http)
    {
        var form = new MultipartFormDataContent();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "screenshot.png");
        var response = await http.PostAsync("/api/attachments", form);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task TextOnlyModelReceivesTheFallbackDescriptionInsteadOfTheImage()
    {
        var http = _factory.CreateClient();
        var (chat, fallback) = await SetupAsync(http);

        // The setting round-trips through the config API.
        var patched = await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = fallback });
        patched.EnsureSuccessStatusCode();
        Assert.Equal(fallback, (await http.GetFromJsonAsync<JsonElement>("/api/config")).GetProperty("imageFallbackModel").GetString());

        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var attachmentId = await UploadPngAsync(http);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "what does this screenshot say?", attachments = new[] { attachmentId }, modelId = chat, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");

        // The fallback was asked exactly once about the image.
        Assert.Single(VisionFallbackHandler.Bodies, b => b.Contains("\"model\":\"gpt-4o\"", StringComparison.Ordinal)
            && b.Contains("image_url", StringComparison.Ordinal));

        // The text model got the description as text and never received pixels.
        var chatBodies = VisionFallbackHandler.Bodies.Where(b => b.Contains("\"model\":\"gpt-oss\"", StringComparison.Ordinal)).ToList();
        Assert.Contains(chatBodies, b => b.Contains("what does this screenshot say?", StringComparison.Ordinal));
        var withImage = chatBodies.Single(b => b.Contains("what does this screenshot say?", StringComparison.Ordinal) && b.Contains("screenshot.png", StringComparison.Ordinal));
        // JSON-escaped request body: `<` arrives as \u003C, so match the tag name only.
        Assert.Contains("image_description", withImage);
        Assert.Contains(VisionFallbackHandler.Description, withImage);
        Assert.DoesNotContain("image_url", withImage, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot see images", withImage, StringComparison.Ordinal);

        // The description is persisted on the attachment and reused: a second turn analyses nothing again.
        var db = _factory.Services.GetRequiredService<Sofia.Core.Data.SofiaDb>();
        var stored = db.Attachments.FindById(attachmentId);
        Assert.Equal(VisionFallbackHandler.Description, stored?.Analysis);
        Assert.Equal("gpt-4o", stored?.AnalyzedBy);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "and now?", modelId = chat, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle" && Count(s, m => m.GetProperty("role").GetString() == "user") >= 2);
        Assert.Single(VisionFallbackHandler.Bodies, b => b.Contains("\"model\":\"gpt-4o\"", StringComparison.Ordinal)
            && b.Contains("image_url", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderRefusingImagesFallsBackToDescriptionsInsteadOfFailingTheRun()
    {
        var http = _factory.CreateClient();
        // The chat model itself claims vision in the catalog, so pixels are tried first — and refused.
        var providerId = (await SetupAsync(http)).ChatModel.Split('/')[0];
        var (chat, fallback) = ($"{providerId}/gpt-4o", $"{providerId}/llava-v1.6");
        VisionFallbackHandler.RefuseImages = true;

        var patched = await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = fallback });
        patched.EnsureSuccessStatusCode();

        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var attachmentId = await UploadPngAsync(http);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "read the error in this picture", attachments = new[] { attachmentId }, modelId = chat, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");

        Assert.Equal(1, VisionFallbackHandler.Refusals);
        var session = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
        Assert.DoesNotContain(session.GetProperty("messages").EnumerateArray(), m =>
            m.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString()!.Contains("image_url", StringComparison.Ordinal));

        // After the refusal the same model is retried blind: description in, no pixels.
        var retried = VisionFallbackHandler.Bodies.Where(b => b.Contains("\"model\":\"gpt-4o\"", StringComparison.Ordinal))
            .Where(b => b.Contains("read the error in this picture", StringComparison.Ordinal)).ToList();
        Assert.Contains(retried, b => b.Contains("image_description", StringComparison.Ordinal) && !b.Contains("image_url", StringComparison.Ordinal));
    }

    /// <summary>The shape the user actually hit: a local runtime that claims vision, accepts the request (HTTP 200),
    /// then fails mid-stream with "…does not support vision" and no status code. The step must still recover.</summary>
    [Fact]
    public async Task InStreamRefusalWithoutStatusCodeAlsoFallsBackToDescriptions()
    {
        var http = _factory.CreateClient();
        var providerId = (await SetupAsync(http)).ChatModel.Split('/')[0];
        var (chat, fallback) = ($"{providerId}/gpt-4o", $"{providerId}/llava-v1.6");
        VisionFallbackHandler.RefuseImages = true;
        VisionFallbackHandler.RefuseInStream = true;

        var patched = await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = fallback });
        patched.EnsureSuccessStatusCode();

        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var attachmentId = await UploadPngAsync(http);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "what is in this screenshot?", attachments = new[] { attachmentId }, modelId = chat, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");

        Assert.Equal(1, VisionFallbackHandler.Refusals);
        var session = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
        Assert.DoesNotContain(session.GetProperty("messages").EnumerateArray(), m =>
            m.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString()!.Contains("support vision", StringComparison.Ordinal));

        // Recovered exactly like the HTTP-400 case: description in, no pixels.
        Assert.Contains(VisionFallbackHandler.Bodies, b => b.Contains("\"model\":\"gpt-4o\"", StringComparison.Ordinal)
            && b.Contains("what is in this screenshot?", StringComparison.Ordinal)
            && b.Contains("image_description", StringComparison.Ordinal) && !b.Contains("image_url", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAFallbackTheTextOnlyModelStillJustHearsThatItCannotSee()
    {
        var http = _factory.CreateClient();
        var (chat, _) = await SetupAsync(http);

        var workspace = await Post(http, "/api/workspaces", new { kind = "folder", path = _project });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var attachmentId = await UploadPngAsync(http);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "describe my picture", attachments = new[] { attachmentId }, modelId = chat, permissionMode = "full" });
        await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");

        Assert.DoesNotContain(VisionFallbackHandler.Bodies, b => b.Contains("\"model\":\"gpt-4o\"", StringComparison.Ordinal));
        var body = VisionFallbackHandler.Bodies.Single(b => b.Contains("describe my picture", StringComparison.Ordinal) && b.Contains("screenshot.png", StringComparison.Ordinal));
        Assert.Contains("cannot see images", body);
    }

    [Fact]
    public async Task ConfigRejectsUnknownAndTextOnlyFallbackModels()
    {
        var http = _factory.CreateClient();
        var (chat, fallback) = await SetupAsync(http);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = "nope/nothing" })).StatusCode);
        // gpt-oss is catalog-known as text-only, so it cannot be the eyes of another model.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = chat })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = fallback })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync("/api/config", new { imageFallbackModel = "" })).StatusCode);
        Assert.Equal("", (await http.GetFromJsonAsync<JsonElement>("/api/config")).GetProperty("imageFallbackModel").GetString());
    }

    private static int Count(JsonElement session, Func<JsonElement, bool> predicate) =>
        session.GetProperty("messages").EnumerateArray().Count(predicate);

    private static async Task<JsonElement> Post(System.Net.Http.HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} → {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task WaitFor(System.Net.Http.HttpClient http, string sessionId, Func<JsonElement, bool> predicate, int tries = 150)
    {
        JsonElement last = default;
        for (var i = 0; i < tries; i++)
        {
            last = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            if (predicate(last)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Session never reached the expected state: {last}");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_home, true); } catch { /* the database may still hold the file briefly */ }
    }
}

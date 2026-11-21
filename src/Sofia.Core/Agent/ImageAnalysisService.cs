using System.Text;
using System.Collections.Concurrent;
using Sofia.Core.Data;
using Sofia.Contracts.Domain;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;

namespace Sofia.Core.Agent;

/// <summary>
/// Fallback vision: when the conversation model cannot see images (text-only model, or a provider that rejected image
/// input), one configured vision model describes each image once and the description is what reaches the conversation
/// instead of the pixels. Descriptions live on the attachment document, so an image is analysed a single time and never
/// re-billed by later steps; changing the configured fallback re-analyses on next use.
/// A missing or broken fallback never fails a run: the history falls back to the old "the current model cannot see images" note.
/// </summary>
public sealed class ImageAnalysisService(SofiaDb db, ProviderService providers, ModelGate gate, ILogger<ImageAnalysisService> log)
{
    /// <summary>Largest image sent to the fallback model; matches the limit the request builder uses for pixels.</summary>
    public const long MaxImageBytes = 15 * 1024 * 1024;

    private const int MaxDescriptionTokens = 1200;

    /// <summary>In-flight analyses by attachment id, so parallel sessions (or two mentions of one image) describe it once.</summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _running = [];

    /// <summary>The configured fallback, or null when it is unset, unresolvable, disabled, oversized for the job, or the chat model itself.</summary>
    public (ProviderConfig Provider, ModelInfo Model)? Resolve(string? chatModelId)
    {
        var configured = db.GetConfig().ImageFallbackModel;
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (string.Equals(configured, chatModelId, StringComparison.Ordinal)) return null;
        var resolved = providers.Resolve(configured);
        if (resolved is null || !resolved.Value.Provider.Enabled) return null;
        // Never describe a text-only model's blind spot with another text-only model.
        if (!resolved.Value.Model.CanSeeImages) return null;
        return resolved.Value;
    }

    private ProviderService Providers => providers;
    private ModelGate Gate => gate;

    /// <summary>Starts one run's view of the fallback: answers the history builder synchronously and collects what is still missing.</summary>
    public Runner For(ProviderConfig provider, ModelInfo model) => new(this, provider, model);

    /// <summary>One run's view of the fallback: a per-run cache of descriptions plus the ids still to analyse.</summary>
    public sealed class Runner(ImageAnalysisService service, ProviderConfig provider, ModelInfo model)
    {
        private readonly Dictionary<string, string?> _cache = [];

        /// <summary>Model label recorded on the analysis; a different fallback re-analyses the image.</summary>
        public string AnalyzerLabel => model.Label;

        /// <summary>
        /// What the history builder calls: the stored description of this image, or null when there is none. Images
        /// asked about without a description are remembered so the run can analyse them and build the history again.
        /// </summary>
        public string? Describe(Attachment image)
        {
            if (_cache.TryGetValue(image.Id, out var cached)) return cached;
            var stored = service.Stored(image.Id, AnalyzerLabel);
            _cache[image.Id] = stored;
            if (stored is null) Pending.Add(image.Id);
            return stored;
        }

        /// <summary>Attachment ids the history asked for that still have no description.</summary>
        public List<string> Pending { get; } = [];

        /// <summary>Describes every pending image with the fallback model (serialised on its slot) and updates the cache.</summary>
        public async Task AnalyzePendingAsync(string sessionId, CancellationToken ct)
        {
            var ids = Pending.Distinct().ToList();
            Pending.Clear();
            if (ids.Count == 0) return;
            IDisposable? slot = null;
            try
            {
                // A single-client runtime serves one stream at a time: wait our turn instead of failing the image.
                if (service.Providers.IsSingleClient(model.Id))
                    slot = await service.Gate.AcquireSlotAsync(model.Id, sessionId, ct);
                foreach (var id in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    _cache[id] = await service.AnalyzeAsync(id, provider, model, AnalyzerLabel, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The run was aborted mid-analysis: the next step retries these images.
            }
            finally
            {
                slot?.Dispose();
            }
        }
    }

    /// <summary>The description stored for this attachment, when the same fallback model produced it.</summary>
    private string? Stored(string attachmentId, string analyzerLabel)
    {
        var stored = db.Attachments.FindById(attachmentId);
        return stored?.Analysis is { Length: > 0 } analysis && stored.AnalyzedBy == analyzerLabel ? analysis : null;
    }

    /// <summary>Asks the fallback model about one attachment and persists the answer; null (never a throw) when it cannot.</summary>
    private async Task<string?> AnalyzeAsync(string attachmentId, ProviderConfig provider, ModelInfo model, string analyzerLabel, CancellationToken ct)
        => await _running.GetOrAdd(attachmentId,
            _ => DescribeOnceAsync(attachmentId, provider, model, analyzerLabel, ct));

    private async Task<string?> DescribeOnceAsync(string attachmentId, ProviderConfig provider, ModelInfo model, string analyzerLabel, CancellationToken ct)
    {
        try
        {
            var attachment = db.Attachments.FindById(attachmentId);
            if (attachment is null || !File.Exists(attachment.StoragePath)) return null;
            if (attachment.Bytes > MaxImageBytes)
            {
                log.LogInformation("Image {Attachment} ({Size}) is too large for fallback analysis", attachment.Id, attachment.Size);
                return null;
            }

            var request = new ChatRequest
            {
                Model = model.ModelId,
                System = DescriptionPrompt,
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content =
                        [
                            ContentPart.FromText($"Describe the attached image ({attachment.Name})."),
                            new ContentPart { ImageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(attachment.StoragePath, ct)), ImageMime = attachment.Mime },
                        ],
                    },
                ],
                Reasoning = "off",
                MaxTokens = Math.Min(MaxDescriptionTokens, model.MaxOutput),
                ModelSupportsReasoning = false,
            };

            var answer = new StringBuilder();
            await foreach (var ev in providers.CreateClient(provider, model.ModelId).StreamAsync(request, ct))
                if (ev is TextDelta t) answer.Append(t.Text);

            var description = answer.ToString().Trim();
            if (description.Length == 0) return null;

            // Persist on the standalone document: messages embed their own copy of the attachment, so they stay verbatim.
            attachment.Analysis = description;
            attachment.AnalyzedBy = analyzerLabel;
            db.Attachments.Upsert(attachment);
            log.LogInformation("Described image {Attachment} with fallback model {Model} ({Chars} chars)", attachment.Id, analyzerLabel, description.Length);
            return description;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A broken fallback must never break the conversation: the history keeps its "cannot see images" note.
            log.LogWarning("Fallback image analysis with {Model} failed: {Error}", analyzerLabel, Text.FirstLine(ex.Message, 200));
            return null;
        }
        finally
        {
            _running.TryRemove(attachmentId, out _);
        }
    }

    private const string DescriptionPrompt = """
        You describe images for an assistant that cannot see them. Your description is the only thing it ever learns about the picture, so report what is actually there, completely and factually:
        - transcribe every visible text verbatim (UI labels, error messages, code, numbers, table cells), keeping the reading order;
        - name the layout and the elements (buttons, dialogs, charts, terminals, people, objects) and where they are;
        - state exact values, states and errors — an error code or a colour can be the whole answer.

        Do not interpret, speculate, recommend anything, or answer questions the picture seems to contain: you are the eyes, not the brain. Plain text or Markdown, as long as the image needs.
        """;
}

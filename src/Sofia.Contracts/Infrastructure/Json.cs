using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sofia.Contracts.Infrastructure;

/// <summary>
/// The wire contract: REST bodies, SignalR payloads and the per-session JSON columns. It has to stay byte-identical
/// whichever host serializes it, so every host configures its serializer from this one place.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }

    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PropertyNameCaseInsensitive = true;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    }

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

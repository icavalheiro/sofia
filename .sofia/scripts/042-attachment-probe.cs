// Spec 042 evidence: POST /api/attachments against the running server, exactly the call the composer makes on submit
// (ISofiaBackend.UploadAsync → multipart field "file"), then GET the returned id back. No session, no prompt: the probe
// never touches the user's conversations.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5199") };
var name = $"042-attachment-probe-{DateTime.UtcNow:HHmmss}.txt";
var bytes = Encoding.UTF8.GetBytes("spec 042 composer attachment probe");
using var form = new MultipartFormDataContent();
var file = new ByteArrayContent(bytes);
file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
form.Add(file, "file", name);

var upload = await client.PostAsync("/api/attachments", form);
Console.WriteLine($"POST /api/attachments -> {(int)upload.StatusCode} {upload.StatusCode}");
var body = await upload.Content.ReadAsStringAsync();
Console.WriteLine(body);
if (!upload.IsSuccessStatusCode) return 1;

using var document = JsonDocument.Parse(body);
var id = document.RootElement.GetProperty("id").GetString();
var kind = document.RootElement.GetProperty("kind").GetString();
var size = document.RootElement.GetProperty("bytes").GetInt64();
Console.WriteLine($"id={id} kind={kind} bytes={size} (expected {bytes.Length})");

var fetched = await client.GetByteArrayAsync($"/api/attachments/{id}");
Console.WriteLine($"GET /api/attachments/{id} -> {fetched.Length} bytes, roundtrip={fetched.AsSpan().SequenceEqual(bytes)}");
return fetched.AsSpan().SequenceEqual(bytes) && kind == "file" && size == bytes.Length ? 0 : 1;

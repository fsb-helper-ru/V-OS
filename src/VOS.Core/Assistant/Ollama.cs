using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VOS.Core.Assistant;

/// <summary>A chat turn in a provider-neutral form (Ollama and the on-device assistant use plain text turns).</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>Raised for Ollama failures with a message written for the user.</summary>
public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal client for an Ollama server's /api/chat endpoint with streaming (newline-delimited JSON). Ollama runs
/// models on the user's machine; a non-loopback endpoint is treated like a cloud provider by the assistant (consent).
/// </summary>
public static class OllamaClient
{
    private static readonly HttpClient s_http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool IsLoopback(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    public static bool IsValidEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);

    /// <summary>Streams the assistant's reply as text fragments.</summary>
    public static async IAsyncEnumerable<string> ChatAsync(string endpoint, string model, IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsValidEndpoint(endpoint)) throw new OllamaException("The Ollama server address is not a valid http or https URL.");
        var url = endpoint.TrimEnd('/') + "/api/chat";
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));

        HttpResponseMessage response;
        // Loading a model can take a while on first use; headers must still arrive within two minutes.
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headers.CancelAfter(TimeSpan.FromMinutes(2));
            try { response = await s_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OllamaException("Ollama did not answer within two minutes."); }
            catch (HttpRequestException ex) { throw new OllamaException($"Could not reach Ollama at {endpoint}. Make sure it is running.", ex); }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var error = ParseLine(text).Error;
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new OllamaException($"The model “{model}” is not installed on the Ollama server. Install it with: ollama pull {model}");
                throw new OllamaException(error ?? $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                var (content, done, err) = ParseLine(line);
                if (err != null) throw new OllamaException(err);
                if (!string.IsNullOrEmpty(content)) yield return content;
                if (done) yield break;
            }
        }
    }

    /// <summary>Parses one NDJSON line: {"message":{"content":"..."},"done":false} or {"error":"..."}.</summary>
    public static (string? Content, bool Done, string? Error) ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false, null);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return (null, true, e.GetString());
            string? content = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            bool done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            return (content, done, null);
        }
        catch (JsonException) { return (null, false, null); }
    }
}

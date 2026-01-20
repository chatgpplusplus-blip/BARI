using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BARI_web.Services;

public sealed class OllamaChatClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public OllamaChatClient(HttpClient http, IConfiguration cfg)
    {
        _http = http;

        var baseUrl = cfg["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(baseUrl);

        if (int.TryParse(cfg["Ollama:TimeoutSeconds"], out var sec) && sec > 0)
            _http.Timeout = TimeSpan.FromSeconds(sec);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public sealed record Msg(string Role, string Content);

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
        public sealed class ChatMessage
        {
            public string? Role { get; set; }
            public string? Content { get; set; }
        }
    }

    /// <summary>
    /// Llama a POST /api/chat (Ollama). Si pasas format: "json" o un JSON Schema, forzas salida estructurada.
    /// </summary>
    public async Task<string> ChatAsync(
        string model,
        IReadOnlyList<Msg> messages,
        object? format,
        Dictionary<string, object?>? options,
        CancellationToken ct)
    {
        var req = new
        {
            model,
            stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            format,   // "json" o JSON schema object
            options   // temperature, num_predict, etc.
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, _json, ct);
        resp.EnsureSuccessStatusCode();

        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(_json, ct);
        return parsed?.Message?.Content?.Trim() ?? "";
    }
}

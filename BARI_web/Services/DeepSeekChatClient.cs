using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BARI_web.Services;

public sealed class DeepSeekChatClient
{
    private readonly HttpClient _http;
    private readonly DeepSeekOptions _opt;
    private readonly ILogger<DeepSeekChatClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public DeepSeekChatClient(HttpClient http, IOptions<DeepSeekOptions> opt, ILogger<DeepSeekChatClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<string> CreateChatCompletionAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        bool jsonMode = false,
        int? maxTokens = null,
        double temperature = 0.2,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey) && _http.DefaultRequestHeaders.Authorization is null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = false,
            ["temperature"] = temperature
        };

        if (maxTokens is not null) payload["max_tokens"] = maxTokens.Value;

        // JSON Mode (DeepSeek pide response_format + que el prompt incluya la palabra "json")
        if (jsonMode)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("DeepSeek HTTP {Status}: {Body}", (int)resp.StatusCode, raw);
            throw new HttpRequestException($"DeepSeek error {(int)resp.StatusCode}: {raw}");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(raw, JsonOpts)
                     ?? throw new Exception("DeepSeek response inválida (no JSON).");

        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        return StripJsonCodeFenceIfAny(content);
    }

    public async Task<T> CreateJsonAsync<T>(
        string model,
        IReadOnlyList<ChatMessage> messages,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        // Asegura que el prompt mencione json (DeepSeek lo recomienda/require en JSON mode)
        if (!messages.Any(m => m.Content.Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            messages = messages.Concat(new[]
            {
                new ChatMessage { Role = "system", Content = "Responde SOLO en json válido." }
            }).ToList();
        }

        var txt = await CreateChatCompletionAsync(
            model: model,
            messages: messages,
            jsonMode: true,
            maxTokens: maxTokens,
            temperature: 0.0,
            ct: ct
        );

        try
        {
            return JsonSerializer.Deserialize<T>(txt, JsonOpts)
                   ?? throw new Exception("JSON vacío/ inválido.");
        }
        catch (Exception ex)
        {
            throw new Exception($"No pude parsear JSON del modelo. Texto recibido:\n{txt}", ex);
        }
    }

    private static string StripJsonCodeFenceIfAny(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            // ```json ... ```
            var firstNewLine = t.IndexOf('\n');
            if (firstNewLine >= 0) t = t[(firstNewLine + 1)..];
            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) t = t[..lastFence];
        }
        return t.Trim();
    }

    private sealed class ChatCompletionsResponse
    {
        public Choice[]? Choices { get; set; }
    }

    private sealed class Choice
    {
        public Msg? Message { get; set; }
    }

    private sealed class Msg
    {
        public string? Content { get; set; }
    }
}

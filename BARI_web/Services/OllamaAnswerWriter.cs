using System.Text.Json;

namespace BARI_web.Services;

public sealed class OllamaAnswerWriter
{
    private readonly IConfiguration _cfg;
    private readonly OllamaChatClient _ollama;

    public OllamaAnswerWriter(IConfiguration cfg, OllamaChatClient ollama)
    {
        _cfg = cfg;
        _ollama = ollama;
    }

    public async Task<string> WriteAsync(string userQuestion, object dbResult, CancellationToken ct)
    {
        var model = _cfg["Ollama:ModelWriter"] ?? "gemma3:latest";

        var system = """
Eres un asistente de inventario (solo lectura).
Responde en español, claro y corto.
Si hay lista, usa bullets.
Si no hay datos, dilo y sugiere cómo refinar la búsqueda.
No inventes campos que no estén en el JSON.
""";

        var payload = JsonSerializer.Serialize(dbResult);

        var prompt = $"""
PREGUNTA:
{userQuestion}

RESULTADO_DB (JSON):
{payload}
""";

        var text = await _ollama.ChatAsync(
            model: model,
            messages: new[]
            {
                new OllamaChatClient.Msg("system", system),
                new OllamaChatClient.Msg("user", prompt),
            },
            format: null,
            options: new Dictionary<string, object?> { ["temperature"] = 0.2, ["num_predict"] = 500 },
            ct: ct
        );

        return string.IsNullOrWhiteSpace(text)
            ? "No pude generar una respuesta. Intenta reformular la pregunta."
            : text.Trim();
    }
}

using System.Text.Json;

namespace BARI_web.Services;

public sealed class BariIntentRouter
{
    private readonly IConfiguration _cfg;
    private readonly OllamaChatClient _ollama;

    public BariIntentRouter(IConfiguration cfg, OllamaChatClient ollama)
    {
        _cfg = cfg;
        _ollama = ollama;
    }

    public sealed class RoutePlan
    {
        public string Route { get; set; } = "db"; // db | web | mixed
        public bool NeedsClarification { get; set; }
        public string? ClarifyingQuestion { get; set; }
        public decimal Confidence { get; set; }
    }

    public async Task<RoutePlan> RouteAsync(string userQuestion, CancellationToken ct)
    {
        var model = _cfg["Ollama:ModelPlanner"] ?? "gemma3:latest";

        var system = """
Clasifica la intención del usuario para un asistente de laboratorio.
Devuelve SOLO JSON.
Rutas:
- db: inventario/ubicación/calibraciones/sustancias/equipos/documentos dentro del sistema.
- web: requiere info externa (definiciones generales, normativa externa, etc.) que no está en BD/documentos.
- mixed: mezcla (ej: "tengo X?" + "qué precauciones generales debo tomar?").
""";

        var schema = new
        {
            type = "object",
            required = new[] { "route", "needsClarification", "confidence" },
            properties = new
            {
                route = new { type = "string" },
                needsClarification = new { type = "boolean" },
                clarifyingQuestion = new { type = "string" },
                confidence = new { type = "number" }
            }
        };

        var content = await _ollama.ChatAsync(
            model,
            new[]
            {
                new OllamaChatClient.Msg("system", system),
                new OllamaChatClient.Msg("user", userQuestion),
            },
            format: schema,
            options: new Dictionary<string, object?> { ["temperature"] = 0 },
            ct
        );

        try
        {
            return JsonSerializer.Deserialize<RoutePlan>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new RoutePlan { Route = "db", Confidence = 0.5m };
        }
        catch
        {
            return new RoutePlan { Route = "db", Confidence = 0.5m };
        }
    }
}

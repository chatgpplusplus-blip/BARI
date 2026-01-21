using Microsoft.Extensions.Options;

namespace BARI_web.Services;

public sealed class BariIntentRouter
{
    private readonly DeepSeekChatClient _llm;
    private readonly DeepSeekOptions _opt;

    public BariIntentRouter(DeepSeekChatClient llm, IOptions<DeepSeekOptions> opt)
    {
        _llm = llm;
        _opt = opt.Value;
    }

    public async Task<RouterDecision> DecideAsync(string userQuestion, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        var q = (userQuestion ?? "").Trim();
        if (q.Length < 2)
            return new RouterDecision { Intent = "needs_clarification", ClarifyingQuestion = "¿Qué quieres consultar exactamente del inventario o del laboratorio?" };

        var lower = q.ToLowerInvariant();

        var looksDb =
            lower.Contains("cuánt") || lower.Contains("cuantos") || lower.Contains("cantidad") ||
            lower.Contains("inventario") || lower.Contains("tenemos") || lower.Contains("hay ") ||
            lower.Contains("dónde") || lower.Contains("donde") || lower.Contains("ubic") ||
            lower.Contains("venc") || lower.Contains("qr") || lower.Contains("cas") ||
            lower.Contains("equipo") || lower.Contains("reactiv") || lower.Contains("sustanc") ||
            lower.Contains("documento") || lower.Contains("material") ||
            lower.Contains("mesón") || lower.Contains("mesones") ||
            lower.Contains("área") || lower.Contains("areas") || lower.Contains("laboratorio");

        if (looksDb)
            return new RouterDecision { Intent = "db_query", Notes = "Heurística: parece consulta del inventario/BD." };

        // fallback: pregunta al LLM
        var msgs = new List<ChatMessage>
        {
            new() { Role="system", Content=
@"Responde en json.
Clasifica la intención del usuario en:
- db_query
- general_help
- needs_clarification

Devuelve SOLO:
{ ""intent"": ""db_query|general_help|needs_clarification"", ""clarifying_question"": null, ""notes"": ""breve"" }"
            },
            new() { Role="user", Content = q }
        };

        var decision = await _llm.CreateJsonAsync<RouterDecision>(_opt.ModelPlanner, msgs, maxTokens: 180, ct: ct);
        decision.Intent = NormalizeIntent(decision.Intent);

        if (decision.Intent == "needs_clarification" && string.IsNullOrWhiteSpace(decision.ClarifyingQuestion))
            decision.ClarifyingQuestion = "¿Puedes dar un poco más de detalle para poder consultarlo en la base de datos?";

        return decision;
    }

    private static string NormalizeIntent(string? intent)
    {
        var x = (intent ?? "").Trim().ToLowerInvariant();
        return x switch
        {
            "db" or "database" or "dbquery" or "db_query" => "db_query",
            "general" or "general_help" => "general_help",
            "clarify" or "needs_clarification" => "needs_clarification",
            _ => "general_help"
        };
    }
}

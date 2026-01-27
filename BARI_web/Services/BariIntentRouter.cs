using System.Linq;
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
        var hasHistory = history?.Any(m => m.Role == "assistant") == true;
        var looksFollowUp = hasHistory && (
            lower.Contains("ambos") || lower.Contains("ambas") ||
            lower.Contains("estos") || lower.Contains("estas") ||
            lower.Contains("esos") || lower.Contains("esas") ||
            lower.Contains("ese") || lower.Contains("esa") ||
            lower.Contains("mismo") || lower.Contains("misma") ||
            lower.Contains("diferenc") || lower.Contains("compar") ||
            lower.Contains("revisa") || lower.Contains("detall") ||
            lower.Contains("datos") || lower.Contains("anterior") ||
            lower.Contains("lo de arriba") || lower.Contains("de arriba"));

        var looksDb =
    lower.Contains("cuánt") || lower.Contains("cuantos") || lower.Contains("cantidad") ||
    lower.Contains("inventario") || lower.Contains("tenemos") || lower.Contains("hay ") ||
    lower.Contains("dónde") || lower.Contains("donde") || lower.Contains("ubic") ||
    lower.Contains("venc") || lower.Contains("qr") || lower.Contains("cas") ||
    lower.Contains("equipo") || lower.Contains("reactiv") || lower.Contains("sustanc") ||
    lower.Contains("documento") || lower.Contains("material") ||
    lower.Contains("mesón") || lower.Contains("mesones") ||
    lower.Contains("área") || lower.Contains("areas") || lower.Contains("laboratorio") ||

    // ✅ NUEVO: infraestructura / layout
    lower.Contains("instalacion") || lower.Contains("instalaciones") || lower.Contains("infraestructura") ||
    lower.Contains("ducha") || lower.Contains("lavaplatos") || lower.Contains("campana") || lower.Contains("extractor") ||
    lower.Contains("aire acondicionado") || lower.Contains("tomacorriente") ||
    lower.Contains("gas") || lower.Contains("ethernet") || lower.Contains("wifi") || lower.Contains("access point") ||
    lower.Contains("puerta") || lower.Contains("ventana") ||
    lower.Contains("canvas") || lower.Contains("plano") || lower.Contains("layout") ||
    lower.Contains("poligono") || lower.Contains("coorden") ||
    lower.Contains("planta") ||
    lower.Contains("mantenimiento") || lower.Contains("revision") || lower.Contains("próxima revisión") || lower.Contains("proxima revision");

        var looksWeb =
    lower.Contains("busca en internet") || lower.Contains("google") || lower.Contains("web") ||
    lower.Contains("en línea") || lower.Contains("online") || lower.Contains("link") ||
    lower.Contains("artículo") || lower.Contains("paper") || lower.Contains("pdf") ||
    lower.Contains("últimas") || lower.Contains("noticias") || lower.Contains("precio en el mercado");

        if (looksWeb)
            return new RouterDecision { Intent = "web_search", Notes = "Heurística: el usuario pide búsqueda web." };

        if (looksFollowUp)
            return new RouterDecision { Intent = "db_query", Notes = "Heurística: pregunta de seguimiento con contexto previo." };

        if (looksDb)
            return new RouterDecision { Intent = "db_query", Notes = "Heurística: parece consulta del inventario/BD." };

        // fallback: pregunta al LLM
        var msgs = new List<ChatMessage>
        {
            new() { Role="system", Content=
@"Responde en json.
Clasifica la intención del usuario en:
- db_query
- web_search
- general_help
- needs_clarification

Devuelve SOLO:
{ ""intent"": ""db_query|web_search|general_help|needs_clarification"", ""clarifying_question"": null, ""notes"": ""breve"" }"

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
            "web" or "web_search" or "internet" or "browse" or "search" => "web_search",
            "general" or "general_help" => "general_help",
            "clarify" or "needs_clarification" => "needs_clarification",
            _ => "general_help"
        };

    }
}

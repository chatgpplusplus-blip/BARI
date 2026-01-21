using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BARI_web.Services;

public sealed class DeepSeekAnswerWriter
{
    private readonly DeepSeekChatClient _llm;
    private readonly DeepSeekOptions _opt;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DeepSeekAnswerWriter(DeepSeekChatClient llm, IOptions<DeepSeekOptions> opt)
    {
        _llm = llm;
        _opt = opt.Value;
    }

    public async Task<string> WriteGeneralHelpAsync(string userQuestion, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        var msgs = new List<ChatMessage>
        {
            new() { Role="system", Content =
@"Eres BariBot, asistente de un sistema de inventario de laboratorio (química y electrónica).
Responde en español claro y práctico. Si no tienes acceso a datos concretos, dilo.
No inventes cantidades exactas."
            }
        };

        if (history is not null)
        {
            foreach (var h in history.TakeLast(Math.Min(_opt.HistoryWindow, history.Count)))
                if (h.Role is "user" or "assistant")
                    msgs.Add(new ChatMessage { Role = h.Role, Content = h.Content });
        }

        msgs.Add(new ChatMessage { Role = "user", Content = userQuestion });

        return await _llm.CreateChatCompletionAsync(_opt.ModelWriter, msgs, jsonMode: false, maxTokens: 600, temperature: 0.35, ct: ct);
    }

    public async Task<string> WriteFromDbSqlAsync(string userQuestion, SqlPlan plan, DbQueryResult data, CancellationToken ct = default)
    {
        // Resumen compacto para no mandar tablas enormes al modelo
        object summary = data.ScalarCount is not null
            ? new { kind = "scalar", count = data.ScalarCount, sql = plan.Sql, explain = plan.Explain }
            : new { kind = "rows", columns = data.Columns, rows = data.Rows.Take(20).ToList(), sql = plan.Sql, explain = plan.Explain };

        var summaryJson = JsonSerializer.Serialize(summary, JsonOpts);

        var msgs = new List<ChatMessage>
        {
            new() { Role="system", Content =
@"Eres BariBot (inventario de laboratorio).
Responde SOLO con base en los datos recibidos (json).
- Si hay count: di el número claramente.
- Si hay filas: muestra máximo 10 items en lista, bien legible.
Si no hay resultados: dilo y sugiere filtros (nombre, id, área, laboratorio_id, etc.).
No inventes datos."
            },
            new() { Role="user", Content = $"Pregunta: {userQuestion}\n\nDatos (json):\n{summaryJson}"}
        };

        return await _llm.CreateChatCompletionAsync(_opt.ModelWriter, msgs, jsonMode: false, maxTokens: 650, temperature: 0.2, ct: ct);
    }
}

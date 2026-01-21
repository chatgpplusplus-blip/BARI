using Microsoft.Extensions.Options;

namespace BARI_web.Services;

public sealed class BariBotOrchestrator
{
    private readonly BariIntentRouter _router;
    private readonly DeepSeekSqlPlanner _planner;
    private readonly PostgresReadOnlyExecutor _db;
    private readonly DeepSeekAnswerWriter _writer;
    private readonly ILogger<BariBotOrchestrator> _log;

    public BariBotOrchestrator(
        BariIntentRouter router,
        DeepSeekSqlPlanner planner,
        PostgresReadOnlyExecutor db,
        DeepSeekAnswerWriter writer,
        ILogger<BariBotOrchestrator> log)
    {
        _router = router;
        _planner = planner;
        _db = db;
        _writer = writer;
        _log = log;
    }

    public async Task<BariBotResponse> AskAsync(string userText, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        var response = new BariBotResponse();

        // 1) Router: ¿BD, ayuda general o aclarar?
        var decision = await _router.DecideAsync(userText, history, ct);
        response.Decision = decision;

        if (decision.Intent == "needs_clarification")
        {
            response.UsedDatabase = false;
            response.Answer = decision.ClarifyingQuestion ?? "¿Puedes dar un poco más de detalle?";
            return response;
        }

        if (decision.Intent == "general_help")
        {
            response.UsedDatabase = false;
            response.Answer = await _writer.WriteGeneralHelpAsync(userText, history, ct);
            return response;
        }

        // 2) Planificador multi-paso (puede devolver varios steps SQL)
        SqlActionPlan plan;
        try
        {
            plan = await _planner.PlanAsync(userText, history, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error generando el plan de consulta.");
            response.UsedDatabase = false;
            response.Answer = "Tuve un problema generando el plan para esa consulta. Intenta reformularla o hacerlo más específico.";
            return response;
        }

        // Guardamos un plan 'simple' para exponer en la respuesta (compatibilidad con SqlPlan / UI)
        var uiPlan = new SqlPlan
        {
            Intent = plan.Intent,
            ClarifyingQuestion = plan.ClarifyingQuestion,
            Explain = plan.Explain,
            Sql = null // lo rellenaremos más abajo si hay SQL
        };
        response.Plan = uiPlan;

        // Intenciones especiales desde el planner
        if (plan.Intent == "needs_clarification")
        {
            response.UsedDatabase = false;
            response.Answer = plan.ClarifyingQuestion ?? "Me falta un dato para consultar en la base. ¿Puedes especificar un poco más?";
            return response;
        }

        if (plan.Intent == "general_help")
        {
            response.UsedDatabase = false;
            response.Answer = await _writer.WriteGeneralHelpAsync(userText, history, ct);
            return response;
        }

        // 3) Ejecutar todos los pasos SQL que el plan haya definido
        var sqlSteps = plan.Steps
            .Where(s => s.Type == "sql" && !string.IsNullOrWhiteSpace(s.Sql))
            .ToList();

        if (sqlSteps.Count == 0)
        {
            response.UsedDatabase = false;
            response.Answer = "No pude generar ninguna consulta SQL útil para eso. Intenta reformular con nombre/ID/área/laboratorio o describe mejor lo que buscas.";
            return response;
        }

        var allResults = new List<(SqlActionStep step, DbQueryResult result)>();

        try
        {
            foreach (var step in sqlSteps)
            {
                var sql = step.Sql!;
                _log.LogInformation("Ejecutando step SQL '{Name}': {Sql}", step.Name ?? step.Description ?? "sin_nombre", sql.Replace("\n", " "));
                var data = await _db.ExecuteSqlAsync(sql, ct);
                allResults.Add((step, data));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error ejecutando una de las consultas del plan.");
            response.UsedDatabase = false;
            response.Answer = "Tuve un problema al consultar la base de datos para esa pregunta. Puedes intentar reformularla o dividirla en pasos más simples.";
            return response;
        }

        response.UsedDatabase = true;

        // 4) Empaquetar resultados para el writer
        if (allResults.Count == 1)
        {
            // Caso simple: 1 sola consulta → fluye igual que antes
            var (step, data) = allResults[0];

            uiPlan.Sql = step.Sql; // para logging/resumen que verá el writer
            response.Plan = uiPlan;
            response.Data = data;

            response.Answer = await _writer.WriteFromDbSqlAsync(userText, uiPlan, data, ct);
            return response;
        }
        else
        {
            // Caso multi-consulta:
            // Creamos un DbQueryResult sintético que contiene filas donde cada fila representa
            // (step_name, description, sql, columns, rows) de cada consulta ejecutada.
            var multi = new DbQueryResult();
            multi.Columns.AddRange(new[] { "step", "name", "description", "sql", "columns", "rows" });

            int index = 0;
            foreach (var (step, data) in allResults)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["step"] = index,
                    ["name"] = step.Name ?? $"step_{index}",
                    ["description"] = step.Description,
                    ["sql"] = step.Sql,
                    ["columns"] = data.Columns,
                    ["rows"] = data.Rows
                };

                multi.Rows.Add(row);
                index++;
            }

            // Unimos los SQL para que el writer tenga contexto de lo que se ejecutó
            uiPlan.Sql = string.Join(";\n\n", sqlSteps.Select(s => s.Sql));
            uiPlan.Explain ??= $"Plan con {sqlSteps.Count} consultas SQL ejecutadas.";

            response.Plan = uiPlan;
            response.Data = multi;

            // Reutilizamos el writer actual, que solo ve un JSON con columns+rows,
            // pero ahora cada fila contiene el resultado de un step (nested).
            response.Answer = await _writer.WriteFromDbSqlAsync(userText, uiPlan, multi, ct);
            return response;
        }
    }
}

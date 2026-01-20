using BARI_web.Models;

namespace BARI_web.Services;

public sealed class BariBotOrchestrator
{
    private readonly BariIntentRouter _router;
    private readonly OllamaSqlPlanner _planner;
    private readonly PostgresReadOnlyExecutor _db;
    private readonly OllamaAnswerWriter _writer;

    public BariBotOrchestrator(
        BariIntentRouter router,
        OllamaSqlPlanner planner,
        PostgresReadOnlyExecutor db,
        OllamaAnswerWriter writer)
    {
        _router = router;
        _planner = planner;
        _db = db;
        _writer = writer;
    }

    public async Task<BariBotResponse> AskAsync(string question, int labId, CancellationToken ct)
    {
        // Ayuda rápida (sin llamar al modelo)
        var q = (question ?? "").ToLowerInvariant();
        if (q.Contains("que puedo preguntar") || q.Contains("qué puedo preguntar") || q.Contains("ayuda"))
        {
            return new BariBotResponse
            {
                Text = """
Puedes preguntarme cosas como:
- Contenedores que vencen en X días
- Sustancias por nombre comercial / CAS
- Equipos que requieren calibración y su próxima fecha
- Ubicación: área / mesón / nivel / posición
- Códigos H/P y pictogramas GHS
- Documentos por título o procedencia
"""
            };
        }

        var route = await _router.RouteAsync(question, ct);

        // Por ahora: si el router duda, igual intentamos DB
        if (route.NeedsClarification)
            route.Route = "db";

        if (route.Route.Equals("web", StringComparison.OrdinalIgnoreCase))
        {
            return new BariBotResponse
            {
                Text = "Esta pregunta requiere Internet, pero esa ruta todavía no está conectada. Si quieres, puedo buscar en tu base de datos y documentos cargados."
            };
        }

        // Plan SQL
        var plan = await _planner.PlanAsync(question, labId, ct);

        if (plan.NeedsClarification)
        {
            return new BariBotResponse
            {
                Text = plan.ClarifyingQuestion ?? "¿Qué exactamente quieres buscar/listar?"
            };
        }

        // Normaliza SQL (convierte laboratorio_id=1 -> @lab_id, quita ';')
        var sql = PostgresReadOnlyExecutor.NormalizeSqlForPolicy(plan.Sql ?? "");

        if (!PostgresReadOnlyExecutor.IsAllowedSql(sql))
        {
            return new BariBotResponse
            {
                Text = "Esa consulta no cumple la política (solo lectura y filtro por laboratorio). ¿Puedes ser más específico?",
                DebugSql = sql
            };
        }

        var rows = await _db.QueryAsync(sql, plan.Parameters, trustedLabId: labId, ct);
        var answer = await _writer.WriteAsync(question, rows, ct);

        return new BariBotResponse
        {
            Text = answer,
            DebugSql = PostgresReadOnlyExecutor.EnsureLimit(sql, 100)
        };
    }
}

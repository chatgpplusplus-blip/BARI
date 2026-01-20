using System.Text.Json;
using BARI_web.Models;

namespace BARI_web.Services;

public sealed class OllamaSqlPlanner
{
    private readonly IConfiguration _cfg;
    private readonly OllamaChatClient _ollama;

    public OllamaSqlPlanner(IConfiguration cfg, OllamaChatClient ollama)
    {
        _cfg = cfg;
        _ollama = ollama;
    }

    public async Task<SqlPlan> PlanAsync(string userQuestion, int labId, CancellationToken ct)
    {
        var model = _cfg["Ollama:ModelPlanner"] ?? "gemma3:latest";

        var schemaHint = """
TABLAS POR LABORATORIO (DEBEN filtrar laboratorio_id = @lab_id):
- contenedores(cont_id, sustancia_id, fecha_vencimiento, area_id, meson_id, nivel, posicion, laboratorio_id)
- sustancias(sustancia_id, nombre_comercial, nombre_quimico, cas, laboratorio_id)
- equipos(equipo_id, nombre, area_id, meson_id, nivel, posicion, laboratorio_id, requiere_calibracion)
- areas(area_id, nombre_areas, laboratorio_id)
- documentos(documento_id, titulo, url, archivo_local, procedencia, laboratorio_id)
- calibraciones(cal_id, equipo_id, fecha, proxima_fecha, proveedor, costo)  (filtra via JOIN equipos.laboratorio_id=@lab_id)

TABLAS GLOBALES (NO filtrar por lab):
- laboratorios(laboratorio_id, nombre)
- ghs_pictogramas(ghs_id, descripcion, icon_url, detalle)
- h_codes(h_id, descripcion, grupo, nota)
- p_codes(p_id, descripcion, grupo, nota)

REGLAS:
- SOLO SELECT o WITH.
- PROHIBIDO: INSERT/UPDATE/DELETE/DDL.
- Si usas tablas por laboratorio: usa SIEMPRE laboratorio_id = @lab_id (NO uses numeros como 1).
- Si usas solo tablas globales: NO uses @lab_id.
- SIEMPRE LIMIT 100.
- NO pongas ';' al final.
- NO incluyas lab_id en parameters (el sistema lo inyecta).
- Si falta info: needsClarification=true y 1 pregunta corta.

MAPEO DIRECTO (no te confundas):
- "cuantos laboratorios hay" -> SELECT COUNT(*) FROM laboratorios
- "nombres de laboratorios" -> SELECT laboratorio_id, nombre FROM laboratorios ORDER BY nombre
- "pictogramas" -> SELECT ghs_id, descripcion, icon_url FROM ghs_pictogramas ORDER BY ghs_id
- "codigo H" -> SELECT h_id, descripcion FROM h_codes ORDER BY h_id
- "codigo P" -> SELECT p_id, descripcion FROM p_codes ORDER BY p_id
""";

        var system = """
Eres un traductor de español a SQL Postgres (Supabase).
Devuelve SOLO JSON que cumpla el schema. Sin markdown. Sin texto extra.
""";

        // OJO: NO ponemos labId aquí para no inducir "laboratorio_id = 1"
        var prompt = $"""
{schemaHint}

PREGUNTA:
{userQuestion}

Devuelve SQL exacto para Postgres.
""";

        var planSchema = BuildSqlPlanJsonSchema();

        var content = await _ollama.ChatAsync(
            model: model,
            messages: new[]
            {
                new OllamaChatClient.Msg("system", system),
                new OllamaChatClient.Msg("user", prompt)
            },
            format: planSchema,
            options: new Dictionary<string, object?> { ["temperature"] = 0 },
            ct: ct
        );

        if (string.IsNullOrWhiteSpace(content))
            return new SqlPlan { NeedsClarification = true, ClarifyingQuestion = "No entendí la pregunta. ¿Puedes reformularla?" };

        try
        {
            var plan = JsonSerializer.Deserialize<SqlPlan>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return plan ?? new SqlPlan
            {
                NeedsClarification = true,
                ClarifyingQuestion = "No pude generar una consulta válida. ¿Qué quieres listar/buscar?"
            };
        }
        catch
        {
            return new SqlPlan
            {
                NeedsClarification = true,
                ClarifyingQuestion = "La respuesta del modelo no fue JSON válido. ¿Puedes reformular la pregunta?"
            };
        }
    }

    private static object BuildSqlPlanJsonSchema()
    {
        return new
        {
            type = "object",
            required = new[] { "sql", "parameters", "needsClarification", "confidence" },
            properties = new
            {
                sql = new { type = "string" },
                parameters = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        required = new[] { "name", "value", "pgType" },
                        properties = new
                        {
                            name = new { type = "string" },
                            value = new { type = "string" },
                            pgType = new { type = "string" }
                        }
                    }
                },
                needsClarification = new { type = "boolean" },
                clarifyingQuestion = new { type = "string" },
                confidence = new { type = "number" }
            }
        };
    }
}

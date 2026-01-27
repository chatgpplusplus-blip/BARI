using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BARI_web.Services;

/// <summary>
/// Un paso del plan de acciones del bot.
/// type = "sql" significa que es una consulta que debemos ejecutar.
/// Puedes extender a otros tipos ("note", etc.) si quisieras en el futuro.
/// </summary>
public sealed class SqlActionStep
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "sql"; // por ahora nos interesan los "sql"

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sql")]
    public string? Sql { get; set; }
}

/// <summary>
/// Plan multi-paso para una pregunta del usuario.
/// El modelo decide cuántos pasos SQL usar y con qué propósito.
/// </summary>
public sealed class SqlActionPlan
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "db_query"; // db_query | web_search | general_help | needs_clarification

    [JsonPropertyName("clarifying_question")]
    public string? ClarifyingQuestion { get; set; }

    [JsonPropertyName("explain")]
    public string? Explain { get; set; }

    [JsonPropertyName("steps")]
    public List<SqlActionStep> Steps { get; set; } = new();
}

public sealed class DeepSeekSqlPlanner
{
    private readonly DeepSeekChatClient _llm;
    private readonly DeepSeekOptions _opt;
    private readonly SchemaCatalog _schema;

    public DeepSeekSqlPlanner(DeepSeekChatClient llm, IOptions<DeepSeekOptions> opt, SchemaCatalog schema)
    {
        _llm = llm;
        _opt = opt.Value;
        _schema = schema;
    }

    /// <summary>
    /// Genera un plan de acciones (posiblemente con varias consultas SQL) para responder la pregunta.
    /// </summary>
    public async Task<SqlActionPlan> PlanAsync(string userQuestion, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        var q = (userQuestion ?? "").Trim();
        if (q.Length < 2)
        {
            return new SqlActionPlan
            {
                Intent = "needs_clarification",
                ClarifyingQuestion = "¿Qué quieres consultar exactamente?"
            };
        }

        // Slice del esquema relevante para no mandar TODA la BD al modelo,
        // pero recuerda: el executor sigue teniendo acceso a toda la BD en lectura.
        var schemaSlice = await _schema.BuildSchemaSliceForPromptAsync(q, maxTables: 14, ct: ct);

        var system = $@"
Responde SOLO en JSON válido (json).
Eres un planificador para un sistema de inventario/laboratorio en PostgreSQL.

GLOSARIO / DESAMBIGUACIÓN (MUY IMPORTANTE):
- laboratorios: es el LUGAR FÍSICO (salón/ambiente). Tiene laboratorio_id (integer) y nombre.
- asignaturas: es la MATERIA (ej. ""Química General"").
- laboratorio_realizado: es la PRÁCTICA / LABORATORIO N de una asignatura (ej. ""Laboratorio 1 - ..."").
  Campos clave: laboratorio_realizado_id, asignatura_id, laboratorio_id (físico), nombre, descripcion.
- experiencias_clases: actividades/pasos; puede vincularse a laboratorio_realizado por experiencias_clases.laboratorio_realizado_id.

REGLA:
- Si el usuario dice ""Laboratorio <N> de la materia/asignatura <X>"" o menciona una asignatura,
  interpreta ""Laboratorio <N>"" como laboratorio_realizado (NO como tabla laboratorios).
  Entonces:
  1) Buscar asignatura por asignaturas.nombre ILIKE '%<X>%'
  2) Buscar laboratorio_realizado de esa asignatura con laboratorio_realizado.nombre ILIKE 'Laboratorio <N>%'
  3) (Opcional) listar experiencias_clases asociadas a ese laboratorio_realizado_id.
- Si el usuario habla de ubicación física (""dónde queda"", ""en qué sala"", ""en qué área"", ""laboratorio_id""),
  interpreta como tabla laboratorios (lugar físico).
- Si solo dice ""laboratorio 1"" sin contexto, usa needs_clarification y pregunta:
  ""¿Te refieres al laboratorio físico (lugar) o a la práctica de una asignatura?""

RECORDATORIOS DEL ESQUEMA:
- Materiales físicos están en la tabla materiales (tipo: 'VIDRIO','PLASTICO','MONTAJE','CONSUMIBLE').
- Equipos están en equipos y sus modelos en modelos_equipo.
- Sustancias abstractas en sustancias; contenedores físicos en contenedores (relación por sustancia_id).
- Documentos están en documentos y usan:
  - categoria_id, subcategoria_id (FK compuesta con categoria)
  - alcance (GENERAL/MARCA/LABORATORIO/CLASE/ASIGNATURA/EXPERIENCIA/EQUIPO/MATERIAL/SUSTANCIA/CONTENEDOR)
  - laboratorio_contexto_id (opcional)
- Instalaciones fijas están en instalaciones (subcategoria_id, laboratorio_id, area_id, fechas de revisión).
- Ubicación típica: area_id, meson_id, nivel, posicion, canvas_id.


Debes:
1) Pensar internamente qué pide el usuario y qué tablas/columnas son relevantes.
2) Diseñar una pequeña ESTRATEGIA de varios pasos si hace falta (ej. para ""peligroso"", ""riesgo"", ""idea de proyecto"", etc.).
   - Por ejemplo, si preguntan ""reactivo más peligroso"", puedes:
     * Buscar contenedores + sustancias.
     * Consultar H-codes (h_codes) y pictogramas GHS (ghs_pictogramas) asociados.
     * Definir un criterio de peligrosidad según esos datos.
3) Expresar esa estrategia como una lista de pasos ""steps"" donde cada paso de tipo ""sql""
   sea una consulta SELECT/WITH que el backend ejecutará.
4) NO muestres tu razonamiento, solo el JSON final.

REGLAS DE SEGURIDAD (OBLIGATORIAS):
- Genera SOLO SQL de lectura: SELECT o WITH ... SELECT
- NO uses ';'
- NO uses comentarios (-- o /* */)
- NO uses INSERT/UPDATE/DELETE/DDL ni comandos de sesión (SET, SHOW, etc.)
- Para listados usa LIMIT (por defecto {_opt.DefaultListLimit} y nunca más de {_opt.MaxListLimit}).
- Usa ILIKE para búsquedas por texto.
- Si necesitas varias consultas, crea varios steps de tipo ""sql"". Cada step debe ser ejecutable por separado.

Esquema relevante (resumen):
{schemaSlice}

FORMATO DE RESPUESTA (OBLIGATORIO, JSON):
{{
  ""intent"": ""db_query|needs_clarification|general_help"",
  ""clarifying_question"": ""<si falta dato crítico, pregunta corta o null>"",
  ""explain"": ""<breve explicación global de la estrategia que sigues>"",
  ""steps"": [
    {{
      ""type"": ""sql"",
      ""name"": ""principal"",
      ""description"": ""<qué hace esta consulta>"",
      ""sql"": ""SELECT ... LIMIT 50""
    }},
    {{
      ""type"": ""sql"",
      ""name"": ""peligrosidad"",
      ""description"": ""<consulta de H/P/GHS o detalles para evaluar riesgo>"",
      ""sql"": ""SELECT ... LIMIT 100""
    }}
    // Puedes omitir steps adicionales si no son necesarios.
  ]
}}

Instrucciones:
- Si la pregunta se responde con una única consulta simple (ej. ""¿cuántos X hay?""), usa un solo step SQL.
- Si la mejor respuesta requiere analizar conceptos como ""peligroso"", ""riesgo"", ""más crítico"", ""mejor opción"", etc.,
  está PERMITIDO y RECOMENDADO usar varios steps SQL (para obtener candidatos, detalles, peligrosidad, etc.).
- Si falta un dato crítico (ej. ""detalles"" sin identificar el elemento) usa intent=""needs_clarification"" y deja steps vacíos.
- Si es una duda teórica general (no requiere BD), usa intent=""general_help"" y deja steps vacíos.
- Si el usuario no especifica cantidad, usa LIMIT {_opt.DefaultListLimit}.
- Máximo {_opt.MaxSqlSteps} pasos SQL.

";

        var msgs = new List<ChatMessage>
        {
            new() { Role = "system", Content = system }
        };

        // Un poco de historial ayuda a mantener contexto de conversación,
        // pero no hace falta mandar todo.
        if (history is not null && history.Count > 0)
        {
            foreach (var h in history.TakeLast(Math.Min(_opt.HistoryWindow, history.Count)))
            {
                if (h.Role is "user" or "assistant")
                    msgs.Add(new ChatMessage { Role = h.Role, Content = h.Content });
            }
        }

        msgs.Add(new ChatMessage { Role = "user", Content = q });

        var plan = await _llm.CreateJsonAsync<SqlActionPlan>(_opt.ModelPlanner, msgs, maxTokens: 900, ct: ct);

        // Normalización básica
        plan.Intent = (plan.Intent ?? "db_query").Trim().ToLowerInvariant();
        if (plan.Intent is not ("db_query" or "general_help" or "needs_clarification" or "web_search"))
            plan.Intent = "db_query";


        if (plan.Steps == null)
            plan.Steps = new List<SqlActionStep>();

        foreach (var step in plan.Steps)
        {
            step.Type = (step.Type ?? "sql").Trim().ToLowerInvariant();
            step.Sql = step.Sql?.Trim();
        }

        return plan;
    }

}

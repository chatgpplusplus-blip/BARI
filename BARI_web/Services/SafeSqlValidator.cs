using System.Text.RegularExpressions;

namespace BARI_web.Services;

public sealed class SafeSqlValidator
{
    // Bloqueo fuerte de cosas peligrosas (DDL/DML y comandos)
    private static readonly string[] BannedTokens =
    {
        "insert","update","delete","drop","alter","create","grant","revoke","truncate",
        "copy","call","do","execute","prepare","deallocate","vacuum","analyze","refresh",
        "comment","security","owner","cluster","listen","notify",
        "set","show","reset", // evita cambios de sesión desde el modelo
    };

    public int MaxRows { get; set; } = 100;

    public string ValidateAndNormalize(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("SQL vacío.");

        var s = sql.Trim();

        // No permitimos multi-statement
        if (s.Contains(';'))
            throw new InvalidOperationException("SQL inválido: no se permiten ';'.");

        // No permitimos comentarios (evita esconder payload)
        if (s.Contains("--") || s.Contains("/*") || s.Contains("*/"))
            throw new InvalidOperationException("SQL inválido: no se permiten comentarios.");

        // Debe ser SELECT o WITH
        var start = s.TrimStart();
        if (!(start.StartsWith("select", StringComparison.OrdinalIgnoreCase) ||
              start.StartsWith("with", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Solo se permite SELECT/WITH.");

        // Banned tokens
        var lowered = Regex.Replace(s, @"\s+", " ").ToLowerInvariant();
        foreach (var tok in BannedTokens)
        {
            if (Regex.IsMatch(lowered, $@"\b{Regex.Escape(tok)}\b"))
                throw new InvalidOperationException($"SQL bloqueado por seguridad (token '{tok}').");
        }

        // Fuerza LIMIT si no hay (para evitar consultas gigantes)
        if (!Regex.IsMatch(lowered, @"\blimit\b"))
        {
            s += $"\nLIMIT {MaxRows}";
        }
        else
        {
            // Si el LIMIT es muy grande, lo recortamos
            // (super básico: si existe "limit N" donde N > MaxRows, lo reemplaza)
            s = Regex.Replace(
                s,
                @"(?i)\blimit\s+(\d+)\b",
                m =>
                {
                    if (int.TryParse(m.Groups[1].Value, out var n) && n > MaxRows)
                        return $"LIMIT {MaxRows}";
                    return m.Value;
                });
        }

        return s.Trim();
    }
}

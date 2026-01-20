using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using BARI_web.Models;
using System.Text.RegularExpressions;


namespace BARI_web.Services;

public sealed class PostgresReadOnlyExecutor
{
    private static readonly Regex HasLimit = new(@"\blimit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingSemicolon = new(@";\s*$", RegexOptions.Compiled);

    private static readonly Regex LabIdEqualsNumber = new(
        @"(?i)(\b[a-zA-Z_][a-zA-Z0-9_]*\.)?laboratorio_id\s*=\s*\d+",
        RegexOptions.Compiled);

    private static readonly Regex LabIdInNumber = new(
        @"(?i)(\b[a-zA-Z_][a-zA-Z0-9_]*\.)?laboratorio_id\s+in\s*\(\s*\d+\s*\)",
        RegexOptions.Compiled);


    private readonly string _connString;

    public PostgresReadOnlyExecutor(IConfiguration cfg)
    {
        _connString = cfg["Supabase:ConnectionString"]
                      ?? throw new InvalidOperationException("Falta Supabase:ConnectionString en appsettings.");
    }

    public static string NormalizeSqlForPolicy(string sql)
    {
        var s = (sql ?? "").Trim();

        // permite ';' final pero lo quitamos para no romper EnsureLimit y para política
        s = TrailingSemicolon.Replace(s, "");

        // fuerza lab_id por parámetro (sin importar el número que haya puesto el modelo)
        // t1.laboratorio_id = 1  -> t1.laboratorio_id = @lab_id
        s = LabIdEqualsNumber.Replace(s, m =>
        {
            var txt = m.Value;
            var dotIdx = txt.IndexOf("laboratorio_id", StringComparison.OrdinalIgnoreCase);
            var prefix = dotIdx > 0 ? txt[..dotIdx] : ""; // incluye "t1." si existe
            return $"{prefix}laboratorio_id = @lab_id";
        });

        // laboratorio_id IN (1) -> laboratorio_id IN (@lab_id)
        s = LabIdInNumber.Replace(s, m =>
        {
            var txt = m.Value;
            var dotIdx = txt.IndexOf("laboratorio_id", StringComparison.OrdinalIgnoreCase);
            var prefix = dotIdx > 0 ? txt[..dotIdx] : "";
            return $"{prefix}laboratorio_id IN (@lab_id)";
        });

        return s.Trim();
    }

    private static bool TouchesTable(string sqlLower, string table)
    {
        return sqlLower.Contains($" from {table} ")
            || sqlLower.Contains($" from {table}\n")
            || sqlLower.Contains($" join {table} ")
            || sqlLower.Contains($" join {table}\n")
            || sqlLower.Contains($" from {table}\r")
            || sqlLower.Contains($" join {table}\r")
            || sqlLower.Contains($" from {table};")
            || sqlLower.Contains($" join {table};")
            || sqlLower.EndsWith($" from {table}")
            || sqlLower.EndsWith($" join {table}");
    }

    public static bool IsAllowedSql(string sql)
    {
        // 👇 clave: valida el SQL ya normalizado
        var sRaw = NormalizeSqlForPolicy(sql);
        var s = sRaw.ToLowerInvariant();

        if (!(s.StartsWith("select") || s.StartsWith("with"))) return false;

        // multi-statement
        if (s.Contains(";")) return false;

        // No permitimos cosas peligrosas
        string[] banned = { "insert", "update", "delete", "drop", "alter", "create", "truncate", "grant", "revoke" };
        if (banned.Any(b => s.Contains(b))) return false;

        // tablas globales (sin laboratorio_id)
        string[] globalTables =
        {
        "h_codes", "p_codes", "ghs_pictogramas",
        "laboratorios", "plantas",
        "categorias", "subcategorias",
        "marcas", "unidades", "estados_activo", "condiciones"
    };

        // tablas por-laboratorio (tienen laboratorio_id o dependen de ello)
        string[] labTables =
        {
        "contenedores", "sustancias", "equipos", "calibraciones",
        "areas", "mesones",
        "materiales_vidrio", "materiales_montaje", "materiales_consumible",
        "documentos", "asignaturas", "experiencias_clases",
        "canvas_lab", "poligonos", "poligonos_puntos", "puertas", "ventanas", "bloques_int",
        "modelos_equipo",
        "sustancias_h", "sustancias_p", "sustancias_pictogramas",
        "experiencia_equipos", "experiencia_materiales_vidrio", "experiencia_materiales_montaje",
        "experiencia_materiales_consumible", "experiencia_sustancias",
        "laboratorio_realizado"
    };

        var touchesLab = labTables.Any(t => TouchesTable(s, t));
        var touchesGlobal = globalTables.Any(t => TouchesTable(s, t));

        // Si toca tablas por-laboratorio, exigir lab_id por parámetro
        if (touchesLab)
        {
            var hasLabParam = s.Contains("@lab_id") || s.Contains(":lab_id");
            var hasLabCol = s.Contains("laboratorio_id");

            if (!hasLabCol) return false;
            if (!hasLabParam) return false;

            return true;
        }

        // Si solo toca globales, permitir sin lab_id
        if (touchesGlobal) return true;

        // Si no detectamos tablas conocidas, por seguridad rechazamos
        return false;
    }


    public static string EnsureLimit(string sql, int limit = 100)
    {
        sql = NormalizeSqlForPolicy(sql);

        if (HasLimit.IsMatch(sql)) return sql;
        return sql.TrimEnd() + $" LIMIT {limit}";
    }


    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, List<SqlParam> parameters, int trustedLabId, CancellationToken ct)
    {
        if (!IsAllowedSql(sql))
            throw new InvalidOperationException("SQL rechazado por política (solo lectura / lab_id / sin multi-statement).");

        sql = EnsureLimit(sql, 100);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn)
        {
            CommandTimeout = 10
        };

        // Parámetro de laboratorio SIEMPRE desde el sistema (no del modelo)
        cmd.Parameters.AddWithValue("lab_id", NpgsqlDbType.Integer, trustedLabId);

        foreach (var p in parameters)
        {
            var name = (p.Name ?? "").Trim();

            // aceptar @foo o :foo
            if (name.StartsWith("@") || name.StartsWith(":"))
                name = name[1..];

            if (string.Equals(name, "lab_id", StringComparison.OrdinalIgnoreCase))
                continue; // lo inyecta el sistema

            var npgType = ToNpgsqlType(p.PgType);
            cmd.Parameters.Add(new NpgsqlParameter(name, npgType) { Value = ParseValue(p.Value, npgType) });
        }


        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }


    private static NpgsqlDbType ToNpgsqlType(string pgType) =>
        (pgType ?? "text").Trim().ToLowerInvariant() switch
        {
            "int4" or "int" or "integer" => NpgsqlDbType.Integer,
            "numeric" or "decimal" => NpgsqlDbType.Numeric,
            "date" => NpgsqlDbType.Date,
            "bool" or "boolean" => NpgsqlDbType.Boolean,
            _ => NpgsqlDbType.Text
        };

    private static object ParseValue(string raw, NpgsqlDbType t)
    {
        raw ??= "";

        return t switch
        {
            NpgsqlDbType.Integer => int.TryParse(raw, out var i) ? i : 0,
            NpgsqlDbType.Numeric => decimal.TryParse(raw, out var d) ? d : 0m,
            NpgsqlDbType.Date => DateOnly.TryParse(raw, out var dt) ? dt : DateOnly.FromDateTime(DateTime.UtcNow),
            NpgsqlDbType.Boolean => bool.TryParse(raw, out var b) && b,
            _ => raw
        };
    }


}


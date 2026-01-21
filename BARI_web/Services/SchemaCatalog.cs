using System.Collections.Concurrent;
using System.Text;
using Npgsql;

namespace BARI_web.Services;

public sealed class DbSchema
{
    public Dictionary<string, DbTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DbTable? TryGet(string tableFullNameOrName)
    {
        if (Tables.TryGetValue(tableFullNameOrName, out var t)) return t;

        // Intento por nombre sin schema
        var simple = tableFullNameOrName.Contains('.')
            ? tableFullNameOrName.Split('.', 2)[1]
            : tableFullNameOrName;

        return Tables.Values.FirstOrDefault(x => x.Name.Equals(simple, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DbTable
{
    public string Schema { get; init; } = "public";
    public string Name { get; init; } = "";
    public string FullName => $"{Schema}.{Name}";

    public List<DbColumn> Columns { get; } = new();
    public List<string> PrimaryKey { get; } = new();
    public List<DbForeignKey> ForeignKeys { get; } = new();
}

public sealed class DbColumn
{
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "";
    public bool IsNullable { get; init; }
}

public sealed class DbForeignKey
{
    public string ConstraintName { get; init; } = "";
    public string FromTable { get; init; } = ""; // schema.table
    public string FromColumn { get; init; } = "";
    public string ToTable { get; init; } = "";   // schema.table
    public string ToColumn { get; init; } = "";
}

public sealed class SchemaCatalog
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<SchemaCatalog> _log;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DbSchema? _cached;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(30);

    public SchemaCatalog(NpgsqlDataSource ds, ILogger<SchemaCatalog> log)
    {
        _ds = ds;
        _log = log;
    }

    public async Task<DbSchema> GetSchemaAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && (now - _lastRefresh) < CacheTtl)
            return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && (now - _lastRefresh) < CacheTtl)
                return _cached;

            _cached = await LoadSchemaAsync(ct);
            _lastRefresh = now;

            _log.LogInformation("SchemaCatalog: cargado {Tables} tablas.", _cached.Tables.Count);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<DbSchema> LoadSchemaAsync(CancellationToken ct)
    {
        var schema = new DbSchema();

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1) Tablas
        var tablesCmd = new NpgsqlCommand(@"
SELECT table_schema, table_name
FROM information_schema.tables
WHERE table_type='BASE TABLE'
  AND table_schema NOT IN ('pg_catalog','information_schema')
ORDER BY table_schema, table_name;", conn);

        await using (var r = await tablesCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sch = r.GetString(0);
                var name = r.GetString(1);
                var t = new DbTable { Schema = sch, Name = name };
                schema.Tables[t.FullName] = t;
                // También index por nombre simple si no colisiona
                if (!schema.Tables.ContainsKey(name))
                    schema.Tables[name] = t;
            }
        }

        // 2) Columnas
        var colsCmd = new NpgsqlCommand(@"
SELECT table_schema, table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema NOT IN ('pg_catalog','information_schema')
ORDER BY table_schema, table_name, ordinal_position;", conn);

        await using (var r = await colsCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sch = r.GetString(0);
                var tname = r.GetString(1);
                var col = r.GetString(2);
                var type = r.GetString(3);
                var nullable = r.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase);

                var key = $"{sch}.{tname}";
                if (schema.Tables.TryGetValue(key, out var t))
                {
                    t.Columns.Add(new DbColumn
                    {
                        Name = col,
                        DataType = type,
                        IsNullable = nullable
                    });
                }
            }
        }

        // 3) Primary Keys
        var pkCmd = new NpgsqlCommand(@"
SELECT tc.table_schema, tc.table_name, kcu.column_name
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu
  ON tc.constraint_name = kcu.constraint_name
 AND tc.table_schema = kcu.table_schema
WHERE tc.constraint_type = 'PRIMARY KEY'
  AND tc.table_schema NOT IN ('pg_catalog','information_schema')
ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position;", conn);

        await using (var r = await pkCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sch = r.GetString(0);
                var tname = r.GetString(1);
                var col = r.GetString(2);

                var key = $"{sch}.{tname}";
                if (schema.Tables.TryGetValue(key, out var t))
                    t.PrimaryKey.Add(col);
            }
        }

        // 4) Foreign Keys
        var fkCmd = new NpgsqlCommand(@"
SELECT
  tc.constraint_name,
  tc.table_schema,
  tc.table_name,
  kcu.column_name,
  ccu.table_schema AS foreign_table_schema,
  ccu.table_name   AS foreign_table_name,
  ccu.column_name  AS foreign_column_name
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu
  ON tc.constraint_name = kcu.constraint_name
 AND tc.table_schema = kcu.table_schema
JOIN information_schema.constraint_column_usage ccu
  ON ccu.constraint_name = tc.constraint_name
 AND ccu.constraint_schema = tc.table_schema
WHERE tc.constraint_type = 'FOREIGN KEY'
  AND tc.table_schema NOT IN ('pg_catalog','information_schema')
ORDER BY tc.table_schema, tc.table_name;", conn);

        await using (var r = await fkCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var cname = r.GetString(0);
                var fromSch = r.GetString(1);
                var fromTbl = r.GetString(2);
                var fromCol = r.GetString(3);
                var toSch = r.GetString(4);
                var toTbl = r.GetString(5);
                var toCol = r.GetString(6);

                var fromKey = $"{fromSch}.{fromTbl}";
                if (schema.Tables.TryGetValue(fromKey, out var t))
                {
                    t.ForeignKeys.Add(new DbForeignKey
                    {
                        ConstraintName = cname,
                        FromTable = fromKey,
                        FromColumn = fromCol,
                        ToTable = $"{toSch}.{toTbl}",
                        ToColumn = toCol
                    });
                }
            }
        }

        return schema;
    }

    /// <summary>
    /// Construye un "slice" de esquema relevante para una pregunta (para no mandar TODAS las tablas al LLM).
    /// El bot igual tiene acceso a toda la BD porque el executor es genérico; el slice es solo para planear.
    /// </summary>
    public async Task<string> BuildSchemaSliceForPromptAsync(string userQuestion, int maxTables = 14, CancellationToken ct = default)
    {
        var db = await GetSchemaAsync(ct);

        var tokens = Tokenize(userQuestion);
        var scored = new List<(DbTable t, int score)>();

        // Score por match con nombre tabla/columnas
        var uniqueTables = db.Tables.Values.DistinctBy(t => t.FullName).ToList();
        foreach (var t in uniqueTables)
        {
            int score = 0;
            foreach (var tok in tokens)
            {
                if (t.Name.Contains(tok, StringComparison.OrdinalIgnoreCase)) score += 8;
                if (t.FullName.Contains(tok, StringComparison.OrdinalIgnoreCase)) score += 10;

                // columnas
                foreach (var c in t.Columns)
                {
                    if (c.Name.Contains(tok, StringComparison.OrdinalIgnoreCase)) score += 3;
                }
            }

            // Boost si la pregunta menciona "cuántos", "lista", etc. (suele ser conteo/lista)
            if (score > 0 && (userQuestion.Contains("cuánt", StringComparison.OrdinalIgnoreCase) ||
                              userQuestion.Contains("lista", StringComparison.OrdinalIgnoreCase) ||
                              userQuestion.Contains("mostrar", StringComparison.OrdinalIgnoreCase)))
                score += 2;

            if (score > 0) scored.Add((t, score));
        }

        // Si nada matchea, manda un set mínimo (tablas más "centrales" por FKs)
        var selected = scored
            .OrderByDescending(x => x.score)
            .Select(x => x.t)
            .Take(maxTables)
            .ToList();

        if (selected.Count == 0)
        {
            // fallback: tablas con más FKs (conectividad)
            selected = uniqueTables
                .OrderByDescending(t => t.ForeignKeys.Count)
                .Take(Math.Min(maxTables, uniqueTables.Count))
                .ToList();
        }

        // Incluye tablas relacionadas por FK (para joins)
        var set = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in selected)
            set[t.FullName] = t;

        foreach (var t in selected)
        {
            foreach (var fk in t.ForeignKeys)
            {
                if (db.Tables.TryGetValue(fk.ToTable, out var toT))
                    set[toT.FullName] = toT;
            }
        }

        var final = set.Values.Take(maxTables).ToList();

        // Construye texto compacto
        var sb = new StringBuilder();
        sb.AppendLine("Esquema relevante (PostgreSQL):");
        foreach (var t in final.OrderBy(t => t.Schema).ThenBy(t => t.Name))
        {
            sb.Append("- ").Append(t.FullName).Append(" (");
            sb.Append(string.Join(", ", t.Columns.Take(40).Select(c => $"{c.Name}:{c.DataType}")));
            if (t.Columns.Count > 40) sb.Append(", ...");
            sb.Append(')');

            if (t.PrimaryKey.Count > 0)
                sb.Append(" PK[").Append(string.Join(",", t.PrimaryKey)).Append(']');

            if (t.ForeignKeys.Count > 0)
            {
                sb.Append(" FK[");
                sb.Append(string.Join("; ", t.ForeignKeys.Take(6).Select(fk =>
                    $"{fk.FromColumn}->{fk.ToTable}.{fk.ToColumn}")));
                if (t.ForeignKeys.Count > 6) sb.Append("; ...");
                sb.Append(']');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> Tokenize(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                clean.Append(ch);
            else
                clean.Append(' ');
        }

        return clean
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Select(x => x.ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}

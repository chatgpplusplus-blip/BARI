using Dapper;
using Npgsql;
using System.Data;
using System.Globalization;

namespace BARI_web.General_Services.DataBaseConnection;

public class PgCrud
{
    private readonly NpgsqlDataSource _ds;

    private string _table = "areas";
    private List<PgColumn> _cols = new(); // esquema cacheado
    private string _idCol = "area_id";

    public PgCrud(NpgsqlDataSource ds) => _ds = ds;

    // ============================
    // Selección de “hoja” (tabla)
    // ============================
    public void UseSheet(string sheetName)
    {
        var name = (sheetName ?? "").Trim();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Areas"] = "areas",
            ["Mesones"] = "mesones",
            ["Marcas"] = "marcas",
            ["Condiciones"] = "condiciones",
            ["Unidades"] = "unidades",
            ["Categorias"] = "categorias",
            ["Subcategorias"] = "subcategorias",
            ["Reactivos"] = "sustancias",
            ["Sustancias"] = "sustancias",
            ["Contenedores"] = "contenedores",
            ["Pictogramas"] = "ghs_pictogramas",
            ["H_Codes"] = "h_codes",
            ["P_Codes"] = "p_codes",
            ["Usos"] = "usos",
            ["Asignaturas"] = "asignaturas",
            // NUEVO: para el mapa
            ["canvas_lab"] = "canvas_lab",
            ["poligonos"] = "poligonos",
        };

        _table = map.TryGetValue(name, out var t) ? t : name.ToLowerInvariant();
        _cols.Clear(); // reset cache
    }

    // ============================
    // Lectura
    // ============================
    public async Task<IReadOnlyList<string>> GetHeadersAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        return _cols.Select(c => c.Name).ToList();
    }

    public async Task<IList<Dictionary<string, string>>> ReadAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        if (_cols.Count == 0) return new List<Dictionary<string, string>>();

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var colList = string.Join(",", _cols.Select(c => $"\"{c.Name}\""));
        var sql = $"select {colList} from public.\"{_table}\"";

        var rows = new List<Dictionary<string, string>>();

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);
        while (await rdr.ReadAsync(ct))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var col = _cols[i];
                if (rdr.IsDBNull(i)) { dict[col.Name] = ""; continue; }

                object val = rdr.GetValue(i);

                // Formateo seguro e invariante
                if (val is decimal dec)
                    dict[col.Name] = dec.ToString(CultureInfo.InvariantCulture);
                else if (val is double dbl)
                    dict[col.Name] = dbl.ToString(CultureInfo.InvariantCulture);
                else if (val is float fl)
                    dict[col.Name] = Convert.ToDecimal(fl).ToString(CultureInfo.InvariantCulture);
                else if (val is DateTime dt && col.DataType.Contains("date", StringComparison.OrdinalIgnoreCase))
                    dict[col.Name] = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                else
                    dict[col.Name] = Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
            }
            rows.Add(dict);
        }
        return rows;
    }

    // ============================
    // Creación
    // ============================
    public async Task CreateAsync(Dictionary<string, object> data, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);

        var pairs = data
            .Where(kv => _cols.Any(c => c.Name.Equals(Norm(kv.Key), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (pairs.Count == 0) return;

        var cols = pairs.Select(kv => $"\"{Norm(kv.Key)}\"").ToList();
        var pars = pairs.Select((_, i) => $"@p{i}").ToList();
        var sql = $"insert into public.\"{_table}\" ({string.Join(",", cols)}) values ({string.Join(",", pars)})";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };

        for (int i = 0; i < pairs.Count; i++)
        {
            var colName = Norm(pairs[i].Key);
            var col = _cols.First(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
            cmd.Parameters.AddWithValue($"p{i}", NormalizeForDb(pairs[i].Value, col));
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ============================
    // Update por PK
    // ============================
    public async Task<bool> UpdateByIdAsync(string idColName, string idValue, Dictionary<string, object> updates, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        if (_cols.Count == 0) return false;
        _idCol = !string.IsNullOrWhiteSpace(idColName) ? Norm(idColName) : _idCol;

        var setPairs = updates
            .Where(kv => _cols.Any(c => c.Name.Equals(Norm(kv.Key), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (setPairs.Count == 0) return false;

        var sets = setPairs.Select((kv, i) => $"\"{Norm(kv.Key)}\"=@p{i}");
        var sql = $"update public.\"{_table}\" set {string.Join(",", sets)} where \"{_idCol}\"=@id";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };

        for (int i = 0; i < setPairs.Count; i++)
        {
            var colName = Norm(setPairs[i].Key);
            var col = _cols.First(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
            cmd.Parameters.AddWithValue($"p{i}", NormalizeForDb(setPairs[i].Value, col));
        }

        var idCol = _cols.FirstOrDefault(c => c.Name.Equals(_idCol, StringComparison.OrdinalIgnoreCase))
                    ?? _cols.First();
        cmd.Parameters.AddWithValue("id", NormalizeForDb(idValue, idCol));

        var n = await cmd.ExecuteNonQueryAsync(ct);
        return n > 0;
    }

    // ============================
    // Delete por PK
    // ============================
    public async Task<bool> DeleteByIdAsync(string idColName, string idValue, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        if (_cols.Count == 0) return false;
        _idCol = !string.IsNullOrWhiteSpace(idColName) ? Norm(idColName) : _idCol;

        var sql = $"delete from public.\"{_table}\" where \"{_idCol}\" = @id";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { id = idValue }, cancellationToken: ct, commandTimeout: 60));
        return n > 0;
    }

    // ============================
    // Lookups
    // ============================
    public async Task<Dictionary<string, string>> GetLookupAsync(string sheetName, string keyCol, string valueCol, CancellationToken ct = default)
    {
        var table = sheetName.ToLowerInvariant();
        var sql = $"select \"{Norm(keyCol)}\" as key, \"{Norm(valueCol)}\" as val from public.\"{table}\"";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: 60));
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var k = (string?)r.key;
            var v = (string?)r.val ?? "";
            if (!string.IsNullOrWhiteSpace(k)) dict[k] = v;
        }
        return dict;
    }

    // ============================
    // Helpers de esquema / formato
    // ============================
    private sealed record PgColumn(string Name, string DataType, bool IsNullable, int? NumericScale);

    private async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_cols.Count > 0) return;

        const string sql = @"
select
  c.column_name,
  c.data_type,
  (c.is_nullable='YES') as is_nullable,
  c.numeric_scale
from information_schema.columns c
where c.table_schema='public' and c.table_name=@t
order by c.ordinal_position";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, new { t = _table }, cancellationToken: ct, commandTimeout: 30));

        _cols = rows
            .Select(r => new PgColumn(
                (string)r.column_name,
                (string)r.data_type,
                (bool)r.is_nullable,
                (int?)r.numeric_scale
            ))
            .ToList();

        if (_cols.Count == 0)
        {
            _idCol = "";
            return;
        }

        _idCol = _cols.FirstOrDefault(c => c.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))?.Name
                 ?? _cols.First().Name;
    }

    private static string Norm(string s) => (s ?? "").Trim().Replace('\u00A0', ' ');

    // ¡Clave! Normalizar hacia DB sin pasar por ToString()
    // ¡Clave! Normalizar hacia DB sin pasar por ToString() cuando es NULL/DBNull
    private static object NormalizeForDb(object? val, PgColumn col)
    {
        if (val is null) return DBNull.Value;
        if (val is DBNull) return DBNull.Value;  // <-- evita que DBNull termine como ""

        // NULL explícito desde strings (para textos / FKs opcionales)
        if (val is string s && string.IsNullOrWhiteSpace(s))
            return DBNull.Value;

        var type = col.DataType.ToLowerInvariant();

        // Boolean
        if (type is "boolean")
        {
            if (val is bool b) return b;
            var ss = Convert.ToString(val, CultureInfo.InvariantCulture)?.Trim();
            if (bool.TryParse(ss, out var bb)) return bb;
            if (ss == "1") return true;
            if (ss == "0") return false;
            return DBNull.Value;
        }

        // Enteros
        if (type is "integer" or "smallint" or "bigint")
        {
            if (val is int i) return i;
            if (val is long l) return l;
            var ss = Convert.ToString(val, CultureInfo.InvariantCulture);
            if (long.TryParse(ss, NumberStyles.Any, CultureInfo.InvariantCulture, out var ll)) return ll;
            return DBNull.Value;
        }

        // NUMERIC / REAL / DOUBLE
        if (type.Contains("numeric") || type.Contains("real") || type.Contains("double"))
        {
            decimal d;

            if (val is decimal dec) d = dec;
            else if (val is double dbl) d = (decimal)dbl;
            else if (val is float fl) d = (decimal)fl;
            else
            {
                // string → permite coma o punto decimal
                var ss = (Convert.ToString(val, CultureInfo.InvariantCulture) ?? "").Trim();
                if (string.IsNullOrEmpty(ss)) return DBNull.Value;

                // normalización simple: última coma/punto como decimal
                if (ss.Count(ch => ch == ',' || ch == '.') > 1)
                    ss = ss.Replace(".", "").Replace(",", ".");
                else
                    ss = ss.Replace(",", ".");

                if (!decimal.TryParse(ss, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    return DBNull.Value;
            }

            // Redondeo a la escala declarada (por defecto 3)
            var scale = col.NumericScale ?? 3;
            d = Math.Round(d, scale, MidpointRounding.AwayFromZero);
            return d;
        }

        // DATE
        if (type.Contains("date"))
        {
            if (val is DateTime dt) return dt.Date;
            var ss = Convert.ToString(val, CultureInfo.InvariantCulture);
            if (DateTime.TryParse(ss, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt2))
                return dt2.Date;
            return DBNull.Value;
        }

        // Texto / varchar
        // OJO: a estas alturas 'val' no es DBNull (lo filtramos arriba),
        // así que devolver String es seguro; las cadenas vacías ya se convirtieron a NULL antes.
        return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
    }

}

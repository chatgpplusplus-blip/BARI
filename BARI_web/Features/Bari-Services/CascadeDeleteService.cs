using System.Globalization;
using Npgsql;

namespace BARI_web.Features.Services;

public sealed record CascadeDependency(
    string TableName,
    string ColumnName,
    long Count,
    bool HasMore,
    IReadOnlyList<string> Items)
{
    public string CountLabel => HasMore ? $"{Count}+" : Count.ToString(CultureInfo.InvariantCulture);
}

public sealed class CascadeDeleteService
{
    private const int PreviewLimit = 12;

    private static readonly string[] PreferredNameColumns =
    {
        "nombre",
        "name",
        "titulo",
        "title",
        "descripcion",
        "descripcion_detalle",
        "detalle",
        "codigo",
        "codigo_clase",
        "sigla"
    };

    private readonly NpgsqlDataSource _ds;
    private readonly SchemaCatalog _schemaCatalog;
    private readonly ILogger<CascadeDeleteService> _log;

    public CascadeDeleteService(NpgsqlDataSource ds, SchemaCatalog schemaCatalog, ILogger<CascadeDeleteService> log)
    {
        _ds = ds;
        _schemaCatalog = schemaCatalog;
        _log = log;
    }

    public async Task<IReadOnlyList<CascadeDependency>> GetDependenciesAsync(
        string tableName,
        string id,
        int? laboratorioId,
        string labColumn = "laboratorio_id",
        CancellationToken ct = default)
    {
        var db = await _schemaCatalog.GetSchemaAsync(ct);
        var target = db.TryGet(tableName);
        if (target is null)
            return Array.Empty<CascadeDependency>();

        var dependencies = new List<CascadeDependency>();
        var referencing = GetReferencingForeignKeys(db, target);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        foreach (var fk in referencing)
        {
            if (!db.Tables.TryGetValue(fk.FromTable, out var fromTable))
                continue;

            var preview = await GetReferencePreviewAsync(conn, fromTable, fk, id, laboratorioId, labColumn, ct);
            if (preview.Count == 0)
                continue;

            dependencies.Add(new CascadeDependency(
                fromTable.Name,
                fk.FromColumn,
                preview.Count,
                preview.HasMore,
                preview.Items));
        }

        return dependencies;
    }

    public async Task<int> DeleteWithCascadeAsync(
        string tableName,
        string id,
        int? laboratorioId,
        string labColumn = "laboratorio_id",
        bool allowNullLabScope = false,
        CancellationToken ct = default)
    {
        var db = await _schemaCatalog.GetSchemaAsync(ct);
        var target = db.TryGet(tableName);
        if (target is null)
            return 0;

        var referencing = GetReferencingForeignKeys(db, target);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var fk in referencing)
            {
                if (!db.Tables.TryGetValue(fk.FromTable, out var fromTable))
                    continue;

                var deleteSql = BuildDeleteSql(fromTable, fk.FromColumn, laboratorioId, labColumn);
                await using var cmd = new NpgsqlCommand(deleteSql, conn, tx);
                cmd.Parameters.AddWithValue("id", id);

                if (ShouldApplyLabFilter(fromTable, laboratorioId, labColumn))
                    cmd.Parameters.AddWithValue("lab", laboratorioId!.Value);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            var targetDelete = BuildTargetDeleteSql(target, labColumn, allowNullLabScope);
            await using var targetCmd = new NpgsqlCommand(targetDelete, conn, tx);
            targetCmd.Parameters.AddWithValue("id", id);

            if (ShouldApplyLabFilter(target, laboratorioId, labColumn))
                targetCmd.Parameters.AddWithValue("lab", laboratorioId!.Value);

            var rows = await targetCmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return rows;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en borrado en cascada para {Table} ({Id}).", tableName, id);
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static List<DbForeignKey> GetReferencingForeignKeys(DbSchema schema, DbTable target)
    {
        return schema.Tables.Values
            .DistinctBy(t => t.FullName)
            .SelectMany(t => t.ForeignKeys)
            .Where(fk => fk.ToTable.Equals(target.FullName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string BuildDeleteSql(DbTable fromTable, string fkColumn, int? laboratorioId, string labColumn)
    {
        var where = $"{QuoteIdent(fkColumn)} = @id";
        if (ShouldApplyLabFilter(fromTable, laboratorioId, labColumn))
            where += $" AND {QuoteIdent(labColumn)} = @lab";

        return $"DELETE FROM {QuoteIdent(fromTable.Schema)}.{QuoteIdent(fromTable.Name)} WHERE {where};";
    }

    private static string BuildTargetDeleteSql(DbTable target, string labColumn, bool allowNullLabScope)
    {
        var pk = target.PrimaryKey.FirstOrDefault() ?? "id";
        var where = $"{QuoteIdent(pk)} = @id";

        // Si se permite scope nulo, entonces el filtro por laboratorio aplica aunque la columna no exista
        // (pero lo normal es que exista). Si no existe, no agregamos filtro para evitar SQL inválido.
        var hasLabColumn = target.Columns.Any(c => c.Name.Equals(labColumn, StringComparison.OrdinalIgnoreCase));

        if (allowNullLabScope && hasLabColumn)
        {
            where += $" AND ({QuoteIdent(labColumn)} = @lab OR {QuoteIdent(labColumn)} IS NULL)";
        }
        else if (hasLabColumn)
        {
            where += $" AND {QuoteIdent(labColumn)} = @lab";
        }

        return $"DELETE FROM {QuoteIdent(target.Schema)}.{QuoteIdent(target.Name)} WHERE {where};";
    }

    private static bool ShouldApplyLabFilter(DbTable table, int? laboratorioId, string labColumn)
    {
        if (laboratorioId is null)
            return false;

        return table.Columns.Any(c => c.Name.Equals(labColumn, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(IReadOnlyList<string> Items, long Count, bool HasMore)> GetReferencePreviewAsync(
        NpgsqlConnection conn,
        DbTable fromTable,
        DbForeignKey fk,
        string id,
        int? laboratorioId,
        string labColumn,
        CancellationToken ct)
    {
        var previewColumns = GetPreviewColumns(fromTable, fk);
        var selectCols = string.Join(", ", previewColumns.Select(QuoteIdent));

        var where = $"{QuoteIdent(fk.FromColumn)} = @id";
        if (ShouldApplyLabFilter(fromTable, laboratorioId, labColumn))
            where += $" AND {QuoteIdent(labColumn)} = @lab";

        // 1) Conteo real
        var countSql = $"SELECT COUNT(*) FROM {QuoteIdent(fromTable.Schema)}.{QuoteIdent(fromTable.Name)} WHERE {where};";
        await using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.Parameters.AddWithValue("id", id);
        if (ShouldApplyLabFilter(fromTable, laboratorioId, labColumn))
            countCmd.Parameters.AddWithValue("lab", laboratorioId!.Value);

        var countObj = await countCmd.ExecuteScalarAsync(ct);
        var totalCount = (countObj is null || countObj == DBNull.Value) ? 0L : Convert.ToInt64(countObj, CultureInfo.InvariantCulture);

        if (totalCount == 0)
            return (Array.Empty<string>(), 0, false);

        // 2) Preview (limit+1 para saber si hay más)
        var sql =
            $"SELECT {selectCols} " +
            $"FROM {QuoteIdent(fromTable.Schema)}.{QuoteIdent(fromTable.Name)} " +
            $"WHERE {where} " +
            $"LIMIT @limit;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("limit", PreviewLimit + 1);
        if (ShouldApplyLabFilter(fromTable, laboratorioId, labColumn))
            cmd.Parameters.AddWithValue("lab", laboratorioId!.Value);

        var items = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var parts = new List<string>();

            for (var i = 0; i < previewColumns.Count; i++)
            {
                var col = previewColumns[i];
                var val = reader.IsDBNull(i)
                    ? "—"
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "—";

                if (previewColumns.Count == 1)
                    parts.Add(val);
                else
                    parts.Add($"{col}={val}");
            }

            items.Add(string.Join(", ", parts));
        }

        // Ojo: el error CS0019 ('>' con grupo de métodos) te salía por duplicados/ambigüedad,
        // aquí ya no hay duplicados ni llamadas ambiguas.
        var hasMore = items.Count > PreviewLimit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        if (hasMore)
            items.Add("…y más");

        return (items, totalCount, hasMore);
    }

    private static IReadOnlyList<string> GetPreviewColumns(DbTable table, DbForeignKey fk)
    {
        var textColumns = table.Columns
            .Where(c => IsTextColumn(c.DataType))
            .Select(c => c.Name)
            .Where(c => !IsIdColumn(c))
            .Where(c => !c.Equals(fk.FromColumn, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Preferencias exactas (1 columna)
        foreach (var preferred in PreferredNameColumns)
        {
            var exact = textColumns.FirstOrDefault(c => c.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
                return new List<string> { exact };
        }

        // Coincidencias parciales (hasta 2)
        var containsPreferred = textColumns
            .Where(c => PreferredNameColumns.Any(p => c.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToList();

        if (containsPreferred.Count > 0)
            return containsPreferred;

        // Cualquier texto (hasta 2)
        if (textColumns.Count > 0)
            return textColumns.Take(2).ToList();

        // PK si existe
        if (table.PrimaryKey.Count > 0)
            return table.PrimaryKey;

        // Último recurso: FK
        return new List<string> { fk.FromColumn };
    }

    private static bool IsTextColumn(string dataType)
    {
        return dataType.Contains("char", StringComparison.OrdinalIgnoreCase)
               || dataType.Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdColumn(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIdent(string ident)
    {
        // NpgsqlCommandBuilder.QuoteIdentifier es de instancia, por eso instanciamos.
        return new NpgsqlCommandBuilder().QuoteIdentifier(ident);
    }
}

using System.Data;
using Npgsql;

namespace BARI_web.Services;

public sealed class PostgresReadOnlyExecutor
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PostgresReadOnlyExecutor> _log;
    private readonly SafeSqlValidator _validator;

    public PostgresReadOnlyExecutor(NpgsqlDataSource ds, ILogger<PostgresReadOnlyExecutor> log, SafeSqlValidator validator)
    {
        _ds = ds;
        _log = log;
        _validator = validator;
    }

    public async Task<DbQueryResult> ExecuteSqlAsync(string sqlFromModel, CancellationToken ct = default)
    {
        var safeSql = _validator.ValidateAndNormalize(sqlFromModel);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // IMPORTANTE: SET LOCAL requiere transacción
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // Timeouts defensivos (solo afectan a esta transacción)
            await using (var setup = new NpgsqlCommand(@"
SET LOCAL statement_timeout = 6000;
SET LOCAL idle_in_transaction_session_timeout = 6000;
SET LOCAL lock_timeout = 2000;
", conn, tx))
            {
                await setup.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand(safeSql, conn, tx);

            _log.LogInformation("SQL (safe): {Sql}", safeSql.Replace("\n", " "));

            // Si parece COUNT sin group by, devolvemos escalar (más barato)
            if (LooksLikeScalarCount(safeSql))
            {
                var scalar = await cmd.ExecuteScalarAsync(ct);
                await tx.CommitAsync(ct);

                return new DbQueryResult
                {
                    ScalarCount = Convert.ToInt64(scalar ?? 0)
                };
            }

            // 👇 CLAVE: el reader se crea y SE CIERRA/SE DISPONE
            // ANTES de hacer COMMIT
            var result = new DbQueryResult();

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                for (int i = 0; i < reader.FieldCount; i++)
                    result.Columns.Add(reader.GetName(i));

                int rows = 0;
                while (await reader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[result.Columns[i]] = await reader.IsDBNullAsync(i, ct)
                            ? null
                            : reader.GetValue(i);
                    }

                    result.Rows.Add(row);
                    rows++;

                    // Hard cap por si el LIMIT no se aplicó
                    if (rows >= _validator.MaxRows)
                        break;
                }

                // (Opcional) Cierra explícito; DisposeAsync del using ya lo hace,
                // pero esto ayuda a evitar edge-cases.
                await reader.CloseAsync();
            }

            // Ahora sí: ya no hay comando en progreso → commit seguro
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    private static bool LooksLikeScalarCount(string sql)
    {
        var s = sql.Trim().ToLowerInvariant();
        if (!(s.StartsWith("select") || s.StartsWith("with"))) return false;

        // heurística simple
        return s.Contains("count(") && !s.Contains("group by") && !s.Contains("select *");
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BARI_web.Features.Seguridad_Quimica.Models;

public sealed class SeedCatalogs
{
    private readonly ILogger<SeedCatalogs> _log;
    private readonly NpgsqlDataSource _ds;
    private readonly HttpClient _http = new();

    private readonly string _hpEsLocal;
    private readonly string _hpEnLocal;

    private const string HP_ES_REMOTE = "https://mhchem.github.io/hpstatements/clp/hpstatements-es-latest.json";
    private const string HP_EN_REMOTE = "https://mhchem.github.io/hpstatements/clp/hpstatements-en-latest.json";

    public SeedCatalogs(ILogger<SeedCatalogs> log, NpgsqlDataSource ds, IWebHostEnvironment env)
    {
        _log = log;
        _ds = ds;

        _hpEsLocal = Path.Combine(env.WebRootPath ?? "wwwroot", "data", "hp_es.json");
        _hpEnLocal = Path.Combine(env.WebRootPath ?? "wwwroot", "data", "hp_en.json");
    }

    public async Task RunAsync(bool forceAll = false, CancellationToken ct = default)
    {
        await EnsureMetaAsync(ct);

        var key = "seed_hp_es";
        if (forceAll || !await IsDoneAsync(key, ct))
        {
            var ok = await SeedHPInternalAsync(ct);
            if (ok) await MarkDoneAsync(key, ct);
            else _log.LogWarning("Seed HP no se marcó como done (0 filas).");
        }
        else _log.LogInformation("H/P: ya sembrado (bari_meta=done).");
    }

    public async Task<(int h, int p)> SeedHP_ForceAsync(CancellationToken ct = default)
    {
        await EnsureMetaAsync(ct);
        await SeedHPInternalAsync(ct);
        return await CountHPAsync(ct);
    }

    // ================= core =================
    private async Task<bool> SeedHPInternalAsync(CancellationToken ct)
    {
        JsonDocument? doc =
            await TryLoadLocal(_hpEsLocal, ct) ??
            await TryDownload(HP_ES_REMOTE, ct) ??
            await TryLoadLocal(_hpEnLocal, ct) ??
            await TryDownload(HP_EN_REMOTE, ct);

        if (doc is null)
        {
            _log.LogError("No fue posible obtener el dataset H/P (ni local ni remoto).");
            return false;
        }

        var root = doc.RootElement;

        // Idioma preferido
        var lang = GetPreferredLang(root);
        _log.LogInformation("Idioma elegido en dataset: {Lang}", lang);

        if (!root.TryGetProperty("statements", out var statements)
            || !root.TryGetProperty("codes", out var codes)
            || codes.ValueKind != JsonValueKind.Array)
        {
            _log.LogError("Estructura inesperada del JSON (faltan 'codes' o 'statements').");
            return false;
        }

        var insertedH = 0;
        var insertedP = 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var cEl in codes.EnumerateArray())
        {
            var code = cEl.GetString();
            if (string.IsNullOrWhiteSpace(code)) continue;

            var text = GetStatement(statements, lang, code!);
            if (string.IsNullOrWhiteSpace(text)) continue; // no hay texto, saltar

            if (code!.StartsWith("H", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("EUH", StringComparison.OrdinalIgnoreCase))
            {
                insertedH += await UpsertH(conn, code, text!, GrupoH(code), ct);
            }
            else if (code.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                insertedP += await UpsertP(conn, code, text!, GrupoP(code), ct);
            }
        }

        await tx.CommitAsync(ct);
        _log.LogInformation("H/P sembrados/actualizados: H={H}, P={P}", insertedH, insertedP);

        return insertedH + insertedP > 0;
    }

    private static string GetPreferredLang(JsonElement root)
    {
        if (root.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
        {
            // preferimos 'es' si existe; si no, tomamos el primero
            string? first = null;
            foreach (var l in langs.EnumerateArray())
            {
                var s = l.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                first ??= s;
                if (string.Equals(s, "es", StringComparison.OrdinalIgnoreCase)) return "es";
            }
            return first ?? "en";
        }
        return "en";
    }

    private static string? GetStatement(JsonElement statements, string lang, string code)
    {
        // Las claves vienen como "latest/en/H225" o "latest/es/P210"
        var key = $"latest/{lang}/{code}";
        return statements.TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    // ============== IO helpers ==============
    private async Task<JsonDocument?> TryLoadLocal(string path, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
            {
                await using var fs = File.OpenRead(path);
                _log.LogInformation("Cargando dataset local: {Path}", path);
                return await JsonDocument.ParseAsync(fs, cancellationToken: ct);
            }
            _log.LogWarning("No existe dataset local: {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fallo leyendo dataset local {Path}", path);
        }
        return null;
    }

    private async Task<JsonDocument?> TryDownload(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            _log.LogInformation("Descargado dataset: {Url}", url);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fallo descargando {Url}", url);
            return null;
        }
    }

    // ============== DB helpers ==============
    private async Task<(int h, int p)> CountHPAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        static async Task<int> C(NpgsqlConnection c, string t)
        {
            await using var cmd = new NpgsqlCommand($"select count(*) from {t}", c);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        return (await C(conn, "h_codes"), await C(conn, "p_codes"));
    }

    private static async Task<int> UpsertH(NpgsqlConnection conn, string hId, string descripcion, string grupo, CancellationToken ct)
    {
        const string sql = @"INSERT INTO h_codes (h_id, descripcion, grupo)
                             VALUES (@id,@d,@g)
                             ON CONFLICT (h_id) DO UPDATE SET
                                descripcion = EXCLUDED.descripcion,
                                grupo       = EXCLUDED.grupo;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, hId);
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Text, descripcion);
        cmd.Parameters.AddWithValue("g", NpgsqlDbType.Text, (object?)grupo ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> UpsertP(NpgsqlConnection conn, string pId, string descripcion, string grupo, CancellationToken ct)
    {
        const string sql = @"INSERT INTO p_codes (p_id, descripcion, grupo)
                             VALUES (@id,@d,@g)
                             ON CONFLICT (p_id) DO UPDATE SET
                                descripcion = EXCLUDED.descripcion,
                                grupo       = EXCLUDED.grupo;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, pId);
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Text, descripcion);
        cmd.Parameters.AddWithValue("g", NpgsqlDbType.Text, (object?)grupo ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string GrupoH(string code)
    {
        if (code.StartsWith("H2", StringComparison.OrdinalIgnoreCase)) return "Peligro físico";
        if (code.StartsWith("H3", StringComparison.OrdinalIgnoreCase)) return "Peligro para la salud";
        if (code.StartsWith("H4", StringComparison.OrdinalIgnoreCase)) return "Peligro para el medio ambiente";
        if (code.StartsWith("EUH", StringComparison.OrdinalIgnoreCase)) return "Otro";
        return "Otro";
    }

    private static string GrupoP(string code)
    {
        var baseCode = code.Split('+')[0];
        if (baseCode.StartsWith("P1", StringComparison.OrdinalIgnoreCase)) return "General";
        if (baseCode.StartsWith("P2", StringComparison.OrdinalIgnoreCase)) return "Prevención";
        if (baseCode.StartsWith("P3", StringComparison.OrdinalIgnoreCase)) return "Respuesta";
        if (baseCode.StartsWith("P4", StringComparison.OrdinalIgnoreCase)) return "Almacenamiento";
        if (baseCode.StartsWith("P5", StringComparison.OrdinalIgnoreCase)) return "Eliminación";
        return "Otro";
    }

    // ============== meta table ==============
    private async Task EnsureMetaAsync(CancellationToken ct)
    {
        const string sql = @"CREATE TABLE IF NOT EXISTS bari_meta (
                               key TEXT PRIMARY KEY,
                               val TEXT NOT NULL,
                               at  TIMESTAMP WITHOUT TIME ZONE DEFAULT NOW()
                             );";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> IsDoneAsync(string key, CancellationToken ct)
    {
        const string sql = "select 1 from bari_meta where key=@k and val='done' limit 1;";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task MarkDoneAsync(string key, CancellationToken ct)
    {
        const string sql = @"INSERT INTO bari_meta(key,val) VALUES(@k,'done')
                             ON CONFLICT (key) DO UPDATE SET val='done', at=NOW();";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using BARI_web.General_Services.DataBaseConnection;
using Google.Apis.Auth.OAuth2.Responses;

namespace BARI_web.General_Services.GoogleSheets;

public class SheetsMirrorService : BackgroundService
{
    private readonly ILogger<SheetsMirrorService> _log;
    private readonly IServiceScopeFactory _scopeFactory;


    private readonly (string table, string sheet)[] _maps = new[]
    {
        ("areas","Areas"),
        ("mesones","Mesones"),
        ("marcas","Marcas"),
        ("condiciones","Condiciones"),
        ("unidades","Unidades"),
        ("categorias","Categorias"),
        ("subcategorias","Subcategorias"),
        ("reactivos","Reactivos"),
        ("contenedores","Contenedores"),
        ("ghs_pictogramas","GHS_Pictogramas"),
        ("h_codes","H_Codes"),
        ("p_codes","P_Codes"),
        ("usos","Usos"),
        ("reactivos_pictogramas","Reactivos_Pictogramas"),
         ("reactivos_p","Reactivos_P"),
          ("reactivos_h","Reactivos_H"),
           ("reactivos_usos","Reactivos_Usos")
    };

    public SheetsMirrorService(ILogger<SheetsMirrorService> log, IServiceScopeFactory scopeFactory)
    {
        _log = log;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pg = scope.ServiceProvider.GetRequiredService<PgCrud>();
                var sheets = scope.ServiceProvider.GetRequiredService<SheetsContext>();

                foreach (var (table, sheet) in _maps)
                {
                    await MirrorOneAsync(pg, sheets, table, sheet, stoppingToken);
                }
            }
            catch (TokenResponseException ex) when (
                string.Equals(ex.Error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                var description = ex.Error?.ErrorDescription ?? "invalid_grant";
                _log.LogError("Error en espejo Google Sheets. Credenciales inválidas ({Description}). Se detiene el servicio.", description);
                return;
            }
            catch (Exception ex)
            {
                if (ex is TokenResponseException tokenEx &&
                    string.Equals(tokenEx.Error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError(ex, "Error en espejo Google Sheets. Credenciales inválidas, se detiene el servicio.");
                    return;
                }

                _log.LogError(ex, "Error en espejo Google Sheets");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // backoff si falla
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private static async Task MirrorOneAsync(PgCrud pg, SheetsContext sheets, string table, string sheet, CancellationToken ct)
    {
        // 1) Lee de Postgres
        pg.UseSheet(table);
        var headers = await pg.GetHeadersAsync(ct);
        if (headers.Count == 0) return;

        var rows = await pg.ReadAllAsync(ct);

        // 2) Prepara hoja destino
        sheets.UseSheet(sheet);

        // 3) Borra todo y reescribe
        await sheets.ClearRangeAsync($"{sheet}!A:ZZ");

        // Headers
        await sheets.UpdateRangeAsync($"{sheet}!A1:{ColumnLetter(headers.Count - 1)}1",
            new List<IList<object>> { headers.Cast<object>().ToList() });

        // Datos
        if (rows.Count > 0)
        {
            var data = new List<IList<object>>(rows.Count);
            foreach (var r in rows)
            {
                var line = new object[headers.Count];
                for (int i = 0; i < headers.Count; i++)
                    line[i] = r.TryGetValue(headers[i], out var v) ? v ?? "" : "";
                data.Add(line);
            }
            await sheets.UpdateRangeAsync($"{sheet}!A2:{ColumnLetter(headers.Count - 1)}{data.Count + 1}", data);
        }
    }

    private static string ColumnLetter(int index)
    {
        int i = index; string col = "";
        while (i >= 0) { col = (char)('A' + i % 26) + col; i = i / 26 - 1; }
        return col;
    }
}

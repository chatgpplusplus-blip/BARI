using System.Globalization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace BARI_web.Features.Descarga;

public static class HorasDownloadEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapHorasDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/descarga-horas", HandleDownloadAsync);
        return app;
    }

    private static async Task<IResult> HandleDownloadAsync(
        string tipo,
        int id,
        DateTime start,
        DateTime end,
        NpgsqlDataSource dataSource)
    {
        if (!string.Equals(tipo, "docente", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tipo, "becario", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest($"Tipo inválido: {tipo}");
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        await using var conn = await dataSource.OpenConnectionAsync();
        var nombre = await LoadNombreAsync(conn, tipo, id);
        var (sql, headers, sheetName) = BuildQuery(tipo);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", start.Date);
        cmd.Parameters.AddWithValue("e", end.Date);

        await using var reader = await cmd.ExecuteReaderAsync();
        var bytes = await BuildXlsxAsync(reader, headers, sheetName);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeNombre = string.IsNullOrWhiteSpace(nombre) ? tipo : nombre.Replace(" ", "-");
        var filename = $"horas-{safeNombre}-{start:yyyyMMdd}-{end:yyyyMMdd}-{stamp}.xlsx";
        return Results.File(bytes, XlsxContentType, filename);
    }

    private static (string Sql, IReadOnlyList<string> Headers, string SheetName) BuildQuery(string tipo)
    {
        if (string.Equals(tipo, "docente", StringComparison.OrdinalIgnoreCase))
        {
            return (@"
                SELECT fecha,
                       hora_inicio,
                       hora_fin,
                       EXTRACT(EPOCH FROM (hora_fin - hora_inicio))/3600 AS horas,
                       tipo,
                       COALESCE(observaciones,'')
                FROM horarios_docentes
                WHERE docente_id=@id AND fecha BETWEEN @s AND @e
                ORDER BY fecha, hora_inicio;",
                new[] { "Fecha", "Hora inicio", "Hora fin", "Horas", "Tipo", "Observaciones" },
                "Horas Docente");
        }

        return (@"
            SELECT fecha,
                   hora_inicio,
                   hora_fin,
                   horas_decimal,
                   estado,
                   COALESCE(observaciones,'')
            FROM horas_trabajadas_becario
            WHERE becario_id=@id AND fecha BETWEEN @s AND @e
            ORDER BY fecha, hora_inicio;",
            new[] { "Fecha", "Hora inicio", "Hora fin", "Horas", "Estado", "Observaciones" },
            "Horas Becario");
    }

    private static async Task<string?> LoadNombreAsync(NpgsqlConnection conn, string tipo, int id)
    {
        var table = string.Equals(tipo, "docente", StringComparison.OrdinalIgnoreCase) ? "docentes" : "becarios";
        var idColumn = string.Equals(tipo, "docente", StringComparison.OrdinalIgnoreCase) ? "docente_id" : "becario_id";

        await using var cmd = new NpgsqlCommand($"SELECT nombre FROM {table} WHERE {idColumn}=@id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var res = await cmd.ExecuteScalarAsync();
        return res?.ToString();
    }

    private static async Task<byte[]> BuildXlsxAsync(NpgsqlDataReader reader, IReadOnlyList<string> headers, string sheetName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);

        for (var c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        ws.SheetView.FreezeRows(1);

        var r = 2;
        while (await reader.ReadAsync())
        {
            for (var c = 0; c < headers.Count; c++)
            {
                var cell = ws.Cell(r, c + 1);

                if (reader.IsDBNull(c))
                {
                    cell.Clear();
                    continue;
                }

                SetCellValue(cell, reader.GetValue(c));
            }

            r++;
        }

        var used = ws.RangeUsed();
        if (used != null)
        {
            used.SetAutoFilter();
            ws.Columns().AdjustToContents(1, 120);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        if (value is null)
        {
            cell.Clear();
            return;
        }

        switch (value)
        {
            case string s:
                cell.SetValue(s);
                break;
            case bool b:
                cell.SetValue(b);
                break;
            case int i:
                cell.SetValue(i);
                break;
            case long l:
                cell.SetValue(l);
                break;
            case short sh:
                cell.SetValue((int)sh);
                break;
            case float f:
                cell.SetValue((double)f);
                break;
            case double d:
                cell.SetValue(d);
                break;
            case decimal dec:
                cell.SetValue((double)dec);
                break;
            case DateTime dt:
                cell.SetValue(dt);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case TimeSpan ts:
                cell.SetValue(ts);
                cell.Style.DateFormat.Format = "hh:mm:ss";
                break;
            default:
                cell.SetValue(value.ToString() ?? "");
                break;
        }
    }
}

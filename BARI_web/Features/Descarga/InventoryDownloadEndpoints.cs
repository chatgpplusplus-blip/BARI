using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using BARI_web.General_Services;
using ClosedXML.Excel;

namespace BARI_web.Features.Descarga;

public static class InventoryDownloadEndpoints
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapInventoryDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/descarga", HandleDownloadAsync);
        return app;
    }

    private static async Task<IResult> HandleDownloadAsync(
        string tipo,
        string modo,
        string? campos,
        string? formato,              // <-- nuevo
        NpgsqlDataSource dataSource,
        LaboratorioState laboratorioState)
    {
        if (!AllowedTipos.Contains(tipo))
            return Results.BadRequest($"Tipo inválido: {tipo}");

        if (!string.Equals(modo, "completo", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modo, "detallado", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest($"Modo inválido: {modo}");

        var selectedFields = ResolveFields(tipo, modo, campos);
        if (selectedFields.Count == 0)
            return Results.BadRequest("No se seleccionaron campos para exportar.");

        var (sql, fieldOrder) = BuildQuery(tipo, selectedFields);

        formato ??= "xlsx"; // default: excel real

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("lab", laboratorioState.LaboratorioId);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (string.Equals(formato, "csv", StringComparison.OrdinalIgnoreCase))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            var csv = await BuildCsvAsync(reader, fieldOrder);

            // BOM UTF-8 para que Excel no muestre Â / Ã
            var bom = Encoding.UTF8.GetPreamble();
            var data = Encoding.UTF8.GetBytes(csv);
            var bytes = new byte[bom.Length + data.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(data, 0, bytes, bom.Length, data.Length);

            var filename = $"inventario-{tipo}-{modo}-{stamp}.csv";
            return Results.File(bytes, CsvContentType, filename);
        }
        else
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            var bytes = await BuildXlsxAsync(reader, tipo, fieldOrder);

            var filename = $"inventario-{tipo}-{modo}-{stamp}.xlsx";
            return Results.File(bytes, XlsxContentType, filename);
        }
    }

    // XLSX real
    private static async Task<byte[]> BuildXlsxAsync(NpgsqlDataReader reader, string sheetName, IReadOnlyList<string> fieldOrder)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SanitizeSheetName(sheetName));

        // Header
        for (var c = 0; c < fieldOrder.Count; c++)
        {
            ws.Cell(1, c + 1).Value = fieldOrder[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        ws.SheetView.FreezeRows(1);

        var r = 2;
        while (await reader.ReadAsync())
        {
            for (var c = 0; c < fieldOrder.Count; c++)
            {
                var cell = ws.Cell(r, c + 1);

                if (reader.IsDBNull(c))
                {
                    cell.Clear();
                    continue;
                }

                var v = reader.GetValue(c);

                // Si es string, normaliza para no romper filas/celdas
                if (v is string s)
                    v = NormalizeForExcelCell(s);

                SetCellValue(cell, v);
            }
            r++;
        }
        var used = ws.RangeUsed();
        if (used != null)
        {
            used.SetAutoFilter();
            ws.Columns().AdjustToContents(1, 120); // límite razonable
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string SanitizeSheetName(string name)
    {
        // Excel: máximo 31 chars y sin ciertos símbolos
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        foreach (var ch in invalid) name = name.Replace(ch.ToString(), "");
        name = name.Trim();
        if (name.Length == 0) name = "Inventario";
        return name.Length <= 31 ? name : name[..31];
    }


    private static string NormalizeForExcelCell(string s)
    {
        return s.Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Replace("\t", " ")
                .Trim();
    }


    // CSV: mejorado para Excel (lo dejo abajo)

    private static void SetCellValue(IXLCell cell, object? v)
{
    if (v is null)
    {
        cell.Clear();
        return;
    }

    switch (v)
    {
        case string s:
            cell.SetValue(s);
            return;

        case bool b:
            cell.SetValue(b);
            return;

        case int i:
            cell.SetValue(i);
            return;

        case long l:
            cell.SetValue(l);
            return;

        case short sh:
            cell.SetValue((int)sh);
            return;

        case float f:
            cell.SetValue((double)f);
            return;

        case double d:
            cell.SetValue(d);
            return;

        case decimal dec:
            cell.SetValue((double)dec); // Excel trabaja con double
            return;

        case DateTime dt:
            cell.SetValue(dt);
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
            return;

        case DateOnly d0:
            cell.SetValue(d0.ToDateTime(TimeOnly.MinValue));
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
            return;

        case TimeOnly t0:
            cell.SetValue(t0.ToTimeSpan());
            cell.Style.DateFormat.Format = "hh:mm:ss";
            return;

        case TimeSpan ts:
            cell.SetValue(ts);
            cell.Style.DateFormat.Format = "hh:mm:ss";
            return;

        default:
            // fallback seguro
            cell.SetValue(v.ToString() ?? "");
            return;
    }
}

private static async Task<string> BuildCsvAsync(NpgsqlDataReader reader, IReadOnlyList<string> fieldOrder)
    {
        // Excel ES suele usar ;  (si quieres coma, también sirve si agregas "sep=,")
        const char delim = ';';

        var sb = new StringBuilder();

        // Forzar separador en Excel
        sb.AppendLine($"sep={delim}");

        sb.AppendLine(string.Join(delim, fieldOrder.Select(EscapeCsv)));

        while (await reader.ReadAsync())
        {
            var row = new string[fieldOrder.Count];
            for (var i = 0; i < fieldOrder.Count; i++)
            {
                var value = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i)?.ToString() ?? string.Empty;
                value = NormalizeForExcelCell(value); // <- IMPORTANTÍSIMO para que Excel no rompa filas
                row[i] = EscapeCsv(value);
            }

            sb.AppendLine(string.Join(delim, row));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var needsQuotes = value.Contains(';') || value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"');
        if (!needsQuotes) return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static HashSet<string> ResolveFields(string tipo, string modo, string? campos)
    {
        var allowed = AllowedFields[tipo];
        if (string.Equals(modo, "completo", StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(allowed.Keys, StringComparer.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(campos))
        {
            return new HashSet<string>(allowed.Keys, StringComparer.OrdinalIgnoreCase);
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in campos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (allowed.ContainsKey(raw))
            {
                selected.Add(raw);
            }
        }

        return selected;
    }

    private static (string sql, IReadOnlyList<string> order) BuildQuery(string tipo, HashSet<string> fields)
    {
        var fieldMap = AllowedFields[tipo];
        var order = fieldMap.Keys.Where(fields.Contains).ToList();
        var selectParts = order.Select(key => $"{fieldMap[key]} AS {key}").ToArray();
        var select = string.Join(", ", selectParts);

        var sql = $"SELECT {select} {BaseQueries[tipo]}";
        return (sql, order);
    }

    private static readonly HashSet<string> AllowedTipos = new(StringComparer.OrdinalIgnoreCase)
    {
        "reactivos",
        "equipos",
        "materiales",
        "documentos"
    };

    private static readonly Dictionary<string, string> BaseQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reactivos"] = @"
FROM contenedores c
JOIN sustancias s ON s.sustancia_id = c.sustancia_id
LEFT JOIN categorias cat ON cat.categoria_id = s.categoria_id
LEFT JOIN marcas mc ON mc.marca_id = c.marca_id
LEFT JOIN unidades u ON u.unidad_id = c.unidad_id
LEFT JOIN areas a ON a.area_id = c.area_id
LEFT JOIN mesones m ON m.meson_id = c.meson_id
LEFT JOIN LATERAL (
    SELECT string_agg(DISTINCT sc.nombre, ', ' ORDER BY sc.nombre) AS relaciones
    FROM sustancia_subcategorias ss
    JOIN subcategorias sc ON sc.subcategoria_id = ss.subcategoria_id
    WHERE ss.sustancia_id = s.sustancia_id
) rel ON TRUE
WHERE c.laboratorio_id = @lab
ORDER BY s.sustancia_id, c.cont_id",
        ["equipos"] = @"
FROM equipos e
LEFT JOIN modelos_equipo me ON me.modelo_id = e.modelo_id
LEFT JOIN marcas mk ON mk.marca_id = me.marca_id
LEFT JOIN categorias cat ON cat.categoria_id = me.categoria_id
LEFT JOIN estados_activo ea ON ea.estado_id = e.estado_id
LEFT JOIN areas a ON a.area_id = e.area_id
LEFT JOIN mesones m ON m.meson_id = e.meson_id
LEFT JOIN LATERAL (
    SELECT string_agg(DISTINCT sc.nombre, ', ' ORDER BY sc.nombre) AS relaciones
    FROM modelo_equipo_subcategorias ms
    JOIN subcategorias sc ON sc.subcategoria_id = ms.subcategoria_id
    WHERE ms.modelo_id = me.modelo_id
) rel ON TRUE
WHERE e.laboratorio_id = @lab
ORDER BY me.modelo_id, e.equipo_id",
        ["materiales"] = @"
FROM materiales mat
LEFT JOIN categorias cat ON cat.categoria_id = mat.categoria_id
LEFT JOIN marcas mk ON mk.marca_id = mat.marca_id
LEFT JOIN estados_activo ea ON ea.estado_id = mat.estado_id
LEFT JOIN unidades u ON u.unidad_id = mat.unidad_id
LEFT JOIN areas a ON a.area_id = mat.area_id
LEFT JOIN LATERAL (
    SELECT string_agg(DISTINCT c.caja_id, ', ' ORDER BY c.caja_id) AS relaciones
    FROM cajas_materiales cm
    JOIN cajas c ON c.caja_id = cm.caja_id
    WHERE cm.material_id = mat.material_id
) rel ON TRUE
WHERE mat.laboratorio_id = @lab
ORDER BY mat.material_id",
        ["documentos"] = @"
FROM documentos d
LEFT JOIN categorias cat ON cat.categoria_id = d.categoria_id
LEFT JOIN subcategorias sub ON sub.subcategoria_id = d.subcategoria_id AND sub.categoria_id = d.categoria_id
WHERE d.laboratorio_contexto_id = @lab OR d.alcance = 'GENERAL'
ORDER BY d.documento_id"
    };

    private static readonly Dictionary<string, Dictionary<string, string>> AllowedFields =
    new(StringComparer.OrdinalIgnoreCase)
    {
        ["reactivos"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sustancia_id"] = "s.sustancia_id",
            ["nombre_comercial"] = "s.nombre_comercial",
            ["nombre_quimico"] = "s.nombre_quimico",
            ["cas"] = "s.cas",
            ["forma_fisica"] = "s.forma_fisica",
            ["sustancia_controlada"] = "s.sustancia_controlada",              // bool real
            ["categoria"] = "COALESCE(cat.nombre, s.categoria_id)",
            ["observaciones"] = "s.observaciones",

            ["cont_id"] = "c.cont_id",
            ["marca"] = "COALESCE(mc.nombre, c.marca_id)",
            ["cantidad_nominal"] = "c.cantidad_reactivo_nominal",             // numeric real
            ["cantidad_actual"] = "c.cantidad_reactivo_actual",               // numeric real
            ["unidad"] = "COALESCE(u.simbolo, u.nombre)",
            ["fecha_vencimiento"] = "c.fecha_vencimiento",                    // date real
            ["ubicacion"] = "concat_ws(' · ', a.nombre_areas, m.nombre_meson, c.nivel::text, c.posicion)",
            ["altura_cm"] = "c.altura_cm",                                    // numeric real
            ["relaciones"] = "rel.relaciones"
        },

        ["equipos"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["modelo_id"] = "me.modelo_id",
            ["nombre_modelo"] = "me.nombre_modelo",
            ["marca"] = "COALESCE(mk.nombre, me.marca_id)",
            ["categoria"] = "COALESCE(cat.nombre, me.categoria_id)",
            ["es_calibrable"] = "me.es_calibrable",                           // bool real
            ["altura_cm"] = "me.altura_cm",                                   // numeric real
            ["relaciones"] = "rel.relaciones",

            ["equipo_id"] = "e.equipo_id",
            ["nombre"] = "e.nombre",
            ["serie"] = "e.serie",
            ["estado"] = "COALESCE(ea.nombre, e.estado_id)",
            ["ubicacion"] = "concat_ws(' · ', a.nombre_areas, m.nombre_meson, e.nivel::text, e.posicion)",
            ["fecha_compra"] = "e.fecha_compra",                              // date real
            ["garantia_hasta"] = "e.garantia_hasta",                          // date real
            ["requiere_calibracion"] = "e.requiere_calibracion"               // bool real
        },

        ["materiales"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["material_id"] = "mat.material_id",
            ["nombre"] = "mat.nombre",
            ["tipo"] = "mat.tipo",
            ["categoria"] = "COALESCE(cat.nombre, mat.categoria_id)",
            ["marca"] = "COALESCE(mk.nombre, mat.marca_id)",
            ["estado"] = "COALESCE(ea.nombre, mat.estado_id)",
            ["ubicacion"] = "concat_ws(' · ', a.nombre_areas, mat.posicion)",

            // capacidad es texto (valor + unidad). Eso está bien como string.
            ["capacidad"] =
                "CASE WHEN mat.capacidad_num IS NULL THEN NULL " +
                "ELSE concat_ws(' ', mat.capacidad_num, COALESCE(u.simbolo, u.nombre)) END",

            ["cantidad"] = "mat.cantidad",                                    // numeric real
            ["altura_cm"] = "mat.altura_cm",                                  // numeric real
            ["relaciones"] = "rel.relaciones"
        },

        ["documentos"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["documento_id"] = "d.documento_id",
            ["titulo"] = "d.titulo",
            ["categoria"] = "COALESCE(cat.nombre, d.categoria_id)",
            ["subcategoria"] = "COALESCE(sub.nombre, d.subcategoria_id)",
            ["alcance"] = "d.alcance",
            ["url_archivo"] = "COALESCE(d.url, d.archivo_local)",
            ["procedencia"] = "d.procedencia",
            ["relaciones"] = "concat_ws(', ', d.modelo_equipo_id, d.material_id, d.sustancia_id, d.cont_id)",
            ["notas"] = "d.notas"
        }
    };

}

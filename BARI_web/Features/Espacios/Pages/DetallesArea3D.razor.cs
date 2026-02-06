using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Npgsql;
using BARI_web.General_Services.DataBaseConnection;
using Microsoft.JSInterop;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class DetallesArea3D : ComponentBase
    {
        [Parameter] public string AreaSlug { get; set; } = "";
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] private NpgsqlDataSource DataSource { get; set; } = default!;

        private const decimal DefaultAreaHeight = 3.0m;
        public bool IsLoading { get; private set; } = true;
        private Area3DData? _areaData;

        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal largo_m, decimal margen_m);

        private readonly record struct Point(decimal X, decimal Y);

        private class Poly
        {
            public string poly_id { get; init; } = "";
            public string canvas_id { get; init; } = "";
            public string? area_id { get; init; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public decimal ancho_m { get; set; }
            public decimal largo_m { get; set; }
            public int z_order { get; init; }
            public string? etiqueta { get; init; }
            public string? color_hex { get; init; }
            public List<Point> puntos { get; set; } = new();
        }

        private class AreaDraw
        {
            public string AreaId { get; init; } = "";
            public List<Poly> Polys { get; } = new();
            public decimal MinX { get; set; }
            public decimal MinY { get; set; }
            public decimal MaxX { get; set; }
            public decimal MaxY { get; set; }
            public string Fill { get; set; } = "#E6E6E6";
        }

        private class BlockItem
        {
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
            public decimal ancho { get; set; }
            public decimal largo { get; set; }
            public decimal? altura { get; set; }
            public string? color_hex { get; set; }
            public string? etiqueta { get; set; }
        }

        private class Door
        {
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
        }

        private class Win
        {
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
        }

        private sealed record PointDto(decimal X, decimal Y);

        private sealed record BlockDto(decimal X, decimal Y, decimal W, decimal L, decimal H, string Color, string? Label);

        private sealed record OpeningDto(decimal X, decimal Y, decimal L, string Orient);

        private sealed record Area3DData(
            string AreaId,
            decimal AreaHeight,
            decimal MinX,
            decimal MinY,
            decimal MaxX,
            decimal MaxY,
            List<List<PointDto>> Polys,
            List<BlockDto> Blocks,
            List<OpeningDto> Doors,
            List<OpeningDto> Windows
        );

        protected override async Task OnInitializedAsync()
        {
            try
            {
                var targetAreaId = await ResolveAreaIdFromSlug(AreaSlug);
                var areaInfo = await LoadAreaInfoAsync(targetAreaId);
                if (areaInfo is null)
                {
                    _areaData = null;
                    return;
                }

                var canvasId = areaInfo.canvas_id;
                if (string.IsNullOrWhiteSpace(canvasId))
                {
                    canvasId = await ResolveCanvasFromPolys(targetAreaId);
                }

                var canvas = await LoadCanvasAsync(canvasId);
                if (canvas is null)
                {
                    _areaData = null;
                    return;
                }

                var polys = await LoadPolysForAreaAsync(canvas, targetAreaId);
                if (polys.Count == 0)
                {
                    _areaData = null;
                    return;
                }

                var areaDraw = BuildAreaDrawFromPolys(targetAreaId, polys, areaInfo);
                var blocks = await LoadBlocksForArea(areaDraw, canvas.canvas_id);
                var (doors, windows) = await LoadDoorsAndWindowsForArea(areaDraw, canvas.canvas_id);

                var polyDtos = areaDraw.Polys
                    .OrderBy(p => p.z_order)
                    .Select(p => p.puntos.Select(pt => new PointDto(pt.X, pt.Y)).ToList())
                    .ToList();

                var blockDtos = blocks
                    .Select(b => new BlockDto(
                        b.abs_x,
                        b.abs_y,
                        b.ancho,
                        b.largo,
                        b.altura ?? 0.8m,
                        string.IsNullOrWhiteSpace(b.color_hex) ? "#2563eb" : b.color_hex!,
                        b.etiqueta))
                    .ToList();

                var doorDtos = doors
                    .Select(d => new OpeningDto(d.x_m, d.y_m, d.largo_m, d.orientacion))
                    .ToList();

                var winDtos = windows
                    .Select(w => new OpeningDto(w.x_m, w.y_m, w.largo_m, w.orientacion))
                    .ToList();

                _areaData = new Area3DData(
                    areaDraw.AreaId,
                    areaInfo.altura_m ?? DefaultAreaHeight,
                    areaDraw.MinX,
                    areaDraw.MinY,
                    areaDraw.MaxX,
                    areaDraw.MaxY,
                    polyDtos,
                    blockDtos,
                    doorDtos,
                    winDtos
                );
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task<CanvasLab?> LoadCanvasAsync(string? canvasId)
        {
            if (string.IsNullOrWhiteSpace(canvasId)) return null;
            Pg.UseSheet("canvas_lab");
            var canvases = await Pg.ReadAllAsync();
            var c = canvases.FirstOrDefault(row => string.Equals(Get(row, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase));
            if (c is null) return null;
            return new CanvasLab(
                c["canvas_id"], c["nombre"],
                Dec(c["ancho_m"]), Dec(c["largo_m"]), Dec(c["margen_m"]));
        }

        private async Task<string> ResolveCanvasFromPolys(string areaId)
        {
            Pg.UseSheet("poligonos");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var a = NullIfEmpty(Get(r, "area_id"));
                if (!string.Equals(a ?? "", areaId, StringComparison.OrdinalIgnoreCase)) continue;
                var canvasId = NullIfEmpty(Get(r, "canvas_id"));
                if (!string.IsNullOrWhiteSpace(canvasId))
                    return canvasId;
            }
            return "";
        }

        private async Task<List<Poly>> LoadPolysForAreaAsync(CanvasLab canvas, string targetAreaId)
        {
            var polys = new List<Poly>();
            Pg.UseSheet("poligonos");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
                    continue;

                var areaId = NullIfEmpty(Get(r, "area_id")) ?? "";
                if (!string.Equals(areaId, targetAreaId, StringComparison.OrdinalIgnoreCase))
                    continue;

                polys.Add(new Poly
                {
                    poly_id = Get(r, "poly_id"),
                    canvas_id = Get(r, "canvas_id"),
                    area_id = areaId,
                    x_m = Dec(Get(r, "x_m", "0")),
                    y_m = Dec(Get(r, "y_m", "0")),
                    ancho_m = Dec(Get(r, "ancho_m", "0")),
                    largo_m = Dec(Get(r, "largo_m", "0")),
                    z_order = Int(Get(r, "z_order", "0")),
                    etiqueta = NullIfEmpty(Get(r, "etiqueta")),
                    color_hex = NullIfEmpty(Get(r, "color_hex"))
                });
            }

            if (polys.Count == 0) return polys;

            Pg.UseSheet("poligonos_puntos");
            var pointRows = await Pg.ReadAllAsync();
            var pointsByPoly = pointRows
                .Where(r => polys.Any(p => string.Equals(p.poly_id, Get(r, "poly_id"), StringComparison.OrdinalIgnoreCase)))
                .GroupBy(r => Get(r, "poly_id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(r => Int(Get(r, "orden", "0")))
                          .Select(r => new Point(Dec(Get(r, "x_m", "0")), Dec(Get(r, "y_m", "0"))))
                          .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var p in polys)
            {
                if (pointsByPoly.TryGetValue(p.poly_id, out var pts) && pts.Count >= 3)
                {
                    p.puntos = pts;
                }
                else
                {
                    p.puntos = BuildRectPoints(p.x_m, p.y_m, p.ancho_m, p.largo_m);
                }
                UpdateBoundsFromPoints(p);
            }

            return polys;
        }

        private AreaDraw BuildAreaDrawFromPolys(string areaId, List<Poly> polys, AreaInfo areaInfo)
        {
            var a = new AreaDraw { AreaId = areaId };
            var ordered = polys.OrderBy(p => p.z_order).ToList();
            a.Polys.AddRange(ordered);
            a.MinX = ordered.Min(p => p.puntos.Min(pt => pt.X));
            a.MinY = ordered.Min(p => p.puntos.Min(pt => pt.Y));
            a.MaxX = ordered.Max(p => p.puntos.Max(pt => pt.X));
            a.MaxY = ordered.Max(p => p.puntos.Max(pt => pt.Y));
            a.Fill = ordered.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";
            return a;
        }

        private async Task<List<BlockItem>> LoadBlocksForArea(AreaDraw a, string canvasId)
        {
            var blocks = new List<BlockItem>();
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                SELECT b.bloque_id,
                       b.canvas_id,
                       b.etiqueta,
                       b.color_hex,
                       b.z_order,
                       b.pos_x,
                       b.pos_y,
                       b.ancho,
                       b.largo,
                       b.altura,
                       b.offset_x,
                       b.offset_y
                FROM bloques_int b
                LEFT JOIN mesones me
                  ON lower(trim(me.meson_id)) = lower(trim(b.meson_id))
                LEFT JOIN instalaciones ins
                  ON lower(trim(ins.instalacion_id)) = lower(trim(b.instalacion_id))
                WHERE b.canvas_id = @canvas_id
                  AND COALESCE(me.area_id, ins.area_id) = @area_id
                ORDER BY b.z_order, b.bloque_id;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("canvas_id", canvasId);
            cmd.Parameters.AddWithValue("area_id", a.AreaId);
            await using var reader = await cmd.ExecuteReaderAsync();

            var iPosX = reader.GetOrdinal("pos_x");
            var iPosY = reader.GetOrdinal("pos_y");
            var iAncho = reader.GetOrdinal("ancho");
            var iLargo = reader.GetOrdinal("largo");
            var iAltura = reader.GetOrdinal("altura");
            var iOffX = reader.GetOrdinal("offset_x");
            var iOffY = reader.GetOrdinal("offset_y");
            var iColor = reader.GetOrdinal("color_hex");
            var iEtiqueta = reader.GetOrdinal("etiqueta");

            while (await reader.ReadAsync())
            {
                var offsetX = reader.IsDBNull(iOffX) ? 0m : reader.GetDecimal(iOffX);
                var offsetY = reader.IsDBNull(iOffY) ? 0m : reader.GetDecimal(iOffY);
                var posX = reader.IsDBNull(iPosX) ? 0m : reader.GetDecimal(iPosX);
                var posY = reader.IsDBNull(iPosY) ? 0m : reader.GetDecimal(iPosY);

                var areaCenterX = (a.MinX + a.MaxX) / 2m;
                var areaCenterY = (a.MinY + a.MaxY) / 2m;

                decimal absX = posX;
                decimal absY = posY;
                if (posX == 0m && posY == 0m && (offsetX != 0m || offsetY != 0m))
                {
                    absX = areaCenterX + offsetX;
                    absY = areaCenterY + offsetY;
                }

                blocks.Add(new BlockItem
                {
                    abs_x = absX,
                    abs_y = absY,
                    ancho = reader.IsDBNull(iAncho) ? 0.6m : reader.GetDecimal(iAncho),
                    largo = reader.IsDBNull(iLargo) ? 0.4m : reader.GetDecimal(iLargo),
                    altura = reader.IsDBNull(iAltura) ? (decimal?)null : reader.GetDecimal(iAltura),
                    color_hex = reader.IsDBNull(iColor) ? "#2563eb" : reader.GetString(iColor),
                    etiqueta = reader.IsDBNull(iEtiqueta) ? null : reader.GetString(iEtiqueta)
                });
            }

            return blocks;
        }

        private async Task<(List<Door> Doors, List<Win> Windows)> LoadDoorsAndWindowsForArea(AreaDraw a, string canvasId)
        {
            var doors = new List<Door>();
            var windows = new List<Win>();

            static (string orient, decimal len) AxisAndLen(decimal x1, decimal y1, decimal x2, decimal y2)
            {
                if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                {
                    var orient = (x2 >= x1) ? "E" : "W";
                    return (orient, Math.Abs(x2 - x1));
                }
                var o = (y2 >= y1) ? "S" : "N";
                return (o, Math.Abs(y2 - y1));
            }

            Pg.UseSheet("puertas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (orient, len) = AxisAndLen(x1, y1, x2, y2);

                doors.Add(new Door
                {
                    x_m = x1,
                    y_m = y1,
                    orientacion = orient,
                    largo_m = Math.Max(0.4m, len)
                });
            }

            Pg.UseSheet("ventanas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (orient, len) = AxisAndLen(x1, y1, x2, y2);

                windows.Add(new Win
                {
                    x_m = x1,
                    y_m = y1,
                    orientacion = orient,
                    largo_m = Math.Max(0.4m, len)
                });
            }

            return (doors, windows);
        }

        private async Task<AreaInfo?> LoadAreaInfoAsync(string areaId)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                SELECT area_id,
                       nombre_areas,
                       altura_m,
                       area_total_m2,
                       anotaciones_del_area,
                       planta_id,
                       canvas_id,
                       laboratorio_id
                FROM areas
                WHERE area_id = @area_id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", areaId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new AreaInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7)
            );
        }

        private async Task<string> ResolveAreaIdFromSlug(string slugFromUrl)
        {
            var slug = Slugify((slugFromUrl ?? "").Trim());
            var candidateId = slug.Replace('-', '_');

            Pg.UseSheet("areas");
            var rows = await Pg.ReadAllAsync();

            foreach (var r in rows)
            {
                var name = NullIfEmpty(Get(r, "nombre_areas"));
                var aid = Get(r, "area_id");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var nameSlug = Slugify(name).Replace('-', '_');
                if (string.Equals(nameSlug, candidateId, StringComparison.OrdinalIgnoreCase))
                    return aid;
            }

            return candidateId;
        }

        private sealed record AreaInfo(
            string area_id,
            string nombre,
            decimal? altura_m,
            decimal? area_total_m2,
            string? anotaciones,
            string? planta_id,
            string? canvas_id,
            int laboratorio_id);

        private static List<Point> BuildRectPoints(decimal x, decimal y, decimal w, decimal h)
            => new()
            {
                new Point(x, y),
                new Point(x + w, y),
                new Point(x + w, y + h),
                new Point(x, y + h)
            };

        private static void UpdateBoundsFromPoints(Poly p)
        {
            if (p.puntos.Count == 0) return;
            var minX = p.puntos.Min(pt => pt.X);
            var minY = p.puntos.Min(pt => pt.Y);
            var maxX = p.puntos.Max(pt => pt.X);
            var maxY = p.puntos.Max(pt => pt.Y);
            p.x_m = minX;
            p.y_m = minY;
            p.ancho_m = Math.Max(0.1m, maxX - minX);
            p.largo_m = Math.Max(0.1m, maxY - minY);
        }

        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Js.InvokeVoidAsync("Bari3D.dispose", "area-3d-canvas");
            }
            catch
            {
                // Si el circuito ya se cerró, no hagas nada
            }
        }

        private static string Slugify(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.Trim().ToLowerInvariant();
            var chars = text.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-' || c == '_').ToArray();
            var cleaned = new string(chars);
            cleaned = cleaned.Replace(' ', '-');
            while (cleaned.Contains("--"))
                cleaned = cleaned.Replace("--", "-");
            return cleaned.Trim('-');
        }
    }
}

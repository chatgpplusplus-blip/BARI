using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Npgsql;
using NpgsqlTypes;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class Areas3D : ComponentBase, IAsyncDisposable, IDisposable
    {
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] private NpgsqlDataSource DataSource { get; set; } = default!;
        [Inject] private LaboratorioState LaboratorioState { get; set; } = default!;
        [Inject] private IJSRuntime Js { get; set; } = default!;

        private const decimal DefaultAreaHeight = 3.0m;

        private bool _isDisposed;

        private bool IsLoading { get; set; } = true;
        private string? _currentPlantaId;
        private Dictionary<string, string> _plantasLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AreaMeta> _areasMeta = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Canvas3DView> _views = new();
        private readonly HashSet<string> _initializedContainers = new(StringComparer.OrdinalIgnoreCase);

        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal largo_m, decimal margen_m, int? laboratorio_id);

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
            public decimal CenterX => (MinX + MaxX) / 2m;
            public decimal CenterY => (MinY + MaxY) / 2m;
        }

        private class AreaMeta
        {
            public string area_id { get; set; } = "";
            public string? planta_id { get; set; }
            public string? nombre_areas { get; set; }
            public string? canvas_id { get; set; }
            public decimal? altura_m { get; set; }
        }

        private class BlockItem
        {
            public string? area_id { get; set; }
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
            public decimal ancho { get; set; }
            public decimal largo { get; set; }
            public decimal? altura { get; set; }
            public string? color_hex { get; set; }
            public string? etiqueta { get; set; }
            public string? meson_id { get; set; }
            public int? niveles_totales { get; set; }
            public List<BoxItem> cajas { get; set; } = new();
        }

        private class BoxItem
        {
            public int nivel { get; set; }
            public string? dimensiones { get; set; }
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
        private sealed record BoxDto(int Level, string? Dimensions);
        private sealed record BlockDto(
            decimal X,
            decimal Y,
            decimal W,
            decimal L,
            decimal H,
            string Color,
            string? Label,
            bool IsMeson,
            int Levels,
            List<BoxDto> Boxes);
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

        private sealed class Canvas3DView
        {
            public Canvas3DView(CanvasLab canvas, Area3DData? data)
            {
                Canvas = canvas;
                Data = data;
            }

            public CanvasLab Canvas { get; }
            public Area3DData? Data { get; set; }
            public string ContainerId => $"areas-3d-canvas-{Canvas.canvas_id}";
        }

        protected override async Task OnInitializedAsync()
        {
            try
            {
                IsLoading = true;
                LaboratorioState.OnChange += HandleLaboratorioChanged;
                await LoadAreasMetaAsync();
                await LoadPlantasLookupAsync();
                await ReloadViewsAsync();
            }
            finally
            {
                IsLoading = false;
                StateHasChanged();
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (_isDisposed) return;
            if (IsLoading) return;
            if (_views.Count == 0) return;

            foreach (var view in _views)
            {
                if (view.Data is null) continue;
                if (_initializedContainers.Contains(view.ContainerId)) continue;
                var ok = await Js.InvokeAsync<bool>("Bari3DInitSafe", view.ContainerId, view.Data);
                if (ok)
                {
                    _initializedContainers.Add(view.ContainerId);
                }
            }

        }

        private async void HandleLaboratorioChanged()
        {
            await InvokeAsync(async () =>
            {
                IsLoading = true;
                await ReloadViewsAsync();
                IsLoading = false;
                StateHasChanged();
            });
        }

        private async Task ReloadViewsAsync()
        {
            _views.Clear();
            _initializedContainers.Clear();

            var canvases = await LoadCanvasesAsync();
            EnsurePlantaSelection();

            foreach (var canvas in canvases)
            {
                var data = await BuildCanvasDataAsync(canvas, _currentPlantaId);
                _views.Add(new Canvas3DView(canvas, data));
            }
        }

        private async Task<List<CanvasLab>> LoadCanvasesAsync()
        {
            Pg.UseSheet("canvas_lab");
            var rows = await Pg.ReadAllAsync();
            var labId = LaboratorioState.LaboratorioId;

            return rows
                .Select(r => new CanvasLab(
                    Get(r, "canvas_id"),
                    Get(r, "nombre"),
                    Dec(Get(r, "ancho_m", "0")),
                    Dec(Get(r, "largo_m", "0")),
                    Dec(Get(r, "margen_m", "0")),
                    IntOrNull(Get(r, "laboratorio_id"))))
                .Where(c => c.laboratorio_id == labId)
                .OrderBy(c => c.canvas_id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task LoadAreasMetaAsync()
        {
            _areasMeta.Clear();
            Pg.UseSheet("areas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = Get(r, "area_id");
                _areasMeta[id] = new AreaMeta
                {
                    area_id = id,
                    planta_id = NullIfEmpty(Get(r, "planta_id")),
                    nombre_areas = NullIfEmpty(Get(r, "nombre_areas")),
                    canvas_id = NullIfEmpty(Get(r, "canvas_id")),
                    altura_m = DecOrNull(Get(r, "altura_m"))
                };
            }
        }

        private async Task LoadPlantasLookupAsync()
        {
            _plantasLookup = await Pg.GetLookupAsync("plantas", "planta_id", "nombre");
        }

        private void EnsurePlantaSelection()
        {
            if (!string.IsNullOrWhiteSpace(_currentPlantaId) && _plantasLookup.ContainsKey(_currentPlantaId))
            {
                return;
            }

            _currentPlantaId = _plantasLookup.Keys.FirstOrDefault();
        }

        private async Task<Area3DData?> BuildCanvasDataAsync(CanvasLab canvas, string? plantaId)
        {
            var areaIds = _areasMeta.Values
                .Where(a => string.Equals(a.planta_id ?? "", plantaId ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.area_id)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (areaIds.Count == 0) return null;

            var polys = await LoadPolysAsync(canvas.canvas_id, areaIds);
            if (polys.Count == 0) return null;

            var areas = BuildAreasFromPolys(polys);
            var blocks = await LoadBlocksForAreasAsync(canvas.canvas_id, areas, areaIds);
            var (doors, windows) = await LoadDoorsAndWindowsForAreasAsync(canvas.canvas_id, areaIds);

            var orderedPolys = polys.OrderBy(p => p.z_order).ToList();
            var polyDtos = orderedPolys
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
                    b.etiqueta,
                    !string.IsNullOrWhiteSpace(b.meson_id),
                    b.niveles_totales ?? 0,
                    b.cajas.Select(c => new BoxDto(c.nivel, c.dimensiones)).ToList()))
                .ToList();

            var doorDtos = doors
                .Select(d => new OpeningDto(d.x_m, d.y_m, d.largo_m, d.orientacion))
                .ToList();

            var winDtos = windows
                .Select(w => new OpeningDto(w.x_m, w.y_m, w.largo_m, w.orientacion))
                .ToList();

            var minX = orderedPolys.Min(p => p.puntos.Min(pt => pt.X));
            var minY = orderedPolys.Min(p => p.puntos.Min(pt => pt.Y));
            var maxX = orderedPolys.Max(p => p.puntos.Max(pt => pt.X));
            var maxY = orderedPolys.Max(p => p.puntos.Max(pt => pt.Y));

            var height = ResolvePlantaHeight(areaIds);
            var label = _plantasLookup.TryGetValue(plantaId ?? "", out var name) ? name : "Ãreas";

            return new Area3DData(
                label,
                height,
                minX,
                minY,
                maxX,
                maxY,
                polyDtos,
                blockDtos,
                doorDtos,
                winDtos
            );
        }

        private decimal ResolvePlantaHeight(IEnumerable<string> areaIds)
        {
            var heights = areaIds
                .Select(a => _areasMeta.TryGetValue(a, out var meta) ? meta.altura_m : null)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .ToList();

            return heights.Count > 0 ? heights.Max() : DefaultAreaHeight;
        }

        private async Task<List<Poly>> LoadPolysAsync(string canvasId, List<string> areaIds)
        {
            var polys = new List<Poly>();
            var areaSet = new HashSet<string>(areaIds, StringComparer.OrdinalIgnoreCase);

            Pg.UseSheet("poligonos");
            var rows = await Pg.ReadAllAsync();
            foreach (var r in rows)
            {
                if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var areaId = NullIfEmpty(Get(r, "area_id")) ?? "";
                if (!areaSet.Contains(areaId)) continue;

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

            Pg.UseSheet("poligonos_puntos");
            var pointRows = await Pg.ReadAllAsync();
            var pointsByPoly = pointRows
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
                    p.puntos = BuildRectPoints(p);
                }
                UpdateBoundsFromPoints(p);
            }

            return polys;
        }

        private Dictionary<string, AreaDraw> BuildAreasFromPolys(List<Poly> polys)
        {
            var byArea = new Dictionary<string, AreaDraw>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in polys.GroupBy(p => p.area_id ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var a = new AreaDraw { AreaId = g.Key };
                var ordered = g.OrderBy(p => p.z_order).ToList();
                a.Polys.AddRange(ordered);
                a.MinX = ordered.Min(p => p.puntos.Min(pt => pt.X));
                a.MinY = ordered.Min(p => p.puntos.Min(pt => pt.Y));
                a.MaxX = ordered.Max(p => p.puntos.Max(pt => pt.X));
                a.MaxY = ordered.Max(p => p.puntos.Max(pt => pt.Y));
                byArea[a.AreaId] = a;
            }

            return byArea;
        }

        private async Task<List<BlockItem>> LoadBlocksForAreasAsync(string canvasId, Dictionary<string, AreaDraw> areas, List<string> areaIds)
        {
            var blocks = new List<BlockItem>();
            if (areaIds.Count == 0) return blocks;

            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                SELECT b.bloque_id,
                       b.etiqueta,
                       b.color_hex,
                       b.meson_id,
                       b.pos_x,
                       b.pos_y,
                       b.ancho,
                       b.largo,
                       b.altura,
                       b.offset_x,
                       b.offset_y,
                       me.niveles_totales,
                       COALESCE(me.area_id, ins.area_id) AS area_id
                FROM bloques_int b
                LEFT JOIN mesones me
                  ON lower(trim(me.meson_id)) = lower(trim(b.meson_id))
                LEFT JOIN instalaciones ins
                  ON lower(trim(ins.instalacion_id)) = lower(trim(b.instalacion_id))
                WHERE b.canvas_id = @canvas_id
                  AND COALESCE(me.area_id, ins.area_id) = ANY(@area_ids)
                ORDER BY b.z_order, b.bloque_id;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("canvas_id", canvasId);
            cmd.Parameters.Add("area_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = areaIds.ToArray();
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
            var iMeson = reader.GetOrdinal("meson_id");
            var iNiveles = reader.GetOrdinal("niveles_totales");
            var iAreaId = reader.GetOrdinal("area_id");

            while (await reader.ReadAsync())
            {
                var areaId = reader.IsDBNull(iAreaId) ? "" : reader.GetString(iAreaId);
                if (!areas.TryGetValue(areaId, out var area))
                {
                    continue;
                }

                var offsetX = reader.IsDBNull(iOffX) ? 0m : reader.GetDecimal(iOffX);
                var offsetY = reader.IsDBNull(iOffY) ? 0m : reader.GetDecimal(iOffY);
                var posX = reader.IsDBNull(iPosX) ? 0m : reader.GetDecimal(iPosX);
                var posY = reader.IsDBNull(iPosY) ? 0m : reader.GetDecimal(iPosY);

                decimal absX = posX;
                decimal absY = posY;
                if (posX == 0m && posY == 0m && (offsetX != 0m || offsetY != 0m))
                {
                    absX = area.CenterX + offsetX;
                    absY = area.CenterY + offsetY;
                }

                blocks.Add(new BlockItem
                {
                    area_id = areaId,
                    abs_x = absX,
                    abs_y = absY,
                    ancho = reader.IsDBNull(iAncho) ? 0.6m : reader.GetDecimal(iAncho),
                    largo = reader.IsDBNull(iLargo) ? 0.4m : reader.GetDecimal(iLargo),
                    altura = reader.IsDBNull(iAltura) ? (decimal?)null : reader.GetDecimal(iAltura),
                    color_hex = reader.IsDBNull(iColor) ? "#2563eb" : reader.GetString(iColor),
                    etiqueta = reader.IsDBNull(iEtiqueta) ? null : reader.GetString(iEtiqueta),
                    meson_id = reader.IsDBNull(iMeson) ? null : reader.GetString(iMeson),
                    niveles_totales = reader.IsDBNull(iNiveles) ? null : reader.GetInt32(iNiveles)
                });
            }

            await reader.DisposeAsync();
            await LoadCajasForBlocksAsync(blocks, conn);
            return blocks;
        }

        private static async Task LoadCajasForBlocksAsync(List<BlockItem> blocks, NpgsqlConnection conn)
        {
            var mesonIds = blocks
                .Select(b => b.meson_id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (mesonIds.Count == 0) return;

            const string sql = @"
                SELECT meson_id, nivel, dimensiones
                FROM cajas
                WHERE meson_id = ANY(@meson_ids);";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("meson_ids", mesonIds.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync();

            var iMeson = reader.GetOrdinal("meson_id");
            var iNivel = reader.GetOrdinal("nivel");
            var iDim = reader.GetOrdinal("dimensiones");

            var lookup = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.meson_id))
                .ToDictionary(b => b.meson_id!, StringComparer.OrdinalIgnoreCase);

            while (await reader.ReadAsync())
            {
                var mesonId = reader.GetString(iMeson);
                if (!lookup.TryGetValue(mesonId, out var block)) continue;

                block.cajas.Add(new BoxItem
                {
                    nivel = reader.IsDBNull(iNivel) ? 1 : reader.GetInt32(iNivel),
                    dimensiones = reader.IsDBNull(iDim) ? null : reader.GetString(iDim)
                });
            }
        }

        private async Task<(List<Door> Doors, List<Win> Windows)> LoadDoorsAndWindowsForAreasAsync(string canvasId, List<string> areaIds)
        {
            var doors = new List<Door>();
            var windows = new List<Win>();
            var areaSet = new HashSet<string>(areaIds, StringComparer.OrdinalIgnoreCase);

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
                if (!areaSet.Contains(aA ?? "") && !areaSet.Contains(aB ?? "")) continue;

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
                if (!areaSet.Contains(aA ?? "") && !areaSet.Contains(aB ?? "")) continue;

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

        private static List<Point> BuildRectPoints(Poly p)
            => new()
            {
                new Point(p.x_m, p.y_m),
                new Point(p.x_m + p.ancho_m, p.y_m),
                new Point(p.x_m + p.ancho_m, p.y_m + p.largo_m),
                new Point(p.x_m, p.y_m + p.largo_m)
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

        private void OnChangePlanta(string? plantaId)
        {
            _currentPlantaId = string.IsNullOrWhiteSpace(plantaId) ? null : plantaId;
            _ = ReloadForPlantaAsync();
        }

        private async Task ReloadForPlantaAsync()
        {
            IsLoading = true;
            await ReloadViewsAsync();
            IsLoading = false;
            StateHasChanged();
        }

        public void Dispose()
        {
            LaboratorioState.OnChange -= HandleLaboratorioChanged;
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var view in _views)
            {
                if (_initializedContainers.Contains(view.ContainerId))
                {
                    try
                    {
                        await Js.InvokeVoidAsync("Bari3D.dispose", view.ContainerId);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }

        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static decimal? DecOrNull(string? s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static int? IntOrNull(string s) => int.TryParse(s, out var n) ? n : null;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
    }
}

using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection;
using System;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class Areas : ComponentBase, IDisposable
    {
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;
        [Inject] private LaboratorioState LaboratorioState { get; set; } = default!;

        // ===== State / UI =====
        private bool IsLoading { get; set; } = true;

        // ===== Model / data records =====
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

        // Etiqueta de mesón tomada desde poligonos_interiores
        private readonly Dictionary<string, string> _mesonLabelFromInner = new(StringComparer.OrdinalIgnoreCase);

        // Abrimos puertas/ventanas con el mismo modelo lógico que usas en ModificarAreas
        private class Door
        {
            public string door_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }
            public string? area_id_b { get; set; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E"; // bandera de eje
            public decimal largo_m { get; set; } = 1.0m;
        }

        private class Win
        {
            public string win_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }
            public string? area_id_b { get; set; } // null si exterior
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
        }

        private class AreaDraw
        {
            public string AreaId { get; init; } = "";
            public List<Poly> Polys { get; } = new();
            public decimal Cx { get; set; }
            public decimal Cy { get; set; }
            public string Label { get; set; } = "";
            public decimal MinX { get; set; }
            public decimal MinY { get; set; }
            public decimal MaxX { get; set; }
            public decimal MaxY { get; set; }
            public string Fill { get; set; } = "#E6E6E6";
            public List<(decimal x1, decimal y1, decimal x2, decimal y2)> Outline { get; } = new();
        }

        private sealed class CanvasView
        {
            public CanvasView(CanvasLab canvas, Dictionary<string, AreaDraw> areas, List<Door> doors, List<Win> windows)
            {
                Canvas = canvas;
                Areas = areas;
                Doors = doors;
                Windows = windows;
            }

            public CanvasLab Canvas { get; }
            public Dictionary<string, AreaDraw> Areas { get; }
            public List<Door> Doors { get; set; }
            public List<Win> Windows { get; set; }
        }

        private readonly List<CanvasView> _canvasViews = new();
        private readonly Dictionary<string, AreaDraw> _byArea = new(StringComparer.OrdinalIgnoreCase);

        // ===== Appearance and tolerances =====
        private const decimal OutlineStroke = 0.28m;
        private const decimal TextPad = 0.20m;
        private const decimal Tolerance = 0.004m; // 4 mm
        private static decimal RoundToTolerance(decimal v) => Math.Round(v / Tolerance) * Tolerance;

        private static string ViewBox(CanvasLab canvas) => $"0 0 {S(canvas.ancho_m)} {S(canvas.largo_m)}";
        private static string AspectRatioString(CanvasLab canvas)
        {
            var ar = (double)canvas.ancho_m / (double)canvas.largo_m;
            return $"{ar:0.###} / 1";
        }

        // Lookup de plantas y meta por área
        private Dictionary<string, string> _plantasLookup = new(StringComparer.OrdinalIgnoreCase);

        private class AreaMeta
        {
            public string area_id { get; set; } = "";
            public string? planta_id { get; set; }
            public string? nombre_areas { get; set; }
        }

        private readonly Dictionary<string, AreaMeta> _areasMeta = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _areaNombre = new(StringComparer.OrdinalIgnoreCase);

        // Planta activa
        private string? _currentPlantaId;

        // ===== Lifecycle =====
        protected override async Task OnInitializedAsync()
        {
            try
            {
                IsLoading = true;
                LaboratorioState.OnChange += HandleLaboratorioChanged;

                // Cargamos metadatos de áreas (planta + nombre) una sola vez.
                await LoadAreasAsync();
                await LoadPlantasLookupAsync();

                await ReloadForLaboratorioAsync();
            }
            finally
            {
                IsLoading = false;
                StateHasChanged(); // Un solo render al final
            }
        }

        public void Dispose()
        {
            LaboratorioState.OnChange -= HandleLaboratorioChanged;
        }

        // ===== Carga de datos =====
        private async Task<List<CanvasLab>> LoadCanvasesAsync()
        {
            Pg.UseSheet("canvas_lab");
            var rows = await Pg.ReadAllAsync();
            var labId = LaboratorioState.LaboratorioId;

            var canvases = rows
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

            return canvases;
        }

        private async Task LoadAreasAsync()
        {
            _areasMeta.Clear();
            _areaNombre.Clear();

            Pg.UseSheet("areas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = Get(r, "area_id");
                var nombre = NullIfEmpty(Get(r, "nombre_areas"));
                var planta = NullIfEmpty(Get(r, "planta_id"));

                _areasMeta[id] = new AreaMeta
                {
                    area_id = id,
                    planta_id = planta,
                    nombre_areas = nombre
                };

                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(nombre))
                    _areaNombre[id] = nombre!;
            }
        }

        private async void HandleLaboratorioChanged()
        {
            await InvokeAsync(async () =>
            {
                IsLoading = true;
                await ReloadForLaboratorioAsync();
                IsLoading = false;
                StateHasChanged();
            });
        }

        private async Task ReloadForLaboratorioAsync()
        {
            _canvasViews.Clear();
            _byArea.Clear();

            var canvases = await LoadCanvasesAsync();
            foreach (var canvas in canvases)
            {
                var polys = await LoadPolysAsync(canvas.canvas_id);
                var areas = BuildAreasFromPolys(polys);
                var doors = await LoadDoorsAsync(canvas.canvas_id);
                var windows = await LoadWindowsAsync(canvas.canvas_id);
                _canvasViews.Add(new CanvasView(canvas, areas, doors, windows));

                foreach (var kv in areas)
                {
                    if (!_byArea.ContainsKey(kv.Key))
                        _byArea[kv.Key] = kv.Value;
                }
            }

            EnsurePlantaSelection();
            await BuildAreaSideListsAsync();
        }

        private async Task<List<Poly>> LoadPolysAsync(string canvasId)
        {
            var polys = new List<Poly>();

            Pg.UseSheet("poligonos");
            var rows = await Pg.ReadAllAsync();

            // Filtramos en memoria por canvas (mejorable si PgCrud permite WHERE en el futuro)
            foreach (var r in rows)
            {
                if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase))
                    continue;

                polys.Add(new Poly
                {
                    poly_id = Get(r, "poly_id"),
                    canvas_id = Get(r, "canvas_id"),
                    area_id = NullIfEmpty(Get(r, "area_id")),
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

                // BBox
                a.MinX = ordered.Min(p => p.puntos.Min(pt => pt.X));
                a.MinY = ordered.Min(p => p.puntos.Min(pt => pt.Y));
                a.MaxX = ordered.Max(p => p.puntos.Max(pt => pt.X));
                a.MaxY = ordered.Max(p => p.puntos.Max(pt => pt.Y));

                // Centro ponderado por área de polígonos
                decimal sx = 0, sy = 0, sa = 0;
                foreach (var p in ordered)
                {
                    var area = PolygonArea(p.puntos);
                    if (area <= 0) continue;
                    var centroid = PolygonCentroid(p.puntos);
                    sx += centroid.x * area; sy += centroid.y * area; sa += area;
                }
                if (sa > 0)
                {
                    a.Cx = sx / sa; a.Cy = sy / sa;
                }
                else
                {
                    a.Cx = (a.MinX + a.MaxX) / 2m;
                    a.Cy = (a.MinY + a.MaxY) / 2m;
                }

                // Label: etiqueta del poly o nombre de área desde _areasMeta
                var nombreArea = _areaNombre.TryGetValue(a.AreaId, out var n) ? n : null;
                var etiquetaPoly = ordered.Select(p => p.etiqueta).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                var label = nombreArea ?? etiquetaPoly ?? (string.IsNullOrWhiteSpace(a.AreaId) ? "SIN AREA" : a.AreaId);

                a.Label = string.IsNullOrWhiteSpace(label) ? "SIN AREA" : label.ToUpperInvariant();
                a.Fill = ordered.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";

                byArea[a.AreaId] = a;
            }

            return byArea;
        }

        private async Task LoadPlantasLookupAsync()
        {
            // Si ya cargaste 'areas', el catálogo de plantas puede usarse para selector
            _plantasLookup = await Pg.GetLookupAsync("plantas", "planta_id", "nombre");
        }

        private void EnsurePlantaSelection()
        {
            if (_byArea.Count == 0)
            {
                _currentPlantaId = _plantasLookup.Keys.FirstOrDefault();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_currentPlantaId)
                && _plantasLookup.ContainsKey(_currentPlantaId)
                && _byArea.Keys.Any(IsAreaInCurrentPlanta))
            {
                return;
            }

            var firstAreaId = _byArea.Keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(PlantaOfArea(k)));
            _currentPlantaId = PlantaOfArea(firstAreaId) ?? _plantasLookup.Keys.FirstOrDefault();
        }

        private async Task<List<Door>> LoadDoorsAsync(string canvasId)
        {
            var doors = new List<Door>();

            Pg.UseSheet("puertas");
            try
            {
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var areaA = NullIfEmpty(Get(r, "area_a"));
                    var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                    var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));

                    string orient;
                    decimal len;
                    if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    {
                        orient = (x2 >= x1) ? "E" : "W";
                        len = Math.Abs(x2 - x1);
                    }
                    else
                    {
                        orient = (y2 >= y1) ? "S" : "N";
                        len = Math.Abs(y2 - y1);
                    }

                    doors.Add(new Door
                    {
                        door_id = Get(r, "puerta_id"),
                        canvas_id = Get(r, "canvas_id"),
                        area_id_a = areaA,
                        area_id_b = NullIfEmpty(Get(r, "area_b")),
                        x_m = x1,
                        y_m = y1,
                        orientacion = orient,
                        largo_m = Math.Max(0.4m, len)
                    });
                }
            }
            catch { /* silencioso como en editor */ }

            return doors;
        }

        private async Task<List<Win>> LoadWindowsAsync(string canvasId)
        {
            var windows = new List<Win>();

            Pg.UseSheet("ventanas");
            try
            {
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "canvas_id"), canvasId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var areaA = NullIfEmpty(Get(r, "area_a"));
                    var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                    var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));

                    string orient;
                    decimal len;
                    if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    {
                        orient = (x2 >= x1) ? "E" : "W";
                        len = Math.Abs(x2 - x1);
                    }
                    else
                    {
                        orient = (y2 >= y1) ? "S" : "N";
                        len = Math.Abs(y2 - y1);
                    }

                    windows.Add(new Win
                    {
                        win_id = Get(r, "ventana_id"),
                        canvas_id = Get(r, "canvas_id"),
                        area_id_a = areaA,
                        area_id_b = NullIfEmpty(Get(r, "area_b")),
                        x_m = x1,
                        y_m = y1,
                        orientacion = orient,
                        largo_m = Math.Max(0.4m, len)
                    });
                }
            }
            catch { /* silencioso */ }

            return windows;
        }

        // ===== Build area outline from rectangles =====
        private static void BuildAreaOutline(AreaDraw a)
        {
            var H = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var V = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var p in a.Polys)
            {
                var L = p.x_m; var T = p.y_m; var R = p.x_m + p.ancho_m; var B = p.y_m + p.largo_m;

                var yTop = RoundToTolerance(T);
                var yBot = RoundToTolerance(B);
                var xLft = RoundToTolerance(L);
                var xRgt = RoundToTolerance(R);

                var x1 = RoundToTolerance(System.Math.Min(L, R));
                var x2 = RoundToTolerance(System.Math.Max(L, R));
                var y1 = RoundToTolerance(System.Math.Min(T, B));
                var y2 = RoundToTolerance(System.Math.Max(T, B));

                if (!H.ContainsKey(yTop)) H[yTop] = new();
                if (!H.ContainsKey(yBot)) H[yBot] = new();
                H[yTop].Add((x1, x2));
                H[yBot].Add((x1, x2));

                if (!V.ContainsKey(xLft)) V[xLft] = new();
                if (!V.ContainsKey(xRgt)) V[xRgt] = new();
                V[xLft].Add((y1, y2));
                V[xRgt].Add((y1, y2));
            }

            a.Outline.Clear();

            foreach (var (y, spans) in H)
            {
                if (spans.Count == 0) continue;
                var xs = new SortedSet<decimal>();
                foreach (var (a1, a2) in spans) { var lo = System.Math.Min(a1, a2); var hi = System.Math.Max(a1, a2); xs.Add(lo); xs.Add(hi); }
                var xList = xs.ToList();
                for (int i = 0; i < xList.Count - 1; i++)
                {
                    var s = xList[i]; var e = xList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;
                    int count = 0;
                    foreach (var (a1, a2) in spans)
                    {
                        var lo = System.Math.Min(a1, a2); var hi = System.Math.Max(a1, a2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }
                    if ((count % 2) == 1) a.Outline.Add((s, y, e, y));
                }
            }

            foreach (var (x, spans) in V)
            {
                if (spans.Count == 0) continue;
                var ys = new SortedSet<decimal>();
                foreach (var (b1, b2) in spans) { var lo = System.Math.Min(b1, b2); var hi = System.Math.Max(b1, b2); ys.Add(lo); ys.Add(hi); }
                var yList = ys.ToList();
                for (int i = 0; i < yList.Count - 1; i++)
                {
                    var s = yList[i]; var e = yList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;
                    int count = 0;
                    foreach (var (b1, b2) in spans)
                    {
                        var lo = System.Math.Min(b1, b2); var hi = System.Math.Max(b1, b2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }
                    if ((count % 2) == 1) a.Outline.Add((x, s, x, e));
                }
            }

            var merged = MergeCollinear(a.Outline);
            a.Outline.Clear();
            a.Outline.AddRange(merged);
        }

        private static List<(decimal x1, decimal y1, decimal x2, decimal y2)> MergeCollinear(List<(decimal x1, decimal y1, decimal x2, decimal y2)> src)
        {
            var horizontals = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var verticals = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var s in src)
            {
                if (System.Math.Abs(s.y1 - s.y2) < Tolerance)
                {
                    var y = RoundToTolerance(s.y1);
                    var (a, b) = (System.Math.Min(s.x1, s.x2), System.Math.Max(s.x1, s.x2));
                    if (!horizontals.ContainsKey(y)) horizontals[y] = new();
                    horizontals[y].Add((a, b));
                }
                else if (System.Math.Abs(s.x1 - s.x2) < Tolerance)
                {
                    var x = RoundToTolerance(s.x1);
                    var (a, b) = (System.Math.Min(s.y1, s.y2), System.Math.Max(s.y1, s.y2));
                    if (!verticals.ContainsKey(x)) verticals[x] = new();
                    verticals[x].Add((a, b));
                }
            }

            List<(decimal x1, decimal y1, decimal x2, decimal y2)> outSegs = new();

            foreach (var kv in horizontals)
            {
                var y = kv.Key; var list = kv.Value.OrderBy(t => t.x1).ToList();
                if (list.Count == 0) continue;
                var (curA, curB) = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var (a, b) = list[i];
                    if (a <= curB + Tolerance) curB = System.Math.Max(curB, b);
                    else { outSegs.Add((curA, y, curB, y)); (curA, curB) = (a, b); }
                }
                outSegs.Add((curA, y, curB, y));
            }

            foreach (var kv in verticals)
            {
                var x = kv.Key; var list = kv.Value.OrderBy(t => t.y1).ToList();
                if (list.Count == 0) continue;
                var (curA, curB) = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var (a, b) = list[i];
                    if (a <= curB + Tolerance) curB = System.Math.Max(curB, b);
                    else { outSegs.Add((x, curA, x, curB)); (curA, curB) = (a, b); }
                }
                outSegs.Add((x, curA, x, curB));
            }
            return outSegs;
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

        private string PointsString(Poly p)
            => string.Join(" ", p.puntos.Select(pt => $"{S(pt.X)},{S(pt.Y)}"));

        private static decimal PolygonArea(IReadOnlyList<Point> points)
        {
            if (points.Count < 3) return 0m;
            decimal area = 0m;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += (a.X * b.Y) - (b.X * a.Y);
            }
            return Math.Abs(area) / 2m;
        }

        private static (decimal x, decimal y) PolygonCentroid(IReadOnlyList<Point> points)
        {
            if (points.Count < 3) return (0m, 0m);
            decimal cx = 0m;
            decimal cy = 0m;
            decimal area = 0m;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var cross = (a.X * b.Y) - (b.X * a.Y);
                area += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (area == 0m) return (0m, 0m);
            area *= 0.5m;
            return (cx / (6m * area), cy / (6m * area));
        }

        // ===== Navigation / helpers =====
        private void GoToArea(string? areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return;
            var name = _byArea.TryGetValue(areaId, out var a) ? a.Label : areaId.Replace('_', ' ').ToUpperInvariant();
            var slug = Slugify(name);
            Nav.NavigateTo($"/detalles/{slug}");
        }

        private (decimal fs, decimal pillW, decimal pillH) FitLabel(AreaDraw a, int lenForW, bool twoLines)
        {
            var bboxW = (a.MaxX - a.MinX);
            var bboxH = (a.MaxY - a.MinY);
            if (bboxW <= 0 || bboxH <= 0) return (0.3m, 1.5m, 0.6m);

            var targetW = Clamp(0.6m, bboxW, bboxW * 0.82m);
            var targetH = Clamp(0.35m, bboxH, bboxH * 0.26m);

            var len = Math.Max(1, lenForW);

            var fsByW = targetW / (0.55m * len);
            var fsByH = targetH * (twoLines ? 0.55m : 0.65m);
            var fs = Clamp(0.28m, 3m, Math.Min(fsByW, fsByH));

            var pillH = Clamp(0.45m, targetH, fs * (twoLines ? 2.6m : 1.9m));
            var pillW = Clamp(0.8m, targetW, (0.55m * len) * fs + 2 * TextPad);

            return (fs, pillW, pillH);
        }


        // ===== Door/Window helpers =====
        private static decimal DoorEndX(Door d) => d.x_m + ((d.orientacion is "E" or "W") ? d.largo_m : 0m);
        private static decimal DoorEndY(Door d) => d.y_m + ((d.orientacion is "N" or "S") ? d.largo_m : 0m);
        private static decimal WinEndX(Win w) => w.x_m + ((w.orientacion is "E" or "W") ? w.largo_m : 0m);
        private static decimal WinEndY(Win w) => w.y_m + ((w.orientacion is "N" or "S") ? w.largo_m : 0m);

        private string? PlantaOfArea(string? areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return null;
            return _areasMeta.TryGetValue(areaId!, out var meta) ? meta.planta_id : null;
        }

        private bool IsAreaInCurrentPlanta(string? areaId)
            => !string.IsNullOrWhiteSpace(areaId)
               && string.Equals(PlantaOfArea(areaId) ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase);

        private bool IsDoorVisible(Door d)
        {
            if (string.IsNullOrWhiteSpace(d.area_id_a)) return false;
            return IsAreaInCurrentPlanta(d.area_id_a);
        }

        private bool IsWinVisible(Win w)
        {
            if (string.IsNullOrWhiteSpace(w.area_id_a)) return false;
            return IsAreaInCurrentPlanta(w.area_id_a);
        }

        private void OnChangePlanta(string? plantaId)
        {
            _currentPlantaId = string.IsNullOrWhiteSpace(plantaId) ? null : plantaId;

            // Refiltrar puertas/ventanas según planta activa (sin recargar todo)
            // Como se cargan en memoria, hacemos un refresco rápido:
            // Opción A: volver a cargarlas desde DB para coherencia con canvas
            _ = ReloadOpeningsForPlantaAsync();
        }

        private async Task ReloadOpeningsForPlantaAsync()
        {
            foreach (var view in _canvasViews)
            {
                view.Doors = await LoadDoorsAsync(view.Canvas.canvas_id);
                view.Windows = await LoadWindowsAsync(view.Canvas.canvas_id);
            }
            StateHasChanged();
        }

        // Slugify without accented literals (remove diacritics programmatically)
        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "sin-area";
            var normalized = s.Trim().ToLowerInvariant();

            // remove diacritics
            var formD = normalized.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in formD)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var cleaned = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);

            var sb2 = new System.Text.StringBuilder(cleaned.Length);
            foreach (var ch in cleaned)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-') sb2.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '_' || ch == '/') sb2.Append('-');
            }
            var slug = sb2.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "sin-area" : slug;
        }

        // === Para listas laterales (mismo shape que en DetallesArea) ===
        private class MesonSummary
        {
            public string meson_id { get; set; } = "";
            public string area_id { get; set; } = "";
            public string nombre_meson { get; set; } = "";
        }

        private Dictionary<string, List<MesonSummary>> _mesonesPorArea = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<MaterialMontajeView>> _materialesMontajePorArea = new(StringComparer.OrdinalIgnoreCase);

        private class MaterialMontajeView
        {
            public string material_id { get; set; } = "";
            public string area_id { get; set; } = "";
            public string nombre { get; set; } = "";
            public string? estado_id { get; set; }
            public string? posicion { get; set; }
        }

        private async Task BuildAreaSideListsAsync()
        {
            // ===== Mesones por área (base) =====
            var tempMesones = new List<MesonSummary>();
            try
            {
                Pg.UseSheet("mesones");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    var aid = Get(r, "area_id");
                    tempMesones.Add(new MesonSummary
                    {
                        meson_id = Get(r, "meson_id"),
                        area_id = aid,
                        nombre_meson = Get(r, "nombre_meson")
                    });
                }
            }
            catch { }

            // ===== Mapear poly_id -> area_id (ya tenemos _byArea listo) =====
            var polyToArea = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _byArea)
                foreach (var p in kv.Value.Polys)
                    polyToArea[p.poly_id] = kv.Key;

            // ===== Desde poligonos_interiores: override de nombre de mesón =====
            _mesonLabelFromInner.Clear();
            try
            {
                Pg.UseSheet("poligonos_interiores");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    var pid = Get(r, "area_poly_id");
                    if (!polyToArea.TryGetValue(pid, out var aidOfPoly)) continue;

                    // OVERRIDE de nombre de mesón en la lista (si este inner pertenece a un mesón)
                    var mesonId = NullIfEmpty(Get(r, "meson_id"));
                    var etiqueta = NullIfEmpty(Get(r, "etiqueta"));
                    if (!string.IsNullOrWhiteSpace(mesonId) && !string.IsNullOrWhiteSpace(etiqueta))
                    {
                        if (!_mesonLabelFromInner.ContainsKey(mesonId))
                            _mesonLabelFromInner[mesonId] = etiqueta!;
                    }
                }
            }
            catch { }

            // Aplica override de nombres a los mesones recopilados
            foreach (var m in tempMesones)
                if (_mesonLabelFromInner.TryGetValue(m.meson_id, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                    m.nombre_meson = lbl;

            // Agrupa mesones por área
            _mesonesPorArea = tempMesones
                .GroupBy(m => m.area_id ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => g.OrderBy(x => x.nombre_meson, StringComparer.OrdinalIgnoreCase).ToList(),
                              StringComparer.OrdinalIgnoreCase);

            // ===== Materiales de montaje por área =====
            var tmpMontaje = new List<MaterialMontajeView>();
            try
            {
                Pg.UseSheet("materiales_montaje");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    var areaId = Get(r, "area_id");
                    tmpMontaje.Add(new MaterialMontajeView
                    {
                        material_id = Get(r, "material_id"),
                        area_id = areaId,
                        nombre = Get(r, "nombre"),
                        estado_id = NullIfEmpty(Get(r, "estado_id")),
                        posicion = NullIfEmpty(Get(r, "posicion"))
                    });
                }
            }
            catch { }

            _materialesMontajePorArea = tmpMontaje
                .GroupBy(m => m.area_id ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => g.OrderBy(x => x.nombre, StringComparer.OrdinalIgnoreCase).ToList(),
                              StringComparer.OrdinalIgnoreCase);
        }

        // ===== util & helpers =====
        private static decimal Clamp(decimal min, decimal max, decimal v) => System.Math.Max(min, System.Math.Min(max, v));
        private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        private static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static int? IntOrNull(string s) => int.TryParse(s, out var n) ? n : null;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
    }
}

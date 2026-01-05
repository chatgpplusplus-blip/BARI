using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using BARI_web.General_Services.DataBaseConnection;
using System;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class Areas : ComponentBase
    {
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;

        // ===== State / UI =====
        private bool IsLoading { get; set; } = true;

        // ===== Model / data records =====
        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal alto_m, decimal margen_m);
        private record Poly(string poly_id, string canvas_id, string? area_id,
                            decimal x_m, decimal y_m, decimal ancho_m, decimal alto_m,
                            int z_order, string? etiqueta, string? color_hex);

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

        private CanvasLab? _canvas;
        private readonly List<Poly> _polys = new();
        private readonly Dictionary<string, AreaDraw> _byArea = new(StringComparer.OrdinalIgnoreCase);

        // Listas para puertas/ventanas (coordenadas absolutas)
        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();

        // ===== Appearance and tolerances =====
        private const decimal OutlineStroke = 0.28m;
        private const decimal TextPad = 0.20m;
        private const decimal Tolerance = 0.004m; // 4 mm
        private static decimal RoundToTolerance(decimal v) => Math.Round(v / Tolerance) * Tolerance;

        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.alto_m ?? 10m;
        private string ViewBox() => $"0 0 {S(Wm)} {S(Hm)}";
        private string AspectRatioString() { var ar = (double)Wm / (double)Hm; return $"{ar:0.###} / 1"; }

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

                await LoadCanvasAsync();
                if (_canvas is null) return;

                // Cargamos metadatos de áreas (planta + nombre) una sola vez.
                await LoadAreasAsync();

                // Cargamos polígonos del canvas y construimos estructura por área.
                await LoadPolysAsync();
                BuildAreasFromPolys();

                // Definir planta inicial (según meta de primera área válida o primera planta del catálogo)
                await LoadPlantasLookupAsync();
                InitCurrentPlanta();

                // Construir listas laterales (mesones/instalaciones) — usa _byArea y _areasMeta ya cargados
                await BuildAreaSideListsAsync();

                // Cargar puertas/ventanas ya con planta definida para filtrar de entrada
                await LoadDoorsAsync();
                await LoadWindowsAsync();
            }
            finally
            {
                IsLoading = false;
                StateHasChanged(); // Un solo render al final
            }
        }

        // ===== Carga de datos =====
        private async Task LoadCanvasAsync()
        {
            Pg.UseSheet("canvas_lab");
            var c = (await Pg.ReadAllAsync()).FirstOrDefault();
            if (c is null) return;

            _canvas = new CanvasLab(
                c["canvas_id"],
                c["nombre"],
                Dec(c["ancho_m"]),
                Dec(c["alto_m"]),
                Dec(c["margen_m"])
            );
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

        private async Task LoadPolysAsync()
        {
            _polys.Clear();

            if (_canvas is null) return;

            Pg.UseSheet("poligonos");
            var rows = await Pg.ReadAllAsync();

            // Filtramos en memoria por canvas (mejorable si PgCrud permite WHERE en el futuro)
            foreach (var r in rows)
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
                    continue;

                _polys.Add(new Poly(
                    Get(r, "poly_id"),
                    Get(r, "canvas_id"),
                    NullIfEmpty(Get(r, "area_id")),
                    Dec(Get(r, "x_m", "0")),
                    Dec(Get(r, "y_m", "0")),
                    Dec(Get(r, "ancho_m", "0")),
                    Dec(Get(r, "alto_m", "0")),
                    Int(Get(r, "z_order", "0")),
                    NullIfEmpty(Get(r, "etiqueta")),
                    NullIfEmpty(Get(r, "color_hex"))
                ));
            }
        }

        private void BuildAreasFromPolys()
        {
            _byArea.Clear();

            foreach (var g in _polys.GroupBy(p => p.area_id ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var a = new AreaDraw { AreaId = g.Key };
                var ordered = g.OrderBy(p => p.z_order).ToList();
                a.Polys.AddRange(ordered);

                // BBox
                a.MinX = ordered.Min(p => p.x_m);
                a.MinY = ordered.Min(p => p.y_m);
                a.MaxX = ordered.Max(p => p.x_m + p.ancho_m);
                a.MaxY = ordered.Max(p => p.y_m + p.alto_m);

                // Centro ponderado por área de rectángulos
                decimal sx = 0, sy = 0, sa = 0;
                foreach (var p in ordered)
                {
                    var area = p.ancho_m * p.alto_m;
                    if (area <= 0) continue;
                    var cx = p.x_m + p.ancho_m / 2m;
                    var cy = p.y_m + p.alto_m / 2m;
                    sx += cx * area; sy += cy * area; sa += area;
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
                var etiquetaPoly = ordered.Select(p => p.etiqueta).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                var nombreArea = _areaNombre.TryGetValue(a.AreaId, out var n) ? n : null;
                var label = etiquetaPoly ?? nombreArea ?? "SIN AREA";

                a.Label = label.ToUpperInvariant();
                a.Fill = ordered.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";

                BuildAreaOutline(a);
                _byArea[a.AreaId] = a;
            }
        }

        private async Task LoadPlantasLookupAsync()
        {
            // Si ya cargaste 'areas', el catálogo de plantas puede usarse para selector
            _plantasLookup = await Pg.GetLookupAsync("plantas", "planta_id", "nombre");
        }

        private void InitCurrentPlanta()
        {
            if (_byArea.Count == 0)
            {
                _currentPlantaId = _plantasLookup.Keys.FirstOrDefault();
                return;
            }

            var firstAreaId = _byArea.Keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(PlantaOfArea(k)));
            _currentPlantaId = PlantaOfArea(firstAreaId) ?? _plantasLookup.Keys.FirstOrDefault();
        }

        private async Task LoadDoorsAsync()
        {
            _doors.Clear();
            if (_canvas is null) return;

            Pg.UseSheet("puertas");
            try
            {
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Filtramos por planta activa (usando area_a si existe)
                    var areaA = NullIfEmpty(Get(r, "area_a"));
                    if (!IsAreaInCurrentPlanta(areaA)) continue;

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

                    _doors.Add(new Door
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
        }

        private async Task LoadWindowsAsync()
        {
            _windows.Clear();
            if (_canvas is null) return;

            Pg.UseSheet("ventanas");
            try
            {
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var areaA = NullIfEmpty(Get(r, "area_a"));
                    if (!IsAreaInCurrentPlanta(areaA)) continue;

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

                    _windows.Add(new Win
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
        }

        // ===== Build area outline from rectangles =====
        private static void BuildAreaOutline(AreaDraw a)
        {
            var H = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var V = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var p in a.Polys)
            {
                var L = p.x_m; var T = p.y_m; var R = p.x_m + p.ancho_m; var B = p.y_m + p.alto_m;

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

        // ===== Navigation / helpers =====
        private void GoToArea(string? areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return;
            var name = _byArea.TryGetValue(areaId, out var a) ? a.Label : areaId.Replace('_', ' ').ToUpperInvariant();
            var slug = Slugify(name);
            Nav.NavigateTo($"/detalles/{slug}");
        }

        private (decimal fs, decimal pillW, decimal pillH) FitLabel(AreaDraw a)
        {
            var bboxW = (a.MaxX - a.MinX);
            var bboxH = (a.MaxY - a.MinY);
            if (bboxW <= 0 || bboxH <= 0) return (0.3m, 1.5m, 0.6m);

            var targetW = Clamp(0.6m, bboxW, bboxW * 0.82m);
            var targetH = Clamp(0.35m, bboxH, bboxH * 0.26m);

            var len = System.Math.Max(1, a.Label.Length);
            var fsByW = (decimal)targetW / (decimal)(0.55 * len);
            var fsByH = (decimal)targetH * 0.65m;
            var fs = Clamp(0.28m, 3m, System.Math.Min(fsByW, fsByH));

            var pillH = Clamp(0.45m, targetH, fs * 1.9m);
            var pillW = Clamp(0.8m, targetW, (decimal)(0.55 * len) * fs + 2 * TextPad);

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
            await LoadDoorsAsync();
            await LoadWindowsAsync();
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

        private class InstalacionView
        {
            public string instalacion_id { get; set; } = "";
            public string nombre { get; set; } = "";
            public string? tipo_id { get; set; }
            public string? tipo_nombre { get; set; }
            public string? tipo_descripcion { get; set; }
            public string? notas { get; set; }
        }

        private Dictionary<string, List<MesonSummary>> _mesonesPorArea = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<InstalacionView>> _instalacionesPorArea = new(StringComparer.OrdinalIgnoreCase);

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

            // ===== Desde poligonos_interiores: instalaciones + override de nombre de mesón =====
            _mesonLabelFromInner.Clear();
            var neededInst = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instArea = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Pg.UseSheet("poligonos_interiores");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    var pid = Get(r, "area_poly_id");
                    if (!polyToArea.TryGetValue(pid, out var aidOfPoly)) continue;

                    // Instalaciones (para contarlas y listarlas por área)
                    var insId = NullIfEmpty(Get(r, "instalacion_id"));
                    if (!string.IsNullOrEmpty(insId))
                    {
                        neededInst.Add(insId);
                        instArea[insId] = aidOfPoly; // último gana (válido para nuestro caso)
                    }

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

            // ===== Cargar metadatos de instalaciones (solo las necesarias) =====
            var tmpInst = new Dictionary<string, InstalacionView>(StringComparer.OrdinalIgnoreCase);
            if (neededInst.Count > 0)
            {
                try
                {
                    Pg.UseSheet("instalaciones");
                    foreach (var r in await Pg.ReadAllAsync())
                    {
                        var id = Get(r, "instalacion_id");
                        if (!neededInst.Contains(id)) continue;
                        tmpInst[id] = new InstalacionView
                        {
                            instalacion_id = id,
                            nombre = Get(r, "nombre"),
                            tipo_id = NullIfEmpty(Get(r, "tipo_id")),
                            notas = NullIfEmpty(Get(r, "notas"))
                        };
                    }
                }
                catch { }

                try
                {
                    Pg.UseSheet("instalaciones_tipo");
                    var tipoNombre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var tipoNotas = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in await Pg.ReadAllAsync())
                    {
                        var tid = Get(r, "tipo_id");
                        tipoNombre[tid] = Get(r, "nombre");
                        tipoNotas[tid] = NullIfEmpty(Get(r, "notas"));
                    }
                    foreach (var it in tmpInst.Values)
                    {
                        if (!string.IsNullOrEmpty(it.tipo_id) && tipoNombre.TryGetValue(it.tipo_id, out var tn))
                        {
                            it.tipo_nombre = tn;
                            it.tipo_descripcion = tipoNotas.TryGetValue(it.tipo_id, out var td) ? td : null;
                        }
                    }
                }
                catch { }
            }

            // Agrupa instalaciones por el área detectada desde el inner
            _instalacionesPorArea = tmpInst.Values
                .GroupBy(v => instArea.TryGetValue(v.instalacion_id, out var aid) ? aid : "", StringComparer.OrdinalIgnoreCase)
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
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
    }
}

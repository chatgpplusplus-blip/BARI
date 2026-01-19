using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BARI_web.General_Services.DataBaseConnection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Npgsql;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class ModificarAreas : ComponentBase
    {
        // ===== Inyección de servicios
        [Inject] private NavigationManager Nav { get; set; } = default!;
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] private NpgsqlDataSource DataSource { get; set; } = default!;

        // ===== exclusión de botones (checkboxes "Solo X / Solo Y")
        private bool _joinAxisXOnly = false;
        private bool _joinAxisYOnly = false;
        private void OnToggleJoinX(ChangeEventArgs e) { var v = e.Value is bool b && b; _joinAxisXOnly = v; if (v) _joinAxisYOnly = false; StateHasChanged(); }
        private void OnToggleJoinY(ChangeEventArgs e) { var v = e.Value is bool b && b; _joinAxisYOnly = v; if (v) _joinAxisXOnly = false; StateHasChanged(); }
        private void OnToggleDrawX(ChangeEventArgs e) { var v = e.Value is bool b && b; _drawAxisXOnly = v; if (v) _drawAxisYOnly = false; StateHasChanged(); }
        private void OnToggleDrawY(ChangeEventArgs e) { var v = e.Value is bool b && b; _drawAxisYOnly = v; if (v) _drawAxisXOnly = false; StateHasChanged(); }

        // ===== helpers de extremo (draw helpers)
        private static decimal DoorEndX(Door d) => d.x_m + ((d.orientacion is "E" or "W") ? d.largo_m : 0m);
        private static decimal DoorEndY(Door d) => d.y_m + ((d.orientacion is "N" or "S") ? d.largo_m : 0m);
        private static decimal WinEndX(Win w) => w.x_m + ((w.orientacion is "E" or "W") ? w.largo_m : 0m);
        private static decimal WinEndY(Win w) => w.y_m + ((w.orientacion is "N" or "S") ? w.largo_m : 0m);

        // Convierte string vacío o espacios a DBNull.Value para DB
        private static object DbNullIfEmpty(string? s)
            => string.IsNullOrWhiteSpace(s) ? (object)DBNull.Value : s;
        // Valida un FK string contra un diccionario lookup; si no existe o viene vacío → null
        private string? SanitizeFk(string? id, IReadOnlyDictionary<string, string> valid)
        {
            var s = id?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return valid.ContainsKey(s) ? s : null;
        }

        // ===== modelos
        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal alto_m, decimal margen_m, int? laboratorio_id);
        private readonly record struct Point(decimal X, decimal Y);

        private class Poly
        {
            public string poly_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id { get; set; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public decimal ancho_m { get; set; }
            public decimal alto_m { get; set; }
            public int z_order { get; set; }
            public string? etiqueta { get; set; }
            public string? color_hex { get; set; }
            public List<Point> puntos { get; set; } = new();
            public Poly Clone() => (Poly)MemberwiseClone();
            public (decimal L, decimal T, decimal R, decimal B) Bounds() => (x_m, y_m, x_m + ancho_m, y_m + alto_m);
        }
        private class Door
        {
            public string door_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }
            public string? area_id_b { get; set; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
            public int z_order { get; set; } // UI only (no persist)
        }
        private class Win
        {
            public string win_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }   // <-- igual que puerta
            public string? area_id_b { get; set; }   // <-- igual que puerta (vecina o null si exterior)
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
            public int z_order { get; set; } // UI only (no persist)
        }

        private enum Handle { NW, NE, SW, SE, None }

        // ===== estado
        private CanvasLab? _canvas;
        private readonly List<Poly> _polys = new();
        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();
        private Dictionary<string, string> _areasLookup = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _canvasLookup = new(StringComparer.OrdinalIgnoreCase);
        private string? _currentCanvasId;

        // --- Vista filtrada por planta activa ---
        private IEnumerable<Poly> VisiblePolys() => _polys.Where(IsVisible);

        private Poly? _sel;
        private string _selId { get => _sel?.poly_id ?? ""; set { _sel = _polys.FirstOrDefault(p => p.poly_id == value); NormalizeSelected(); StateHasChanged(); } }
        private ElementReference _svgRef;
        private string? _hoverId;

        private bool _saving = false;
        private string _saveMsg = "";

        private string? _selDoorId;
        private string? _selWinId;
        private Door? SelDoor => _doors.FirstOrDefault(d => d.door_id == _selDoorId);
        private Win? SelWin => _windows.FirstOrDefault(w => w.win_id == _selWinId);

        private bool _showDoorWinPanels = true;

        // Snap/grid controls
        private bool _snapToGrid = true;
        private decimal _gridStep = 0.25m;

        // drag área
        private (decimal x, decimal y)? _dragStart;
        private Poly? _beforeDragPoly;
        private Handle _activeHandle = Handle.None;
        private List<Point>? _beforeDragPoints;
        private int _dragVertexIndex = -1;
        private int _selectedVertexIndex = -1;
        private readonly HashSet<int> _vertexEditIndices = new();
        private string? _vertexEditPolyId;
        private bool _vertexEditSelecting = false;
        private bool _vertexEditActive = false;
        private bool HasVertexEditMode => _vertexEditIndices.Count > 0;

        // drag puerta/ventana
        private Door? _dragDoor; private bool _resizeDoor = false;
        private Win? _dragWin; private bool _resizeWin = false;

        // pan/zoom
        private double _zoom = 1.0;
        private decimal _panX = 0m, _panY = 0m;
        private (double x, double y)? _panStart;
        private bool _panMoved = false;

        // dibujo
        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.alto_m ?? 10m;
        private bool _isDrawing = false;
        private readonly List<Point> _draftPoints = new();
        private string? _drawAreaId;
        private string? _drawMsg;
        private bool _drawAxisXOnly = false;
        private bool _drawAxisYOnly = false;
        private string _newAreaName = string.Empty;
        private string? _newAreaPlantaId;
        private string? _newAreaMsg;
        private bool _creatingArea = false;

        private string ViewBox()
        {
            var vw = (double)Wm / _zoom; var vh = (double)Hm / _zoom;
            var maxX = (decimal)Wm - (decimal)vw; var maxY = (decimal)Hm - (decimal)vh;
            _panX = Clamp(0m, maxX, _panX); _panY = Clamp(0m, maxY, _panY);
            return $"{S((double)_panX)} {S((double)_panY)} {S(vw)} {S(vh)}";
        }
        private string AspectRatioString() { var ar = (double)Wm / (double)Hm; return $"{ar:0.###} / 1"; }

        private (double size, bool show) LabelFontSizeAndVisibility(Poly p, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return (0, false);
            var pad = 0.18m;
            var availW = Math.Max(0m, p.ancho_m - 2 * pad);
            var availH = Math.Max(0m, p.alto_m - 2 * pad);
            if (availW <= 0m || availH <= 0m) return (0, false);
            var len = Math.Max(1, label.Length);
            double fsByWidth = (double)availW / (0.55 * len);
            double fsByHeight = (double)availH * 0.6;
            var fs = Math.Max(0.25, Math.Min(fsByWidth, fsByHeight));
            bool show = fs >= 0.25 && (double)availW >= 0.5 && (double)availH >= 0.4;
            return (fs, show);
        }
        private string AreaLabel(Poly p) => p.etiqueta ?? (_areasLookup.TryGetValue(p.area_id ?? "", out var v) ? v : "SIN ÁREA");
        private string AreaColor(Poly p)
        {
            if (!string.IsNullOrWhiteSpace(p.color_hex)) return p.color_hex!;
            var key = p.area_id ?? ""; var idx = Math.Abs(key.GetHashCode());
            var palette = new[] { "#E6E6E6", "#F2E6D8", "#D8E6F2", "#E6F2D8", "#F2D8E6" };
            return palette[idx % palette.Length];
        }

        // ===== init
        protected override async Task OnInitializedAsync()
        {
            _canvasLookup = await Pg.GetLookupAsync("canvas_lab", "canvas_id", "nombre");
            _currentCanvasId = _canvasLookup.Keys.FirstOrDefault();
            await ReloadCanvasDataAsync();
        }

        private void CenterView()
        {
            var vw = (decimal)((double)Wm / _zoom);
            var vh = (decimal)((double)Hm / _zoom);
            _panX = (Wm - vw) / 2m; _panY = (Hm - vh) / 2m;
        }

        private async Task ReloadCanvasDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentCanvasId))
            {
                _canvas = null;
                return;
            }

            await LoadCanvasAsync(_currentCanvasId);
            if (_canvas is null) return;

            _areasLookup = await Pg.GetLookupAsync("areas", "area_id", "nombre_areas");
            await LoadAreasMetaAsync();
            await LoadPolysAsync();
            await LoadPolyPointsDataAsync();
            _plantasLookup = await Pg.GetLookupAsync("plantas", "planta_id", "nombre");

            _currentPlantaId = ResolveInitialPlanta();
            _drawAreaId = DefaultAreaIdForCurrentPlanta();
            _newAreaPlantaId = _currentPlantaId;
            _sel = _polys.FirstOrDefault(IsVisible);
            _selectedVertexIndex = -1;
            _draftPoints.Clear();
            _isDrawing = false;

            await LoadDoorsAsync();
            await LoadWindowsAsync();

            CenterView();
            NormalizeSelected();
            StateHasChanged();
        }

        private async Task LoadCanvasAsync(string canvasId)
        {
            Pg.UseSheet("canvas_lab");
            var canvases = await Pg.ReadAllAsync();
            var c = canvases.FirstOrDefault(row => string.Equals(row["canvas_id"], canvasId, StringComparison.OrdinalIgnoreCase))
                ?? canvases.FirstOrDefault();
            if (c is null)
            {
                _canvas = null;
                return;
            }

            _canvas = new CanvasLab(
                c["canvas_id"],
                c["nombre"],
                Dec(c["ancho_m"]),
                Dec(c["alto_m"]),
                Dec(c["margen_m"]),
                IntOrNull(NullIfEmpty(Get(c, "laboratorio_id"))));
            _currentCanvasId = _canvas.canvas_id;
        }

        private async Task LoadAreasMetaAsync()
        {
            _areasMeta.Clear();
            Pg.UseSheet("areas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = r["area_id"];
                _areasMeta[id] = new AreaMeta
                {
                    area_id = id,
                    planta_id = NullIfEmpty(Get(r, "planta_id")),
                    canvas_id = NullIfEmpty(Get(r, "canvas_id")),
                    laboratorio_id = IntOrNull(NullIfEmpty(Get(r, "laboratorio_id"))),
                    altura_m = Dec(Get(r, "altura_m", "0")),
                    anotaciones = string.IsNullOrWhiteSpace(Get(r, "anotaciones_del_area")) ? "SIN MODIFICACIONES" : Get(r, "anotaciones_del_area")
                };
            }
        }

        private async Task LoadPolysAsync()
        {
            _polys.Clear();
            if (_canvas is null) return;

            Pg.UseSheet("poligonos");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(r["canvas_id"], _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;
                _polys.Add(new Poly
                {
                    poly_id = r["poly_id"],
                    canvas_id = r["canvas_id"],
                    area_id = NullIfEmpty(r["area_id"]),
                    x_m = Dec(Get(r, "x_m", "0")),
                    y_m = Dec(Get(r, "y_m", "0")),
                    ancho_m = Dec(Get(r, "ancho_m", "0")),
                    alto_m = Dec(Get(r, "alto_m", "0")),
                    z_order = Int(Get(r, "z_order", "0")),
                    etiqueta = NullIfEmpty(Get(r, "etiqueta")),
                    color_hex = NullIfEmpty(Get(r, "color_hex"))
                });
            }
        }

        private string? ResolveInitialPlanta()
        {
            var firstWithArea = _polys.FirstOrDefault(p => PlantaOf(p) != null);
            var planta = PlantaOf(firstWithArea ?? _polys.FirstOrDefault() ?? new Poly());
            if (!string.IsNullOrWhiteSpace(planta)) return planta;
            var fromAreas = _areasMeta.Values
                .FirstOrDefault(meta => string.Equals(meta.canvas_id ?? "", _canvas?.canvas_id ?? "", StringComparison.OrdinalIgnoreCase))
                ?.planta_id;
            if (!string.IsNullOrWhiteSpace(fromAreas)) return fromAreas;
            return _plantasLookup.Keys.FirstOrDefault();
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
                    if (!string.Equals(r["canvas_id"], _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;
                    var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                    var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));

                    string orient; decimal len;
                    if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    {
                        orient = (x2 >= x1) ? "E" : "W"; len = Math.Abs(x2 - x1);
                    }
                    else
                    {
                        orient = (y2 >= y1) ? "S" : "N"; len = Math.Abs(y2 - y1);
                    }

                    _doors.Add(new Door
                    {
                        door_id = Get(r, "puerta_id"),
                        canvas_id = r["canvas_id"],
                        area_id_a = NullIfEmpty(Get(r, "area_a")),
                        area_id_b = NullIfEmpty(Get(r, "area_b")),
                        x_m = x1,
                        y_m = y1,
                        orientacion = orient,
                        largo_m = Math.Max(0.4m, len),
                        z_order = 0
                    });
                }
            }
            catch { }
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
                    if (!string.Equals(r["canvas_id"], _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                    var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                    var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));

                    string orient; decimal len;
                    if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    {
                        orient = (x2 >= x1) ? "E" : "W"; len = Math.Abs(x2 - x1);
                    }
                    else
                    {
                        orient = (y2 >= y1) ? "S" : "N"; len = Math.Abs(y2 - y1);
                    }

                    _windows.Add(new Win
                    {
                        win_id = Get(r, "ventana_id"),
                        canvas_id = r["canvas_id"],
                        area_id_a = NullIfEmpty(Get(r, "area_a")),
                        area_id_b = NullIfEmpty(Get(r, "area_b")),
                        x_m = x1,
                        y_m = y1,
                        orientacion = orient,
                        largo_m = Math.Max(0.4m, len),
                        z_order = 0
                    });
                }
            }
            catch { }
        }

        private async Task LoadPolyPointsDataAsync()
        {
            var pointsByPoly = new Dictionary<string, List<(int orden, Point point)>>(StringComparer.OrdinalIgnoreCase);
            Pg.UseSheet("poligonos_puntos");
            foreach (var row in await Pg.ReadAllAsync())
            {
                var polyId = Get(row, "poly_id");
                if (string.IsNullOrWhiteSpace(polyId)) continue;
                var orden = Int(Get(row, "orden", "0"));
                var x = Dec(Get(row, "x_m", "0"));
                var y = Dec(Get(row, "y_m", "0"));
                if (!pointsByPoly.TryGetValue(polyId, out var list))
                {
                    list = new List<(int, Point)>();
                    pointsByPoly[polyId] = list;
                }
                list.Add((orden, new Point(x, y)));
            }

            foreach (var p in _polys)
            {
                if (pointsByPoly.TryGetValue(p.poly_id, out var list) && list.Count >= 3)
                {
                    p.puntos = list.OrderBy(item => item.orden).Select(item => item.point).ToList();
                }
                else
                {
                    p.puntos = BuildRectPointsForPoly(p);
                }
                UpdateBoundsFromPolyPoints(p);
            }
        }

        private static List<Point> BuildRectPointsForPoly(Poly p)
            => new()
            {
                new Point(p.x_m, p.y_m),
                new Point(p.x_m + p.ancho_m, p.y_m),
                new Point(p.x_m + p.ancho_m, p.y_m + p.alto_m),
                new Point(p.x_m, p.y_m + p.alto_m)
            };

        private void UpdateBoundsFromPolyPoints(Poly p)
        {
            if (p.puntos.Count == 0) return;
            var minX = p.puntos.Min(pt => pt.X);
            var minY = p.puntos.Min(pt => pt.Y);
            var maxX = p.puntos.Max(pt => pt.X);
            var maxY = p.puntos.Max(pt => pt.Y);
            p.x_m = minX;
            p.y_m = minY;
            p.ancho_m = Math.Max(0.1m, maxX - minX);
            p.alto_m = Math.Max(0.1m, maxY - minY);
        }

        private static (decimal minX, decimal minY, decimal maxX, decimal maxY) BoundsOfPointList(IReadOnlyList<Point> points)
        {
            var minX = points.Min(pt => pt.X);
            var minY = points.Min(pt => pt.Y);
            var maxX = points.Max(pt => pt.X);
            var maxY = points.Max(pt => pt.Y);
            return (minX, minY, maxX, maxY);
        }

        private string PointsStringFromPoints(IEnumerable<Point> points)
            => string.Join(" ", points.Select(p => $"{S(p.X)},{S(p.Y)}"));

        private string PointsStringForPoly(Poly p) => PointsStringFromPoints(p.puntos);

        private static decimal DistanceBetweenPoints(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return (decimal)Math.Sqrt((double)(dx * dx + dy * dy));
        }

        // ======== UI helpers (SVG)
        private RenderFragment VertexHandle(Poly p, decimal lx, decimal ly, Handle h) => builder =>
        {
            var size = 0.38m; var x = (lx - size / 2); var y = (ly - size / 2); int seq = 0;
            builder.OpenElement(seq++, "rect");
            builder.AddAttribute(seq++, "x", S(x)); builder.AddAttribute(seq++, "y", S(y));
            builder.AddAttribute(seq++, "width", S(size)); builder.AddAttribute(seq++, "height", S(size));
            builder.AddAttribute(seq++, "fill", "#13a076"); builder.AddAttribute(seq++, "stroke", "#0b6b50");
            builder.AddAttribute(seq++, "stroke-width", S(0.03m)); builder.AddAttribute(seq++, "style", "cursor:nwse-resize");
            builder.AddAttribute(seq++, "onpointerdown:preventDefault", true); builder.AddAttribute(seq++, "onpointerdown:stopPropagation", true);
            builder.AddAttribute(seq++, "onpointerdown", EventCallback.Factory.Create<PointerEventArgs>(this, (PointerEventArgs e) => OnPointerDownResizeArea(e, h)));
            builder.CloseElement();
        };

        private RenderFragment VertexPicker(Poly p, decimal lx, decimal ly, Handle h) => builder =>
        {
            var r = 0.22m; var cx = lx; var cy = ly;
            bool isA = _pickA is not null && _pickA.Value.polyId == p.poly_id && _pickA.Value.h == h;
            bool isB = _pickB is not null && _pickB.Value.polyId == p.poly_id && _pickB.Value.h == h;
            var fill = isA ? "#dc2626" : (isB ? "#f59e0b" : "#ffffff");
            var stroke = isA ? "#7f1d1d" : (isB ? "#b45309" : "#0b6b50");
            int seq = 0;
            builder.OpenElement(seq++, "circle");
            builder.AddAttribute(seq++, "cx", S(cx)); builder.AddAttribute(seq++, "cy", S(cy)); builder.AddAttribute(seq++, "r", S(r));
            builder.AddAttribute(seq++, "fill", fill); builder.AddAttribute(seq++, "stroke", stroke); builder.AddAttribute(seq++, "stroke-width", S(0.06m));
            builder.AddAttribute(seq++, "style", "cursor:pointer");
            builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => PickVertex(p.poly_id, h)));
            builder.CloseElement();
        };

        // ======== Normalización / colisiones
        private static decimal R3(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        private static decimal Clamp(decimal min, decimal max, decimal v) => Math.Max(min, Math.Min(max, v));

        private void NormalizeSelected(bool noOverlap = false, char primaryAxis = '\0')
        {
            if (_sel is null) return;
            _sel.ancho_m = Clamp(0.1m, Wm, _sel.ancho_m);
            _sel.alto_m = Clamp(0.1m, Hm, _sel.alto_m);
            _sel.x_m = Clamp(0, Wm - _sel.ancho_m, _sel.x_m);
            _sel.y_m = Clamp(0, Hm - _sel.alto_m, _sel.y_m);
            if (noOverlap) ResolveCollisions(_sel, primaryAxis);
            _sel.x_m = R3(_sel.x_m); _sel.y_m = R3(_sel.y_m); _sel.ancho_m = R3(_sel.ancho_m); _sel.alto_m = R3(_sel.alto_m);
            if (_sel.z_order < 0) _sel.z_order = 0; if (_sel.z_order > 1_000_000) _sel.z_order = 1_000_000;
        }

        private decimal SnapValue(decimal value)
        {
            if (_gridStep <= 0) return value;
            return Math.Round(value / _gridStep, MidpointRounding.AwayFromZero) * _gridStep;
        }

        private void ApplySnap(Poly target, Poly basePoly, Handle handle, bool snapEnabled)
        {
            if (!snapEnabled) return;

            switch (handle)
            {
                case Handle.None:
                    target.x_m = SnapValue(target.x_m);
                    target.y_m = SnapValue(target.y_m);
                    break;
                case Handle.NE:
                    target.ancho_m = SnapValue(target.ancho_m);
                    target.alto_m = SnapValue(target.alto_m);
                    target.x_m = basePoly.x_m;
                    target.y_m = basePoly.y_m + basePoly.alto_m - target.alto_m;
                    break;
                case Handle.SE:
                    target.ancho_m = SnapValue(target.ancho_m);
                    target.alto_m = SnapValue(target.alto_m);
                    target.x_m = basePoly.x_m;
                    target.y_m = basePoly.y_m;
                    break;
                case Handle.NW:
                    target.ancho_m = SnapValue(target.ancho_m);
                    target.alto_m = SnapValue(target.alto_m);
                    target.x_m = basePoly.x_m + basePoly.ancho_m - target.ancho_m;
                    target.y_m = basePoly.y_m + basePoly.alto_m - target.alto_m;
                    break;
                case Handle.SW:
                    target.ancho_m = SnapValue(target.ancho_m);
                    target.alto_m = SnapValue(target.alto_m);
                    target.x_m = basePoly.x_m + basePoly.ancho_m - target.ancho_m;
                    target.y_m = basePoly.y_m;
                    break;
            }
        }

        private void ResolveCollisions(Poly p, char primaryAxis)
        {
            const decimal eps = 0.001m; int safety = 16;
            while (safety-- > 0)
            {
                var (L1, T1, R1, B1) = p.Bounds();
                (decimal dx, decimal dy) best = (0, 0); double bestMag = double.MaxValue; bool any = false;
                foreach (var o in VisiblePolys())
                {
                    if (o.poly_id == p.poly_id) continue;
                    var (L2, T2, R2, B2) = o.Bounds();
                    bool overlap = (L1 < R2 && R1 > L2 && T1 < B2 && B1 > T2);
                    if (!overlap) continue; any = true;
                    var cLeft = (dx: L2 - R1 - eps, dy: 0m);
                    var cRight = (dx: R2 - L1 + eps, dy: 0m);
                    var cUp = (dx: 0m, dy: T2 - B1 - eps);
                    var cDown = (dx: 0m, dy: B2 - T1 + eps);
                    var cands = new[] { cLeft, cRight, cUp, cDown };
                    foreach (var c in cands)
                    {
                        if (primaryAxis == 'x' && c.dy != 0) continue;
                        if (primaryAxis == 'y' && c.dx != 0) continue;
                        var mag = Math.Abs((double)c.dx) + Math.Abs((double)c.dy);
                        if (mag < bestMag) { bestMag = mag; best = c; }
                    }
                }
                if (!any || bestMag == double.MaxValue) break;
                p.x_m = Clamp(0m, Wm - p.ancho_m, p.x_m + best.dx);
                p.y_m = Clamp(0m, Hm - p.alto_m, p.y_m + best.dy);
            }
        }

        private static bool RectOverlap(Poly a, Poly b)
        {
            var (L1, T1, R1, B1) = a.Bounds(); var (L2, T2, R2, B2) = b.Bounds();
            return (L1 < R2 && R1 > L2 && T1 < B2 && B1 > T2);
        }

        private bool OverlapsAny(Poly candidate, List<Point>? previous)
        {
            var points = candidate.puntos.Count > 0 ? candidate.puntos : previous ?? new List<Point>();
            if (points.Count < 3) return false;
            foreach (var other in VisiblePolys())
            {
                if (other.poly_id == candidate.poly_id) continue;
                if (!BoundsOverlap(points, other.puntos)) continue;
                if (PolygonsOverlap(points, other.puntos)) return true;
            }
            return false;
        }

        private bool OverlapsAnyPoints(string polyId, IReadOnlyList<Point> points)
        {
            foreach (var other in VisiblePolys())
            {
                if (other.poly_id == polyId) continue;
                if (!BoundsOverlap(points, other.puntos)) continue;
                if (PolygonsOverlap(points, other.puntos)) return true;
            }
            return false;
        }

        private static bool BoundsOverlap(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
        {
            if (a.Count == 0 || b.Count == 0) return false;
                var (aMinX, aMinY, aMaxX, aMaxY) = BoundsOfPointList(a);
            var (bMinX, bMinY, bMaxX, bMaxY) = BoundsOfPointList(b);
            return aMinX < bMaxX && aMaxX > bMinX && aMinY < bMaxY && aMaxY > bMinY;
        }

        private static bool PolygonsOverlap(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
        {
            if (a.Count < 3 || b.Count < 3) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var a1 = a[i];
                var a2 = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; j++)
                {
                    var b1 = b[j];
                    var b2 = b[(j + 1) % b.Count];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }
            if (PointInPolygon(a[0], b)) return true;
            if (PointInPolygon(b[0], a)) return true;
            return false;
        }

        private static bool SegmentsIntersect(Point p1, Point p2, Point q1, Point q2)
        {
            static decimal Cross(Point a, Point b, Point c)
                => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

            var d1 = Cross(p1, p2, q1);
            var d2 = Cross(p1, p2, q2);
            var d3 = Cross(q1, q2, p1);
            var d4 = Cross(q1, q2, p2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }

            return d1 == 0 && OnSegment(p1, p2, q1)
                || d2 == 0 && OnSegment(p1, p2, q2)
                || d3 == 0 && OnSegment(q1, q2, p1)
                || d4 == 0 && OnSegment(q1, q2, p2);
        }

        private static bool OnSegment(Point a, Point b, Point p)
            => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
               && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

        private static bool PointInPolygon(Point point, IReadOnlyList<Point> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                var intersect = ((pi.Y > point.Y) != (pj.Y > point.Y))
                    && (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y + 0.0000001m) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        // ======== Conversión pantalla->mundo
        private (decimal x, decimal y) ScreenToWorld(double offsetX, double offsetY)
            => (_panX + (decimal)(offsetX / (PxPerM() * _zoom)),
                _panY + (decimal)(offsetY / (PxPerM() * _zoom)));

        // ===== Anti-túnel
        const decimal EPS = 0.001m;

        // --- Door/Window edge tracking tuning ---
        const decimal EDGE_EPS = 0.0008m;      // tolerancia geométrica (~0.8 mm)
        const decimal CORNER_GUARD = 0.04m;    // guarda-esquinas para penalizar vértices (4 cm)
        const decimal MIN_OVERLAP_FOR_NEIGHBOUR = 0.06m; // solape mínimo (6 cm) para vecino válido

        private static bool RangeOverlap(decimal a1, decimal a2, decimal b1, decimal b2)
        {
            if (a1 > a2) (a1, a2) = (a2, a1); if (b1 > b2) (b1, b2) = (b2, b1);
            return a1 < b2 && a2 > b1;
        }

        private (decimal x, decimal y) MoveWithCollisions(Poly p, decimal baseX, decimal baseY, decimal dx, decimal dy)
        {
            var nx = baseX;
            if (dx != 0)
            {
                var target = Clamp(0m, Wm - p.ancho_m, baseX + dx);
                foreach (var o in VisiblePolys())
                {
                    if (o.poly_id == p.poly_id) continue;
                    var pTop = baseY; var pBottom = baseY + p.alto_m;
                    if (!RangeOverlap(pTop, pBottom, o.y_m, o.y_m + o.alto_m)) continue;
                    if (dx > 0)
                    {
                        var stop = o.x_m - p.ancho_m - EPS;
                        if (nx < o.x_m && target > stop) target = Math.Min(target, stop);
                    }
                    else
                    {
                        var stop = o.x_m + o.ancho_m + EPS;
                        if (nx > stop && target < stop) target = Math.Max(target, stop);
                    }
                }
                nx = target;
            }

            var ny = baseY;
            if (dy != 0)
            {
                var target = Clamp(0m, Hm - p.alto_m, baseY + dy);
                foreach (var o in VisiblePolys())
                {
                    if (o.poly_id == p.poly_id) continue;
                    var pLeft = nx; var pRight = nx + p.ancho_m;
                    if (!RangeOverlap(pLeft, pRight, o.x_m, o.x_m + o.ancho_m)) continue;
                    if (dy > 0)
                    {
                        var stop = o.y_m - p.alto_m - EPS;
                        if (ny < o.y_m && target > stop) target = Math.Min(target, stop);
                    }
                    else
                    {
                        var stop = o.y_m + o.alto_m + EPS;
                        if (ny > stop && target < stop) target = Math.Max(target, stop);
                    }
                }
                ny = target;
            }
            return (nx, ny);
        }

        private decimal ClampRight(Poly p, decimal baseX, decimal y, decimal widthTarget)
        {
            var right = Math.Min(baseX + widthTarget, Wm - EPS);
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (!RangeOverlap(y, y + p.alto_m, o.y_m, o.y_m + o.alto_m)) continue;
                var stop = o.x_m - EPS;
                if (baseX < o.x_m && right > stop) right = Math.Min(right, stop);
            }
            return Math.Max(0.1m, right - baseX);
        }

        private decimal ClampLeft(Poly p, decimal y, decimal baseRight, decimal widthTarget)
        {
            var left = Math.Max(baseRight - widthTarget, 0m + EPS);
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (!RangeOverlap(y, y + p.alto_m, o.y_m, o.y_m + o.alto_m)) continue;
                var stop = o.x_m + o.ancho_m + EPS;
                if (baseRight > stop && left < stop) left = Math.Max(left, stop);
            }
            var newW = baseRight - left;
            return Math.Max(0.1m, newW);
        }

        private decimal ClampBottom(Poly p, decimal x, decimal baseY, decimal heightTarget)
        {
            var bottom = Math.Min(baseY + heightTarget, Hm - EPS);
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (!RangeOverlap(x, x + p.ancho_m, o.x_m, o.x_m + o.ancho_m)) continue;
                var stop = o.y_m - EPS;
                if (baseY < o.y_m && bottom > stop) bottom = Math.Min(bottom, stop);
            }
            return Math.Max(0.1m, bottom - baseY);
        }

        private decimal ClampTop(Poly p, decimal x, decimal baseBottom, decimal heightTarget)
        {
            var top = Math.Max(baseBottom - heightTarget, 0m + EPS);
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (!RangeOverlap(x, x + p.ancho_m, o.x_m, o.x_m + o.ancho_m)) continue;
                var stop = o.y_m + o.alto_m + EPS;
                if (baseBottom > stop && top < stop) top = Math.Max(top, stop);
            }
            var newH = baseBottom - top;
            return Math.Max(0.1m, newH);
        }

        // ======== Drag áreas
        private void OnPointerDownMoveArea(PointerEventArgs e, string polyId)
        {
            _sel = _polys.First(p => p.poly_id == polyId); _selDoorId = null; _selWinId = null;
            if ((_vertexEditSelecting || _vertexEditActive) && string.Equals(_vertexEditPolyId, polyId, StringComparison.OrdinalIgnoreCase))
            {
                _saveMsg = "Modo edición de vértices activo.";
                StateHasChanged();
                return;
            }
            _beforeDragPoly = _sel.Clone();
            _beforeDragPoints = _sel.puntos.Select(p => p).ToList();
            _activeHandle = Handle.None;
            _dragVertexIndex = -1;
            _selectedVertexIndex = -1;
            ClearVertexEdit();
            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY); _dragStart = (wx, wy); StateHasChanged();
        }
        private void OnPointerDownResizeArea(PointerEventArgs e, Handle h)
        {
            if (_sel is null) return; _selDoorId = null; _selWinId = null; _beforeDragPoly = _sel.Clone(); _activeHandle = h;
            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY); _dragStart = (wx, wy); StateHasChanged();
        }

        private void OnPointerMove(PointerEventArgs e)
        {
            // PAN
            if (_panStart is not null && _dragStart is null && _dragDoor is null && _dragWin is null)
            {
                var (sx, sy) = _panStart.Value; var dxPx = e.OffsetX - sx; var dyPx = e.OffsetY - sy;
                if (!_panMoved && (Math.Abs(dxPx) > 3 || Math.Abs(dyPx) > 3)) _panMoved = true;
                _panStart = (e.OffsetX, e.OffsetY);
                var metersX = (decimal)(dxPx / (PxPerM() * _zoom)); var metersY = (decimal)(dyPx / (PxPerM() * _zoom));
                var vw = (decimal)((double)Wm / _zoom); var vh = (decimal)((double)Hm / _zoom);
                _panX = Clamp(0m, Wm - vw, _panX - metersX); _panY = Clamp(0m, Hm - vh, _panY - metersY); StateHasChanged();
            }

            // ÁREAS
            if (_dragStart is not null && _sel is not null && _beforeDragPoints is not null)
            {
                var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
                var dx = wx - _dragStart.Value.x;
                var dy = wy - _dragStart.Value.y;
                var snapEnabled = _snapToGrid && !e.ShiftKey;

                if (_dragVertexIndex >= 0 && _dragVertexIndex < _sel.puntos.Count)
                {
                    var curX = snapEnabled ? SnapValue(wx) : wx;
                    var curY = snapEnabled ? SnapValue(wy) : wy;
                    var deltaX = curX - _dragStart!.Value.x;
                    var deltaY = curY - _dragStart!.Value.y;

                    var selected = _vertexEditIndices.Count > 0 ? _vertexEditIndices : new HashSet<int> { _dragVertexIndex };
                    var selectedPoints = selected.Select(idx => _beforeDragPoints[idx]).ToList();
                    var (minX, minY, maxX, maxY) = BoundsOfPointList(selectedPoints);
                    deltaX = Clamp(0m - minX, Wm - maxX, deltaX);
                    deltaY = Clamp(0m - minY, Hm - maxY, deltaY);

                    var candidate = _beforeDragPoints.Select(p => p).ToList();
                    foreach (var idx in selected)
                    {
                        var pt = _beforeDragPoints[idx];
                        candidate[idx] = new Point(pt.X + deltaX, pt.Y + deltaY);
                    }

                    if (OverlapsAnyPoints(_sel.poly_id, candidate))
                    {
                        decimal lo = 0m, hi = 1m;
                        for (int it = 0; it < 10; it++)
                        {
                            var mid = (lo + hi) / 2m;
                            var test = _beforeDragPoints.Select(p => p).ToList();
                            foreach (var idx in selected)
                            {
                                var pt = _beforeDragPoints[idx];
                                test[idx] = new Point(pt.X + deltaX * mid, pt.Y + deltaY * mid);
                            }
                            if (OverlapsAnyPoints(_sel.poly_id, test)) hi = mid;
                            else lo = mid;
                        }

                        foreach (var idx in selected)
                        {
                            var pt = _beforeDragPoints[idx];
                            _sel.puntos[idx] = new Point(pt.X + deltaX * lo, pt.Y + deltaY * lo);
                        }
                    }
                    else
                    {
                        foreach (var idx in selected)
                        {
                            var pt = _beforeDragPoints[idx];
                            _sel.puntos[idx] = new Point(pt.X + deltaX, pt.Y + deltaY);
                        }
                    }
                    UpdateBoundsFromPolyPoints(_sel);
                }
                else
                {
                    var (minX, minY, maxX, maxY) = BoundsOfPointList(_beforeDragPoints);
                    var maxDx = Wm - maxX;
                    var minDx = 0m - minX;
                    var maxDy = Hm - maxY;
                    var minDy = 0m - minY;
                    dx = Clamp(minDx, maxDx, dx);
                    dy = Clamp(minDy, maxDy, dy);
                    if (snapEnabled)
                    {
                        dx = SnapValue(dx);
                        dy = SnapValue(dy);
                    }
                    _sel.puntos = _beforeDragPoints.Select(pt => new Point(pt.X + dx, pt.Y + dy)).ToList();
                    UpdateBoundsFromPolyPoints(_sel);
                }
                StateHasChanged();
            }

            // PUERTAS
            if (_dragDoor is not null)
            {
                var (x, y) = ScreenToWorld(e.OffsetX, e.OffsetY);
                if (_resizeDoor) ResizeFeatureAlongEdge(_dragDoor, x, y);
                else MoveFeatureToEdge(_dragDoor, x, y);
                StateHasChanged();
            }

            // VENTANAS
            if (_dragWin is not null)
            {
                var (x, y) = ScreenToWorld(e.OffsetX, e.OffsetY);
                if (_resizeWin) ResizeFeatureAlongEdge(_dragWin, x, y);
                else MoveFeatureToEdge(_dragWin, x, y);
                StateHasChanged();
            }
        }

        private void OnPointerUp(PointerEventArgs e)
        {
            if (_sel is not null && _beforeDragPoints is not null && (_dragVertexIndex >= 0 || _dragStart is not null))
            {
                if (OverlapsAny(_sel, _beforeDragPoints))
                {
                    _sel.puntos = _beforeDragPoints.Select(pt => pt).ToList();
                    UpdateBoundsFromPolyPoints(_sel);
                    _saveMsg = "El polígono no puede solaparse con otra área.";
                }
            }
            _dragStart = null; _beforeDragPoly = null; _activeHandle = Handle.None;
            _beforeDragPoints = null; _dragVertexIndex = -1;
            _dragDoor = null; _resizeDoor = false; _dragWin = null; _resizeWin = false;
            if (_panStart is not null && !_panMoved) DeselectAll(); _panStart = null;
        }

        // ======== Handlers pointerdown (puertas / ventanas)
        private void OnPointerDownMoveDoor(PointerEventArgs e, string id) { _dragDoor = _doors.First(d => d.door_id == id); _resizeDoor = false; _selDoorId = id; _selWinId = null; _sel = null; _showDoorWinPanels = true; ClearVertexEdit(); }
        private void OnPointerDownResizeDoor(PointerEventArgs e, string id) { _dragDoor = _doors.First(d => d.door_id == id); _resizeDoor = true; _selDoorId = id; _selWinId = null; _sel = null; _showDoorWinPanels = true; ClearVertexEdit(); }
        private void OnPointerDownMoveWin(PointerEventArgs e, string id) { _dragWin = _windows.First(w => w.win_id == id); _resizeWin = false; _selWinId = id; _selDoorId = null; _sel = null; _showDoorWinPanels = true; ClearVertexEdit(); }
        private void OnPointerDownResizeWin(PointerEventArgs e, string id) { _dragWin = _windows.First(w => w.win_id == id); _resizeWin = true; _selWinId = id; _selDoorId = null; _sel = null; _showDoorWinPanels = true; ClearVertexEdit(); }

        // ======== Pan/Zoom
        private void OnPointerDownBackground(PointerEventArgs e)
        {
            if (_isDrawing)
            {
                AddDraftPoint(e);
                return;
            }
            BeginPan(e);
        }
        private void BeginPan(PointerEventArgs e) { _panStart = (e.OffsetX, e.OffsetY); _panMoved = false; }
        private void OnWheel(WheelEventArgs e) { var delta = Math.Sign(e.DeltaY); var factor = (delta < 0) ? 1.1 : (1 / 1.1); _zoom = Math.Clamp(_zoom * factor, 0.3, 6.0); CenterView(); }
        private void ZoomIn() { _zoom = Math.Clamp(_zoom * 1.1, 0.3, 6.0); CenterView(); }
        private void ZoomOut() { _zoom = Math.Clamp(_zoom / 1.1, 0.3, 6.0); CenterView(); }

        // ======== Puertas/Ventanas pegadas a borde
        private static decimal ClampBetween(decimal a, decimal b, decimal v) => v < a ? a : (v > b ? b : v);
        private readonly Dictionary<string, (string orient, string areaId)> _lastEdgeForDoor = new();
        private readonly Dictionary<string, (string orient, string areaId)> _lastEdgeForWin = new();

        // ==================== NUEVAS VERSIONES (snap real a segmento) ====================

        private Poly? FindNearestPolyEdge(
         decimal px, decimal py,
         out string orientAxis, out decimal sx, out decimal sy,
         out decimal segA, out decimal segB,
         (string orient, string areaId)? prefer,
         IEnumerable<Poly>? candidates = null)
        {
            var polys = (candidates ?? VisiblePolys());
            Poly? best = null; orientAxis = "E"; sx = sy = segA = segB = 0;
            double bestCost = double.MaxValue;

            foreach (var a in polys)
            {
                var (L, T, R, B) = a.Bounds();
                // Top (horizontal)
                {
                    var cx = ClampBetween(L + CORNER_GUARD, R - CORNER_GUARD, px);
                    var cy = T;
                    var dist2 = (double)((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    var cost = dist2;
                    if (prefer is not null && prefer.Value.areaId == (a.area_id ?? "") && prefer.Value.orient == "E")
                        cost *= 0.4;
                    if (cost < bestCost)
                    {
                        bestCost = cost; best = a;
                        orientAxis = "E"; sx = cx; sy = cy; segA = L; segB = R;
                    }
                }
                // Bottom (horizontal)
                {
                    var cx = ClampBetween(L + CORNER_GUARD, R - CORNER_GUARD, px);
                    var cy = B;
                    var dist2 = (double)((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    var cost = dist2;
                    if (prefer is not null && prefer.Value.areaId == (a.area_id ?? "") && prefer.Value.orient == "E")
                        cost *= 0.4;
                    if (cost < bestCost)
                    {
                        bestCost = cost; best = a;
                        orientAxis = "E"; sx = cx; sy = cy; segA = L; segB = R;
                    }
                }
                // Left (vertical)
                {
                    var cx = L;
                    var cy = ClampBetween(T + CORNER_GUARD, B - CORNER_GUARD, py);
                    var dist2 = (double)((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    var cost = dist2;
                    if (prefer is not null && prefer.Value.areaId == (a.area_id ?? "") && prefer.Value.orient == "N")
                        cost *= 0.4;
                    if (cost < bestCost)
                    {
                        bestCost = cost; best = a;
                        orientAxis = "N"; sx = cx; sy = cy; segA = T; segB = B;
                    }
                }
                // Right (vertical)
                {
                    var cx = R;
                    var cy = ClampBetween(T + CORNER_GUARD, B - CORNER_GUARD, py);
                    var dist2 = (double)((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    var cost = dist2;
                    if (prefer is not null && prefer.Value.areaId == (a.area_id ?? "") && prefer.Value.orient == "N")
                        cost *= 0.4;
                    if (cost < bestCost)
                    {
                        bestCost = cost; best = a;
                        orientAxis = "N"; sx = cx; sy = cy; segA = T; segB = B;
                    }
                }
            }
            return best;
        }

        private static decimal EdgeMaxLen(Poly a, string orientAxis)
        {
            var (L, T, R, B) = a.Bounds();
            return orientAxis == "E"
                ? Math.Max(0, (R - L) - 2 * CORNER_GUARD)
                : Math.Max(0, (B - T) - 2 * CORNER_GUARD);
        }

        private Poly? FindSharedEdgeNeighbour(Poly a, string orientAxis, decimal sx, decimal sy, decimal len)
        {
            var (L, T, R, B) = a.Bounds();
            bool horizontal = (orientAxis == "E");

            decimal s1, s2;
            if (horizontal) { s1 = sx; s2 = sx + len; if (s2 < s1) (s1, s2) = (s2, s1); }
            else { s1 = sy; s2 = sy + len; if (s2 < s1) (s1, s2) = (s2, s1); }

            bool onTop = horizontal && Math.Abs((double)(sy - T)) <= (double)EDGE_EPS;
            bool onBottom = horizontal && Math.Abs((double)(sy - B)) <= (double)EDGE_EPS;
            bool onLeft = !horizontal && Math.Abs((double)(sx - L)) <= (double)EDGE_EPS;
            bool onRight = !horizontal && Math.Abs((double)(sx - R)) <= (double)EDGE_EPS;

            Poly? best = null; decimal bestOverlap = 0;

            foreach (var b in VisiblePolys()) // << SOLO visibles
            {
                if (b.poly_id == a.poly_id) continue;
                var (l, t, r, bb) = b.Bounds();

                if (horizontal)
                {
                    if (onTop)
                    {
                        if (Math.Abs((double)(bb - T)) > (double)EDGE_EPS) continue;
                        if (!(t < T)) continue;

                        var ov1 = Math.Max(s1, l);
                        var ov2 = Math.Min(s2, r);
                        var overlap = ov2 - ov1;
                        if (overlap > bestOverlap && overlap >= Math.Min(len, MIN_OVERLAP_FOR_NEIGHBOUR))
                        { bestOverlap = overlap; best = b; }
                    }
                    else if (onBottom)
                    {
                        if (Math.Abs((double)(t - B)) > (double)EDGE_EPS) continue;
                        if (!(bb > B)) continue;

                        var ov1 = Math.Max(s1, l);
                        var ov2 = Math.Min(s2, r);
                        var overlap = ov2 - ov1;
                        if (overlap > bestOverlap && overlap >= Math.Min(len, MIN_OVERLAP_FOR_NEIGHBOUR))
                        { bestOverlap = overlap; best = b; }
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    if (onLeft)
                    {
                        if (Math.Abs((double)(r - L)) > (double)EDGE_EPS) continue;
                        if (!(l < L)) continue;

                        var ov1 = Math.Max(s1, t);
                        var ov2 = Math.Min(s2, bb);
                        var overlap = ov2 - ov1;
                        if (overlap > bestOverlap && overlap >= Math.Min(len, MIN_OVERLAP_FOR_NEIGHBOUR))
                        { bestOverlap = overlap; best = b; }
                    }
                    else if (onRight)
                    {
                        if (Math.Abs((double)(l - R)) > (double)EDGE_EPS) continue;
                        if (!(r > R)) continue;

                        var ov1 = Math.Max(s1, t);
                        var ov2 = Math.Min(s2, bb);
                        var overlap = ov2 - ov1;
                        if (overlap > bestOverlap && overlap >= Math.Min(len, MIN_OVERLAP_FOR_NEIGHBOUR))
                        { bestOverlap = overlap; best = b; }
                    }
                    else
                    {
                        continue;
                    }
                }
            }
            return best;
        }

        private static bool SegmentsOverlap(decimal a1, decimal a2, decimal b1, decimal b2) { if (a2 < a1) (a1, a2) = (a2, a1); if (b2 < b1) (b1, b2) = (b2, b1); return a1 <= b2 && a2 >= b1; }
        private Poly? PolyByArea(string? areaId) => _polys.FirstOrDefault(p => string.Equals(p.area_id ?? "", areaId ?? "", StringComparison.OrdinalIgnoreCase));

        // ======= Puertas (mover/redimensionar sobre borde)
        private void MoveFeatureToEdge(Door d, decimal px, decimal py)
        {
            (string orient, string areaId)? prefer = null;
            if (_lastEdgeForDoor.TryGetValue(d.door_id, out var prev)) prefer = prev;

            var a = FindNearestPolyEdge(px, py, out var axis, out var sx, out var sy, out var segA, out var segB, prefer, VisiblePolys());
            if (a is null) return;

            var maxLen = EdgeMaxLen(a, axis);
            d.largo_m = Clamp(0.4m, maxLen, d.largo_m);

            if (axis == "E")
            {
                var minX = (segA + CORNER_GUARD);
                var maxX = (segB - CORNER_GUARD - d.largo_m);
                d.x_m = Clamp(minX, maxX, sx);
                d.y_m = sy;
                d.orientacion = "E";
            }
            else
            {
                var minY = (segA + CORNER_GUARD);
                var maxY = (segB - CORNER_GUARD - d.largo_m);
                d.y_m = Clamp(minY, maxY, sy);
                d.x_m = sx;
                d.orientacion = "N";
            }

            var other = FindSharedEdgeNeighbour(a, axis, d.x_m, d.y_m, d.largo_m);
            d.area_id_a = a.area_id;
            d.area_id_b = other?.area_id;

            _lastEdgeForDoor[d.door_id] = (axis, d.area_id_a ?? "");
        }

        private void ResizeFeatureAlongEdge(Door d, decimal px, decimal py)
        {
            var a = PolyByArea(d.area_id_a);
            if (a is null) return;

            var axis = (d.orientacion == "E" || d.orientacion == "W") ? "E" : "N";
            var maxLen = EdgeMaxLen(a, axis);

            if (axis == "E")
            {
                var newLen = px - d.x_m;
                d.largo_m = Clamp(0.4m, maxLen, Math.Abs(newLen));
                var (L, _, R, _) = a.Bounds();
                var minX = L + CORNER_GUARD;
                var maxX = R - CORNER_GUARD - d.largo_m;
                d.x_m = Clamp(minX, maxX, d.x_m);
            }
            else
            {
                var newLen = py - d.y_m;
                d.largo_m = Clamp(0.4m, maxLen, Math.Abs(newLen));
                var (_, T, _, B) = a.Bounds();
                var minY = T + CORNER_GUARD;
                var maxY = B - CORNER_GUARD - d.largo_m;
                d.y_m = Clamp(minY, maxY, d.y_m);
            }

            var other = FindSharedEdgeNeighbour(a, axis, d.x_m, d.y_m, d.largo_m);
            d.area_id_b = other?.area_id;
        }

        // ======= Ventanas (mover/redimensionar sobre borde)
        private void MoveFeatureToEdge(Win w, decimal px, decimal py)
        {
            (string orient, string areaId)? prefer = null;
            if (_lastEdgeForWin.TryGetValue(w.win_id, out var prev)) prefer = prev;

            var a = FindNearestPolyEdge(px, py, out var axis, out var sx, out var sy, out var segA, out var segB, prefer);
            if (a is null) return;

            var maxLen = EdgeMaxLen(a, axis);
            w.largo_m = Clamp(0.4m, maxLen, w.largo_m);

            if (axis == "E")
            {
                var minX = segA + CORNER_GUARD;
                var maxX = segB - CORNER_GUARD - w.largo_m;
                w.x_m = Clamp(minX, maxX, sx);
                w.y_m = sy;
                w.orientacion = "E";
            }
            else
            {
                var minY = segA + CORNER_GUARD;
                var maxY = segB - CORNER_GUARD - w.largo_m;
                w.y_m = Clamp(minY, maxY, sy);
                w.x_m = sx;
                w.orientacion = "N";
            }

            var other = FindSharedEdgeNeighbour(a, axis, w.x_m, w.y_m, w.largo_m);
            w.area_id_a = a.area_id;
            w.area_id_b = other?.area_id;

            _lastEdgeForWin[w.win_id] = (axis, w.area_id_a ?? "");
        }

        private void ResizeFeatureAlongEdge(Win w, decimal px, decimal py)
        {
            var a = PolyByArea(w.area_id_a);
            if (a is null) return;

            var axis = (w.orientacion == "E" || w.orientacion == "W") ? "E" : "N";
            var maxLen = EdgeMaxLen(a, axis);

            if (axis == "E")
            {
                var newLen = px - w.x_m;
                w.largo_m = Clamp(0.4m, maxLen, Math.Abs(newLen));
                var (L, _, R, _) = a.Bounds();
                w.x_m = Clamp(L + CORNER_GUARD, R - CORNER_GUARD - w.largo_m, w.x_m);
            }
            else
            {
                var newLen = py - w.y_m;
                w.largo_m = Clamp(0.4m, maxLen, Math.Abs(newLen));
                var (_, T, _, B) = a.Bounds();
                w.y_m = Clamp(T + CORNER_GUARD, B - CORNER_GUARD - w.largo_m, w.y_m);
            }

            var other = FindSharedEdgeNeighbour(a, axis, w.x_m, w.y_m, w.largo_m);
            w.area_id_b = other?.area_id;
        }

        // ===== guardar / nuevo / eliminar
        private async Task Guardar()
        {
            try
            {
                if (_isDrawing)
                {
                    _saveMsg = "Cierra el polígono antes de guardar.";
                    return;
                }
                if (_polys.Any(p => string.IsNullOrWhiteSpace(p.area_id)))
                {
                    _saveMsg = "Todos los polígonos deben estar asignados a un área.";
                    return;
                }
                if (_polys.Any(p => !IsAreaInCurrentCanvas(p.area_id)))
                {
                    _saveMsg = "Todos los polígonos deben pertenecer al canvas actual.";
                    return;
                }

                _saving = true; _saveMsg = "Guardando…"; StateHasChanged();

                // ---- Polígonos
                Pg.UseSheet("poligonos");
                foreach (var p in _polys)
                {
                    UpdateBoundsFromPolyPoints(p);
                    var ok = await Pg.UpdateByIdAsync("poly_id", p.poly_id, new Dictionary<string, object>
                    {
                        ["canvas_id"] = p.canvas_id,
                        ["area_id"] = p.area_id ?? (object)DBNull.Value,
                        ["x_m"] = p.x_m,
                        ["y_m"] = p.y_m,
                        ["ancho_m"] = p.ancho_m,
                        ["alto_m"] = p.alto_m,
                        ["z_order"] = p.z_order,
                        ["etiqueta"] = p.etiqueta ?? (object)DBNull.Value,
                        ["color_hex"] = p.color_hex ?? (object)DBNull.Value
                    });
                    if (!ok)
                    {
                        await Pg.CreateAsync(new Dictionary<string, object>
                        {
                            ["poly_id"] = p.poly_id,
                            ["canvas_id"] = p.canvas_id,
                            ["area_id"] = p.area_id,
                            ["x_m"] = p.x_m,
                            ["y_m"] = p.y_m,
                            ["ancho_m"] = p.ancho_m,
                            ["alto_m"] = p.alto_m,
                            ["z_order"] = p.z_order,
                            ["etiqueta"] = p.etiqueta,
                            ["color_hex"] = p.color_hex
                        });
                    }

                    await ReplacePolyPointsAsync(p);
                }

                // ---- Puertas
                Pg.UseSheet("puertas");
                foreach (var d in _doors)
                {
                    decimal x2 = d.x_m, y2 = d.y_m;
                    switch (d.orientacion)
                    {
                        case "E": x2 = d.x_m + d.largo_m; break;
                        case "W": x2 = d.x_m - d.largo_m; break;
                        case "S": y2 = d.y_m + d.largo_m; break;
                        case "N": y2 = d.y_m - d.largo_m; break;
                    }
                    d.area_id_a = SanitizeFk(d.area_id_a, _areasLookup);
                    d.area_id_b = SanitizeFk(d.area_id_b, _areasLookup);

                    var okd = await Pg.UpdateByIdAsync("puerta_id", d.door_id, new Dictionary<string, object>
                    {
                        ["canvas_id"] = d.canvas_id,
                        ["area_a"] = DbNullIfEmpty(d.area_id_a),
                        ["area_b"] = DbNullIfEmpty(d.area_id_b),
                        ["x1_m"] = d.x_m,
                        ["y1_m"] = d.y_m,
                        ["x2_m"] = x2,
                        ["y2_m"] = y2,
                        ["grosor_m"] = 0.10m,
                        ["color_hex"] = "#13A076"
                    });
                    if (!okd)
                    {
                        await Pg.CreateAsync(new Dictionary<string, object>
                        {
                            ["puerta_id"] = d.door_id,
                            ["canvas_id"] = d.canvas_id,
                            ["area_a"] = DbNullIfEmpty(d.area_id_a),
                            ["area_b"] = DbNullIfEmpty(d.area_id_b),
                            ["x1_m"] = d.x_m,
                            ["y1_m"] = d.y_m,
                            ["x2_m"] = x2,
                            ["y2_m"] = y2,
                            ["grosor_m"] = 0.10m,
                            ["color_hex"] = "#13A076"
                        });
                    }
                }

                // ---- Ventanas
                Pg.UseSheet("ventanas");
                foreach (var w in _windows)
                {
                    decimal x2 = w.x_m, y2 = w.y_m;
                    switch (w.orientacion)
                    {
                        case "E": x2 = w.x_m + w.largo_m; break;
                        case "W": x2 = w.x_m - w.largo_m; break;
                        case "S": y2 = w.y_m + w.largo_m; break;
                        case "N": y2 = w.y_m - w.largo_m; break;
                    }

                    w.area_id_a = SanitizeFk(w.area_id_a, _areasLookup);
                    w.area_id_b = SanitizeFk(w.area_id_b, _areasLookup);

                    var okw = await Pg.UpdateByIdAsync("ventana_id", w.win_id, new Dictionary<string, object>
                    {
                        ["canvas_id"] = w.canvas_id,
                        ["area_a"] = DbNullIfEmpty(w.area_id_a),
                        ["area_b"] = DbNullIfEmpty(w.area_id_b),
                        ["x1_m"] = w.x_m,
                        ["y1_m"] = w.y_m,
                        ["x2_m"] = x2,
                        ["y2_m"] = y2,
                        ["grosor_m"] = 0.05m,
                        ["color_hex"] = "#66CCFF"
                    });
                    if (!okw)
                    {
                        await Pg.CreateAsync(new Dictionary<string, object>
                        {
                            ["ventana_id"] = w.win_id,
                            ["canvas_id"] = w.canvas_id,
                            ["area_a"] = DbNullIfEmpty(w.area_id_a),
                            ["area_b"] = DbNullIfEmpty(w.area_id_b),
                            ["x1_m"] = w.x_m,
                            ["y1_m"] = w.y_m,
                            ["x2_m"] = x2,
                            ["y2_m"] = y2,
                            ["grosor_m"] = 0.05m,
                            ["color_hex"] = "#66CCFF"
                        });
                    }
                }

                // ---- Actualizar tabla AREAS
                Pg.UseSheet("areas");
                foreach (var kv in _areasLookup)
                {
                    var areaId = kv.Key;

                    if (!_areasMeta.TryGetValue(areaId, out var meta))
                    {
                        meta = new AreaMeta { area_id = areaId };
                        _areasMeta[areaId] = meta;
                    }

                    var areaTotal = CalcAreaTotalM2(areaId);

                    var plantaId = meta.planta_id;
                    if (plantaId != null && !_plantasLookup.ContainsKey(plantaId)) plantaId = null;

                    var anot = string.IsNullOrWhiteSpace(meta.anotaciones) ? "SIN MODIFICACIONES" : meta.anotaciones;
                    meta.canvas_id ??= _canvas?.canvas_id;
                    meta.laboratorio_id ??= _canvas?.laboratorio_id;

                    if (meta.laboratorio_id is null)
                    {
                        _saveMsg = "No se pudo resolver laboratorio_id del canvas.";
                        return;
                    }

                    var okArea = await Pg.UpdateByIdAsync("area_id", areaId, new Dictionary<string, object>
                    {
                        ["planta_id"] = plantaId ?? (object)DBNull.Value,
                        ["canvas_id"] = meta.canvas_id ?? (object)DBNull.Value,
                        ["laboratorio_id"] = meta.laboratorio_id ?? (object)DBNull.Value,
                        ["altura_m"] = meta.altura_m ?? (object)DBNull.Value,
                        ["area_total_m2"] = areaTotal,
                        ["anotaciones_del_area"] = anot
                    });

                    if (!okArea)
                    {
                        await Pg.CreateAsync(new Dictionary<string, object>
                        {
                            ["area_id"] = areaId,
                            ["nombre_areas"] = _areasLookup[areaId],
                            ["planta_id"] = plantaId ?? (object)DBNull.Value,
                            ["canvas_id"] = meta.canvas_id ?? (object)DBNull.Value,
                            ["laboratorio_id"] = meta.laboratorio_id ?? (object)DBNull.Value,
                            ["altura_m"] = meta.altura_m ?? (object)DBNull.Value,
                            ["area_total_m2"] = areaTotal,
                            ["anotaciones_del_area"] = anot
                        });
                    }
                }

                _saveMsg = "Guardado ✔";
            }
            catch (Exception ex)
            {
                _saveMsg = "Error al guardar (ver consola).";
                Console.Error.WriteLine($"[Guardar] {ex}");
            }
            finally { _saving = false; StateHasChanged(); }
        }

        private async Task ReplacePolyPointsAsync(Poly p)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await DeletePolyPointsAsync(p.poly_id, conn);

            const string insertSql = @"
                INSERT INTO poligonos_puntos (poly_id, orden, x_m, y_m)
                VALUES (@poly_id, @orden, @x_m, @y_m)";
            var orden = 1;
            foreach (var punto in p.puntos)
            {
                await using var insCmd = new NpgsqlCommand(insertSql, conn);
                insCmd.Parameters.AddWithValue("poly_id", p.poly_id);
                insCmd.Parameters.AddWithValue("orden", orden++);
                insCmd.Parameters.AddWithValue("x_m", punto.X);
                insCmd.Parameters.AddWithValue("y_m", punto.Y);
                await insCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task DeletePolyPointsAsync(string polyId)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await DeletePolyPointsAsync(polyId, conn);
        }

        private static async Task DeletePolyPointsAsync(string polyId, NpgsqlConnection conn)
        {
            await using var delCmd = new NpgsqlCommand("DELETE FROM poligonos_puntos WHERE poly_id = @id", conn);
            delCmd.Parameters.AddWithValue("id", polyId);
            await delCmd.ExecuteNonQueryAsync();
        }

        // Planta/piso actual
        private string? _currentPlantaId;

        // Planta de un polígono (según su area_id)
        private string? PlantaOf(Poly p)
        {
            if (p.area_id is null) return null;
            return _areasMeta.TryGetValue(p.area_id, out var meta) ? meta.planta_id : null;
        }

        // ¿El polígono pertenece a la planta actual?
        private bool IsVisible(Poly p)
            => string.Equals(PlantaOf(p) ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
               && IsAreaInCurrentCanvas(p.area_id);

        private bool IsDoorVisible(Door d)
        {
            if (string.IsNullOrWhiteSpace(d.area_id_a)) return false;
            return _areasMeta.TryGetValue(d.area_id_a!, out var meta)
                && string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
                && IsCanvasMatch(meta.canvas_id);
        }

        private bool IsWinVisible(Win w)
        {
            if (string.IsNullOrWhiteSpace(w.area_id_a)) return false;
            return _areasMeta.TryGetValue(w.area_id_a!, out var meta)
                && string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
                && IsCanvasMatch(meta.canvas_id);
        }

        private IEnumerable<KeyValuePair<string, string>> AreasOfCurrentPlanta()
            => _areasLookup.Where(kv =>
                _areasMeta.TryGetValue(kv.Key, out var meta)
                && string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
                && IsCanvasMatch(meta.canvas_id));

        private void OnChangePlanta(string? plantaId)
        {
            _currentPlantaId = string.IsNullOrWhiteSpace(plantaId) ? null : plantaId;

            if (_sel is null || !IsVisible(_sel))
                _sel = _polys.FirstOrDefault(IsVisible);

            _drawAreaId = DefaultAreaIdForCurrentPlanta();
            _newAreaPlantaId = _currentPlantaId;
            StateHasChanged();
        }

        private void OnSelectedAreaChange(string? areaId)
        {
            if (_sel is null)
            {
                return;
            }

            var nextId = string.IsNullOrWhiteSpace(areaId) ? null : areaId;
            if (!IsAreaInCurrentPlanta(nextId))
            {
                _saveMsg = "El área seleccionada no pertenece a la planta actual.";
                return;
            }

            _sel.area_id = nextId;
            StateHasChanged();
        }

        private void OnDrawAreaChange(string? areaId)
        {
            var nextId = string.IsNullOrWhiteSpace(areaId) ? null : areaId;
            if (!IsAreaInCurrentPlanta(nextId))
            {
                _drawMsg = "El área seleccionada no pertenece a la planta actual.";
                return;
            }

            _drawAreaId = nextId;
        }

        private async Task OnChangeCanvas(string? canvasId)
        {
            _currentCanvasId = string.IsNullOrWhiteSpace(canvasId) ? null : canvasId;
            await ReloadCanvasDataAsync();
        }

        private void StartDrawing()
        {
            _draftPoints.Clear();
            _drawMsg = null;
            _isDrawing = true;
            _selectedVertexIndex = -1;
            _dragVertexIndex = -1;
            ClearVertexEdit();
        }

        private void CancelDrawing()
        {
            _draftPoints.Clear();
            _drawMsg = null;
            _isDrawing = false;
        }

        private void AddDraftPoint(PointerEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_drawAreaId))
            {
                _drawMsg = "Selecciona un área antes de dibujar.";
                return;
            }
            if (!IsAreaInCurrentPlanta(_drawAreaId))
            {
                _drawMsg = "El área seleccionada no pertenece a la planta actual.";
                return;
            }

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            var snapEnabled = _snapToGrid && !e.ShiftKey;
            var x = snapEnabled ? SnapValue(wx) : wx;
            var y = snapEnabled ? SnapValue(wy) : wy;
            x = Clamp(0m, Wm, x);
            y = Clamp(0m, Hm, y);
            if (_draftPoints.Count > 0)
            {
                var last = _draftPoints[^1];
                if (_drawAxisXOnly) y = last.Y;
                if (_drawAxisYOnly) x = last.X;
            }
            var point = new Point(x, y);

            if (_draftPoints.Count >= 3 && DistanceBetweenPoints(_draftPoints[0], point) <= DraftCloseRadius())
            {
                FinalizeDraftPolygon();
                return;
            }

            _draftPoints.Add(point);
            _drawMsg = "Haz clic en el primer punto para cerrar.";
        }

        private decimal DraftCloseRadius()
            => Math.Max(0.35m, _gridStep * 2);

        private void FinalizeDraftPolygon()
        {
            if (_draftPoints.Count < 3)
            {
                _drawMsg = "Se necesitan al menos 3 puntos para cerrar el polígono.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_drawAreaId))
            {
                _drawMsg = "Selecciona un área antes de cerrar el polígono.";
                return;
            }
            if (!IsAreaInCurrentPlanta(_drawAreaId))
            {
                _drawMsg = "El área seleccionada no pertenece a la planta actual.";
                return;
            }

            var poly = new Poly
            {
                poly_id = $"poly_{Guid.NewGuid():N}".Substring(0, 11),
                canvas_id = _canvas!.canvas_id,
                area_id = _drawAreaId,
                z_order = (_polys.Count == 0) ? 0 : _polys.Max(pp => pp.z_order) + 1,
                color_hex = "#E6E6E6",
                puntos = _draftPoints.ToList()
            };
            UpdateBoundsFromPolyPoints(poly);

            if (OverlapsAny(poly, null))
            {
                _drawMsg = "El polígono se superpone con otra área en esta planta.";
                return;
            }

            _polys.Add(poly);
            _sel = poly;
            _draftPoints.Clear();
            _drawMsg = "Polígono creado.";
            _isDrawing = false;
        }

        private void OnPointerDownVertex(PointerEventArgs e, string polyId, int index)
        {
            _sel = _polys.First(p => p.poly_id == polyId);
            _selDoorId = null;
            _selWinId = null;
            _selectedVertexIndex = index;
            if (_vertexEditSelecting) return;
            if (!_vertexEditActive || _vertexEditPolyId != polyId || !_vertexEditIndices.Contains(index))
            {
                _dragVertexIndex = -1;
                _dragStart = null;
                _beforeDragPoints = null;
                return;
            }

            _beforeDragPoints = _sel.puntos.Select(p => p).ToList();
            _dragVertexIndex = index;
            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);
        }

        private void OnVertexClick(string polyId, int index)
        {
            if (!_vertexEditSelecting) return;
            if (_vertexEditPolyId is null)
            {
                _vertexEditPolyId = polyId;
            }
            else if (_vertexEditPolyId != polyId)
            {
                _saveMsg = "Selecciona vértices solo en el polígono activo.";
                StateHasChanged();
                return;
            }

            if (_vertexEditIndices.Contains(index))
            {
                _vertexEditIndices.Remove(index);
            }
            else
            {
                _vertexEditIndices.Add(index);
            }
            _saveMsg = "Selecciona vértices para editar y pulsa OK.";
            StateHasChanged();
        }

        private void Nuevo()
        {
            var id = $"poly_{Guid.NewGuid():N}".Substring(0, 11);
            var aid = DefaultAreaIdForCurrentPlanta();

            var p = new Poly
            {
                poly_id = id,
                canvas_id = _canvas!.canvas_id,
                area_id = aid,
                x_m = _snapToGrid ? SnapValue(0.5m) : 0.5m,
                y_m = _snapToGrid ? SnapValue(0.5m) : 0.5m,
                ancho_m = 2.0m,
                alto_m = 2.0m,
                z_order = (_polys.Count == 0) ? 0 : _polys.Max(pp => pp.z_order) + 1,
                color_hex = "#E6E6E6"
            };
            _polys.Add(p); _sel = p; NormalizeSelected();
        }

        private async Task Eliminar()
        {
            if (_sel is null) return;
            Pg.UseSheet("poligonos");
            await Pg.DeleteByIdAsync("poly_id", _sel.poly_id);
            await DeletePolyPointsAsync(_sel.poly_id);
            _polys.RemoveAll(x => x.poly_id == _sel.poly_id);
            _sel = _polys.FirstOrDefault();
            _saveMsg = "Eliminado";
        }

        private void IniciarEdicionVertice()
        {
            if (_sel is null) return;
            _vertexEditIndices.Clear();
            _vertexEditPolyId = _sel.poly_id;
            _vertexEditSelecting = true;
            _vertexEditActive = false;
            _saveMsg = "Seleccione vértices para editar.";
            StateHasChanged();
        }

        private void ConfirmarEdicionVertices()
        {
            if (!HasVertexEditMode) return;
            _vertexEditSelecting = false;
            _vertexEditActive = true;
            _saveMsg = "Edición de vértices activa.";
            StateHasChanged();
        }

        private void GuardarEdicionVertice()
        {
            if (!_vertexEditActive) return;
            ClearVertexEdit();
            _saveMsg = "Vértices guardados.";
            StateHasChanged();
        }

        private async Task EliminarPuerta()
        {
            var doorId = SelDoor?.door_id;
            if (string.IsNullOrWhiteSpace(doorId)) return;
            Pg.UseSheet("puertas");
            await Pg.DeleteByIdAsync("puerta_id", doorId);
            _doors.RemoveAll(x => x.door_id == doorId);
            _selDoorId = null;
            _saveMsg = "Puerta eliminada";
            StateHasChanged();
        }

        private async Task EliminarVentana()
        {
            if (SelWin is null) return;
            Pg.UseSheet("ventanas");
            await Pg.DeleteByIdAsync("ventana_id", SelWin.win_id);
            _windows.RemoveAll(x => x.win_id == SelWin.win_id);
            _selWinId = null;
            _saveMsg = "Ventana eliminada";
            StateHasChanged();
        }

        // ======= crear puerta/ventana
        private void NuevaPuerta()
        {
            var aid = DefaultAreaIdForCurrentPlanta();
            if (aid is null) return;

            var a = PolyByArea(aid);
            if (a is null) return;

            var sx = a.x_m + a.ancho_m / 2; var sy = a.y_m;
            var d = new Door
            {
                door_id = $"door_{Guid.NewGuid():N}".Substring(0, 12),
                canvas_id = _canvas!.canvas_id,
                area_id_a = aid,
                x_m = sx,
                y_m = sy,
                orientacion = "E",
                largo_m = Clamp(0.4m, a.ancho_m, 1.0m)
            };
            _doors.Add(d); _lastEdgeForDoor[d.door_id] = (d.orientacion, aid);
            _selDoorId = d.door_id; _selWinId = null; _sel = null; _showDoorWinPanels = true;
        }

        private void NuevaVentana()
        {
            var aid = DefaultAreaIdForCurrentPlanta();
            if (aid is null) return;

            var a = PolyByArea(aid);
            if (a is null) return;

            var sx = a.x_m + a.ancho_m / 2; var sy = a.y_m;

            var w = new Win
            {
                win_id = $"win_{Guid.NewGuid():N}".Substring(0, 12),
                canvas_id = _canvas!.canvas_id,
                area_id_a = aid,
                area_id_b = null,
                x_m = sx,
                y_m = sy,
                orientacion = "E",
                largo_m = Clamp(0.4m, a.ancho_m, 1.0m)
            };
            _windows.Add(w); _lastEdgeForWin[w.win_id] = (w.orientacion, aid);
            _selWinId = w.win_id; _selDoorId = null; _sel = null; _showDoorWinPanels = true;
        }

        // ===== utils
        private double PxPerM() { const double nominalSvgPx = 1000.0; return nominalSvgPx / (double)Wm; }
        private decimal PxToWorld(double px) => (decimal)(px / (PxPerM() * _zoom));
        private decimal VertexR() => PxToWorld(5);
        private decimal VertexStroke() => PxToWorld(1.5);
        private decimal DraftR(bool first) => PxToWorld(first ? 7 : 5);
        private static decimal ParseFlexible(object? v)
        {
            var s = (v?.ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0m;
            s = s.Replace(" ", "");
            if (s.Count(ch => ch == ',' || ch == '.') > 1) s = s.Replace(".", "").Replace(",", ".");
            else s = s.Replace(",", ".");
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }
        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
        private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        private static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
        private static int? IntOrNull(string? s) => int.TryParse(s, out var n) ? n : null;
        private void DeselectAll() { _sel = null; _selDoorId = null; _selWinId = null; _hoverId = null; _selectedVertexIndex = -1; ClearVertexEdit(); StateHasChanged(); }

        private void ClearVertexEdit()
        {
            _vertexEditPolyId = null;
            _vertexEditIndices.Clear();
            _vertexEditSelecting = false;
            _vertexEditActive = false;
            _dragVertexIndex = -1;
            _dragStart = null;
            _beforeDragPoints = null;
        }

        // ======================= MODO JUNTAR VÉRTICES =======================
        private bool _joinMode = false;
        private (string polyId, Handle h)? _pickA = null;
        private (string polyId, Handle h)? _pickB = null;
        private string _joinMsg = "";

        private void ToggleJoinMode() { _joinMode = !_joinMode; _joinMsg = _joinMode ? "Orden: A (se mueve) → B (objetivo)" : ""; if (!_joinMode) { _pickA = null; _pickB = null; } StateHasChanged(); }
        private void CancelJoin() { _joinMode = false; _pickA = null; _pickB = null; _joinMsg = ""; }

        private void PickVertex(string polyId, Handle h)
        {
            if (!_joinMode) return;
            if (_pickA is null) { _pickA = (polyId, h); _joinMsg = "Vértice A elegido (se mueve). Ahora elige B (objetivo)."; }
            else if (_pickB is null) { _pickB = (polyId, h); TryJoinAtoB(); }
            StateHasChanged();
        }

        private (decimal x, decimal y) VertexWorld(Poly p, Handle h) => h switch
        {
            Handle.NW => (p.x_m, p.y_m),
            Handle.NE => (p.x_m + p.ancho_m, p.y_m),
            Handle.SW => (p.x_m, p.y_m + p.alto_m),
            Handle.SE => (p.x_m + p.ancho_m, p.y_m + p.alto_m),
            _ => (p.x_m, p.y_m)
        };
        private static bool Nearly(decimal a, decimal b, double eps = 1e-6) => Math.Abs((double)(a - b)) < eps;

        private void TryJoinAtoB()
        {
            if (_pickA is null || _pickB is null) return;
            var pA = _polys.FirstOrDefault(p => p.poly_id == _pickA.Value.polyId);
            var pB = _polys.FirstOrDefault(p => p.poly_id == _pickB.Value.polyId);
            if (pA is null || pB is null) { _joinMsg = "No se pudo encontrar polígonos."; EndJoin(); return; }

            var (ax0, ay0) = VertexWorld(pA, _pickA.Value.h);
            var (bx, by) = VertexWorld(pB, _pickB.Value.h);

            if (_joinAxisXOnly ^ _joinAxisYOnly)
            {
                if (_joinAxisXOnly)
                {
                    _ = StretchCornerToX(pA, _pickA.Value.h, bx);
                    NormalizeSelected(noOverlap: true, primaryAxis: 'x');
                    var (ax1, _) = VertexWorld(pA, _pickA.Value.h);
                    if (Nearly(ax1, bx)) { _joinMsg = "Vértices igualados en X ✔ (ajustando ancho)"; EndJoin(); return; }
                }
                else
                {
                    _ = StretchCornerToY(pA, _pickA.Value.h, by);
                    NormalizeSelected(noOverlap: true, primaryAxis: 'y');
                    var (_, ay1) = VertexWorld(pA, _pickA.Value.h);
                    if (Nearly(ay1, by)) { _joinMsg = "Vértices igualados en Y ✔ (ajustando alto)"; EndJoin(); return; }
                }
            }

            var tx = _joinAxisYOnly ? ax0 : bx;
            var ty = _joinAxisXOnly ? ay0 : by;
            var dx = tx - ax0; var dy = ty - ay0;
            var baseX = pA.x_m; var baseY = pA.y_m;

            var moved = MoveWithCollisions(pA, baseX, baseY, dx, dy);
            bool reached = Nearly(moved.x, baseX + dx) && Nearly(moved.y, baseY + dy);
            if (reached)
            {
                pA.x_m = moved.x; pA.y_m = moved.y; NormalizeSelected();
                _joinMsg = "Vértices unidos ✔ (moviendo A → B)"; EndJoin(); return;
            }

            var snapshot = pA.Clone();
            var ok2 = PlaceFixedVertexExactAndShrink(pA, _pickA.Value.h, tx, ty);
            if (ok2) { NormalizeSelected(); _joinMsg = "Vértices unidos ✔ (A ajustado para alinear)"; }
            else { pA.x_m = snapshot.x_m; pA.y_m = snapshot.y_m; pA.ancho_m = snapshot.ancho_m; pA.alto_m = snapshot.alto_m; _joinMsg = "No se pueden juntar: habría colisión o salida del lienzo."; }
            EndJoin();
        }

        private bool StretchCornerToX(Poly p, Handle fixedCorner, decimal tx)
        {
            var baseLeft = p.x_m; var baseRight = p.x_m + p.ancho_m;
            switch (fixedCorner)
            {
                case Handle.NW:
                case Handle.SW:
                    {
                        var desiredW = baseRight - tx; var newW = ClampLeft(p, p.y_m, baseRight, desiredW);
                        if (newW < 0.1m) return false; p.x_m = baseRight - newW; p.ancho_m = newW; return true;
                    }
                case Handle.NE:
                case Handle.SE:
                    {
                        var desiredW = tx - baseLeft; var newW = ClampRight(p, baseLeft, p.y_m, desiredW);
                        if (newW < 0.1m) return false; p.x_m = baseLeft; p.ancho_m = newW; return true;
                    }
                default: return false;
            }
        }
        private bool StretchCornerToY(Poly p, Handle fixedCorner, decimal ty)
        {
            var baseTop = p.y_m; var baseBottom = p.y_m + p.alto_m;
            switch (fixedCorner)
            {
                case Handle.NW:
                case Handle.NE:
                    {
                        var desiredH = baseBottom - ty; var newH = ClampTop(p, p.x_m, baseBottom, desiredH);
                        if (newH < 0.1m) return false; p.y_m = baseBottom - newH; p.alto_m = newH; return true;
                    }
                case Handle.SW:
                case Handle.SE:
                    {
                        var desiredH = ty - baseTop; var newH = ClampBottom(p, p.x_m, baseTop, desiredH);
                        if (newH < 0.1m) return false; p.y_m = baseTop; p.alto_m = newH; return true;
                    }
                default: return false;
            }
        }

        private bool PlaceFixedVertexExactAndShrink(Poly p, Handle fixedCorner, decimal tx, decimal ty)
        {
            switch (fixedCorner)
            {
                case Handle.NW: p.x_m = tx; p.y_m = ty; break;
                case Handle.NE: p.y_m = ty; p.x_m = tx - p.ancho_m; break;
                case Handle.SW: p.x_m = tx; p.y_m = ty - p.alto_m; break;
                case Handle.SE: p.x_m = tx - p.ancho_m; p.y_m = ty - p.alto_m; break;
            }
            if (p.x_m < 0m) { var overflow = -p.x_m; if (fixedCorner is Handle.NE or Handle.SE) { p.x_m = 0; p.ancho_m -= overflow; } else p.x_m = 0; }
            if (p.y_m < 0m) { var overflow = -p.y_m; if (fixedCorner is Handle.SW or Handle.SE) { p.y_m = 0; p.alto_m -= overflow; } else p.y_m = 0; }
            var W = Wm; var H = Hm;
            if (p.x_m + p.ancho_m > W) { var overflow = p.x_m + p.ancho_m - W; if (fixedCorner is Handle.NW or Handle.SW) p.ancho_m -= overflow; else p.x_m -= overflow; }
            if (p.y_m + p.alto_m > H) { var overflow = p.y_m + p.alto_m - H; if (fixedCorner is Handle.NW or Handle.NE) p.alto_m -= overflow; else p.y_m -= overflow; }
            if (p.ancho_m < 0.1m || p.alto_m < 0.1m) return false;

            const int MAX_ITERS = 24;
            for (int k = 0; k < MAX_ITERS; k++)
            {
                var overlapWith = VisiblePolys().FirstOrDefault(o => o.poly_id != p.poly_id && RectOverlap(p, o)); // << visibles
                if (overlapWith is null) return true;
                var (L1, T1, R1, B1) = p.Bounds(); var (L2, T2, R2, B2) = overlapWith.Bounds();
                var oL = Math.Max(L1, L2); var oT = Math.Max(T1, T2); var oR = Math.Min(R1, R2); var oB = Math.Min(B1, B2);
                if (oL >= oR || oT >= oB) continue;
                var oW = oR - oL; var oH = oB - oT;
                if (oW >= oH)
                {
                    if (fixedCorner is Handle.NW or Handle.NE) { p.alto_m = Math.Max(0.1m, p.alto_m - oH - EPS); }
                    else { var cut = oH + EPS; p.y_m += cut; p.alto_m = Math.Max(0.1m, p.alto_m - cut); }
                }
                else
                {
                    if (fixedCorner is Handle.NW or Handle.SW) { p.ancho_m = Math.Max(0.1m, p.ancho_m - oW - EPS); }
                    else { var cut = oW + EPS; p.x_m += cut; p.ancho_m = Math.Max(0.1m, p.ancho_m - cut); }
                }
                if (p.ancho_m < 0.1m || p.alto_m < 0.1m) return false;
            }
            return false;
        }

        private void EndJoin() { _joinMode = false; _pickA = null; _pickB = null; }

        // ======================= ALINEAR TODO =======================
        private async Task AlinearTodo()
        {
            var visibles = VisiblePolys().ToList();
            if (visibles.Count < 2) return;

            const decimal ALIGN_PROX = 0.004m; // ≈4 mm
            const decimal ROUND_MM = 0.001m;   // redondeo al milímetro

            var changed = new HashSet<string>();
            var iterations = 0;
            bool didAny;

            do
            {
                didAny = false;
                iterations++;
                if (iterations > 6) break;

                for (int i = 0; i < visibles.Count; i++)
                {
                    for (int j = i + 1; j < visibles.Count; j++)
                    {
                        var a = visibles[i];
                        var b = visibles[j];

                        var (AL, AT, AR, AB) = a.Bounds();
                        var (BL, BT, BR, BB) = b.Bounds();

                        if (RangeOverlap(AT, AB, BT, BB) && Math.Abs(AR - BL) <= ALIGN_PROX)
                        {
                            var target = RoundTo((AR + BL) / 2m, ROUND_MM);
                            var dxA = target - AR;
                            var dxB = target - BL;

                            if (CanTranslateX(a, dxA) && CanTranslateX(b, dxB))
                            { a.x_m += dxA; b.x_m += dxB; didAny = true; changed.Add(a.poly_id); changed.Add(b.poly_id); }
                        }

                        if (RangeOverlap(AT, AB, BT, BB) && Math.Abs(AL - BR) <= ALIGN_PROX)
                        {
                            var target = RoundTo((AL + BR) / 2m, ROUND_MM);
                            var dxA = target - AL;
                            var dxB = target - BR;

                            if (CanTranslateX(a, dxA) && CanTranslateX(b, dxB))
                            { a.x_m += dxA; b.x_m += dxB; didAny = true; changed.Add(a.poly_id); changed.Add(b.poly_id); }
                        }

                        if (RangeOverlap(AL, AR, BL, BR) && Math.Abs(AB - BT) <= ALIGN_PROX)
                        {
                            var target = RoundTo((AB + BT) / 2m, ROUND_MM);
                            var dyA = target - AB;
                            var dyB = target - BT;

                            if (CanTranslateY(a, dyA) && CanTranslateY(b, dyB))
                            { a.y_m += dyA; b.y_m += dyB; didAny = true; changed.Add(a.poly_id); changed.Add(b.poly_id); }
                        }

                        if (RangeOverlap(AL, AR, BL, BR) && Math.Abs(AT - BB) <= ALIGN_PROX)
                        {
                            var target = RoundTo((AT + BB) / 2m, ROUND_MM);
                            var dyA = target - AT;
                            var dyB = target - BB;

                            if (CanTranslateY(a, dyA) && CanTranslateY(b, dyB))
                            { a.y_m += dyA; b.y_m += dyB; didAny = true; changed.Add(a.poly_id); changed.Add(b.poly_id); }
                        }
                    }
                }

                foreach (var id in changed.ToArray())
                {
                    var p = visibles.First(pp => pp.poly_id == id);
                    p.x_m = Clamp(0m, Wm - p.ancho_m, RoundTo(p.x_m, ROUND_MM));
                    p.y_m = Clamp(0m, Hm - p.alto_m, RoundTo(p.y_m, ROUND_MM));
                }

                foreach (var id in changed.ToArray())
                {
                    _sel = visibles.First(pp => pp.poly_id == id);
                    NormalizeSelected(noOverlap: true);
                }

            } while (didAny);

            if (changed.Count > 0)
            {
                try
                {
                    _saving = true; _saveMsg = "Alineando…"; StateHasChanged();
                    Pg.UseSheet("poligonos");

                    foreach (var id in changed)
                    {
                        var p = _polys.First(pp => pp.poly_id == id);
                        var ok = await Pg.UpdateByIdAsync("poly_id", p.poly_id, new Dictionary<string, object>
                        {
                            ["x_m"] = RoundTo(p.x_m, ROUND_MM),
                            ["y_m"] = RoundTo(p.y_m, ROUND_MM),
                            ["ancho_m"] = RoundTo(p.ancho_m, ROUND_MM),
                            ["alto_m"] = RoundTo(p.alto_m, ROUND_MM),
                            ["z_order"] = p.z_order,
                            ["area_id"] = p.area_id ?? (object)DBNull.Value,
                            ["canvas_id"] = p.canvas_id,
                            ["etiqueta"] = p.etiqueta ?? (object)DBNull.Value,
                            ["color_hex"] = p.color_hex ?? (object)DBNull.Value
                        });
                        _ = ok;
                    }

                    await NormalizeAndPersistDoorsAndWindowsAsync(ROUND_MM);

                    _saveMsg = $"Alineado ✔ ({changed.Count} área(s))";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AlinearTodo] {ex}");
                    _saveMsg = "Error al alinear (ver consola).";
                }
                finally { _saving = false; StateHasChanged(); }
            }
            else
            {
                await NormalizeAndPersistDoorsAndWindowsAsync(ROUND_MM);
                _saveMsg = "Nada para alinear (ya está preciso).";
                StateHasChanged();
            }
        }

        private async Task NormalizeAndPersistDoorsAndWindowsAsync(decimal roundMm)
        {
            try
            {
                // --- Puertas SOLO de la planta activa ---
                Pg.UseSheet("puertas");
                foreach (var d in _doors.Where(IsDoorVisible))  // << antes: foreach (var d in _doors)
                {
                    (string orient, string areaId)? prefer = null;
                    if (_lastEdgeForDoor.TryGetValue(d.door_id, out var prev)) prefer = prev;

                    // IMPORTANTE: FindNearestPolyEdge ya usa VisiblePolys() por defecto,
                    // así que nunca va a “pescar” bordes de otras plantas.
                    var poly = FindNearestPolyEdge(d.x_m, d.y_m, out var axis, out var sx, out var sy, out var segA, out var segB, prefer);
                    if (poly is not null)
                    {
                        var maxLen = EdgeMaxLen(poly, axis);
                        d.largo_m = Clamp(0.4m, maxLen, d.largo_m);

                        if (axis == "E")
                        {
                            var minX = segA + CORNER_GUARD;
                            var maxX = segB - CORNER_GUARD - d.largo_m;
                            d.x_m = Clamp(minX, maxX, sx);
                            d.y_m = sy;
                            d.orientacion = "E";
                        }
                        else
                        {
                            var minY = segA + CORNER_GUARD;
                            var maxY = segB - CORNER_GUARD - d.largo_m;
                            d.y_m = Clamp(minY, maxY, sy);
                            d.x_m = sx;
                            d.orientacion = "N";
                        }

                        var other = FindSharedEdgeNeighbour(poly, axis, d.x_m, d.y_m, d.largo_m);
                        d.area_id_a = poly.area_id;
                        d.area_id_b = other?.area_id;
                        _lastEdgeForDoor[d.door_id] = (axis, d.area_id_a ?? "");
                    }

                    // Sanear FKs
                    d.area_id_a = SanitizeFk(d.area_id_a, _areasLookup);
                    d.area_id_b = SanitizeFk(d.area_id_b, _areasLookup);

                    // Persistir (igual que antes)...
                    decimal x2 = d.x_m, y2 = d.y_m;
                    switch (d.orientacion)
                    {
                        case "E": x2 = d.x_m + d.largo_m; break;
                        case "W": x2 = d.x_m - d.largo_m; break;
                        case "S": y2 = d.y_m + d.largo_m; break;
                        case "N": y2 = d.y_m - d.largo_m; break;
                    }

                    var toSave = new Dictionary<string, object>
                    {
                        ["canvas_id"] = d.canvas_id,
                        ["area_a"] = DbNullIfEmpty(d.area_id_a),
                        ["area_b"] = DbNullIfEmpty(d.area_id_b),
                        ["x1_m"] = RoundTo(d.x_m, roundMm),
                        ["y1_m"] = RoundTo(d.y_m, roundMm),
                        ["x2_m"] = RoundTo(x2, roundMm),
                        ["y2_m"] = RoundTo(y2, roundMm),
                        ["grosor_m"] = 0.10m,
                        ["color_hex"] = "#13A076"
                    };

                    var okd = await Pg.UpdateByIdAsync("puerta_id", d.door_id, toSave);
                    if (!okd) { toSave["puerta_id"] = d.door_id; await Pg.CreateAsync(toSave); }
                }

                // --- Ventanas SOLO de la planta activa ---
                Pg.UseSheet("ventanas");
                foreach (var w in _windows.Where(IsWinVisible))  // << antes: foreach (var w in _windows)
                {
                    (string orient, string areaId)? prefer = null;
                    if (_lastEdgeForWin.TryGetValue(w.win_id, out var prev)) prefer = prev;

                    var poly = FindNearestPolyEdge(w.x_m, w.y_m, out var axis, out var sx, out var sy, out var segA, out var segB, prefer);
                    if (poly is not null)
                    {
                        var maxLen = EdgeMaxLen(poly, axis);
                        w.largo_m = Clamp(0.4m, maxLen, w.largo_m);

                        if (axis == "E")
                        {
                            var minX = segA + CORNER_GUARD;
                            var maxX = segB - CORNER_GUARD - w.largo_m;
                            w.x_m = Clamp(minX, maxX, sx);
                            w.y_m = sy;
                            w.orientacion = "E";
                        }
                        else
                        {
                            var minY = segA + CORNER_GUARD;
                            var maxY = segB - CORNER_GUARD - w.largo_m;
                            w.y_m = Clamp(minY, maxY, sy);
                            w.x_m = sx;
                            w.orientacion = "N";
                        }

                        var other = FindSharedEdgeNeighbour(poly, axis, w.x_m, w.y_m, w.largo_m);
                        w.area_id_a = poly.area_id;
                        w.area_id_b = other?.area_id;
                        _lastEdgeForWin[w.win_id] = (axis, w.area_id_a ?? "");
                    }

                    // Sanear FKs
                    w.area_id_a = SanitizeFk(w.area_id_a, _areasLookup);
                    w.area_id_b = SanitizeFk(w.area_id_b, _areasLookup);

                    // Persistir (igual que antes)...
                    decimal x2 = w.x_m, y2 = w.y_m;
                    switch (w.orientacion)
                    {
                        case "E": x2 = w.x_m + w.largo_m; break;
                        case "W": x2 = w.x_m - w.largo_m; break;
                        case "S": y2 = w.y_m + w.largo_m; break;
                        case "N": y2 = w.y_m - w.largo_m; break;
                    }

                    var toSave = new Dictionary<string, object>
                    {
                        ["canvas_id"] = w.canvas_id,
                        ["area_a"] = DbNullIfEmpty(w.area_id_a),
                        ["area_b"] = DbNullIfEmpty(w.area_id_b),
                        ["x1_m"] = RoundTo(w.x_m, roundMm),
                        ["y1_m"] = RoundTo(w.y_m, roundMm),
                        ["x2_m"] = RoundTo(x2, roundMm),
                        ["y2_m"] = RoundTo(y2, roundMm),
                        ["grosor_m"] = 0.05m,
                        ["color_hex"] = "#66CCFF"
                    };

                    var okw = await Pg.UpdateByIdAsync("ventana_id", w.win_id, toSave);
                    if (!okw) { toSave["ventana_id"] = w.win_id; await Pg.CreateAsync(toSave); }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NormalizeAndPersistDoorsAndWindowsAsync] {ex}");
            }
        }


        // Lookup de plantas y meta por área
        private Dictionary<string, string> _plantasLookup = new(StringComparer.OrdinalIgnoreCase);

        private class AreaMeta
        {
            public string area_id { get; set; } = "";
            public string? planta_id { get; set; }
            public string? canvas_id { get; set; }
            public int? laboratorio_id { get; set; }
            public decimal? altura_m { get; set; }
            public string anotaciones { get; set; } = "SIN MODIFICACIONES";
        }
        private readonly Dictionary<string, AreaMeta> _areasMeta = new(StringComparer.OrdinalIgnoreCase);

        //CALCULAR AREA TOTAL
        private decimal CalcAreaTotalM2(string areaId)
        {
            var sum = _polys
                .Where(p => string.Equals(p.area_id ?? "", areaId ?? "", StringComparison.OrdinalIgnoreCase))
                .Sum(p => PolygonArea(p.puntos));
            return Math.Round(sum, 3, MidpointRounding.AwayFromZero);
        }

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

        private (decimal x, decimal y) PolyCenter(Poly p)
        {
            if (p.puntos.Count < 3)
            {
                return (p.x_m + p.ancho_m / 2m, p.y_m + p.alto_m / 2m);
            }
            decimal cx = 0m;
            decimal cy = 0m;
            decimal area = 0m;
            for (int i = 0; i < p.puntos.Count; i++)
            {
                var a = p.puntos[i];
                var b = p.puntos[(i + 1) % p.puntos.Count];
                var cross = (a.X * b.Y) - (b.X * a.Y);
                area += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (area == 0) return (p.x_m + p.ancho_m / 2m, p.y_m + p.alto_m / 2m);
            area *= 0.5m;
            return (cx / (6m * area), cy / (6m * area));
        }

        // ----- helpers internos para “Alinear todo”
        private static decimal RoundTo(decimal v, decimal step)
        {
            if (step <= 0) return v;
            return Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
        }

        private bool CanTranslateX(Poly p, decimal dx)
        {
            if (dx == 0) return true;
            var newX = Clamp(0m, Wm - p.ancho_m, p.x_m + dx);
            var test = new Poly { poly_id = p.poly_id, x_m = newX, y_m = p.y_m, ancho_m = p.ancho_m, alto_m = p.alto_m };
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (RectOverlap(test, o)) return false;
            }
            return true;
        }

        // ¿Un área pertenece a la planta actual?
        private bool IsAreaInCurrentPlanta(string? areaId)
            => !string.IsNullOrWhiteSpace(areaId)
               && _areasMeta.TryGetValue(areaId!, out var meta)
               && string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
               && IsCanvasMatch(meta.canvas_id);

        private bool IsAreaInCurrentCanvas(string? areaId)
            => !string.IsNullOrWhiteSpace(areaId)
               && _areasMeta.TryGetValue(areaId!, out var meta)
               && IsCanvasMatch(meta.canvas_id);

        private bool IsCanvasMatch(string? canvasId)
            => AllowCrossCanvasAreas()
               || string.IsNullOrWhiteSpace(canvasId)
               || string.Equals(canvasId, _canvas?.canvas_id ?? "", StringComparison.OrdinalIgnoreCase);

        private bool AllowCrossCanvasAreas()
            => !_areasMeta.Values.Any(meta =>
                   string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(meta.canvas_id ?? "", _canvas?.canvas_id ?? "", StringComparison.OrdinalIgnoreCase));

        // Área por defecto en la planta activa
        private string? DefaultAreaIdForCurrentPlanta()
        {
            if (IsAreaInCurrentPlanta(_sel?.area_id)) return _sel!.area_id;

            var firstInPlanta = _areasLookup
                .Select(kv => kv.Key)
                .FirstOrDefault(aid => _areasMeta.TryGetValue(aid, out var meta)
                                    && string.Equals(meta.planta_id ?? "", _currentPlantaId ?? "", StringComparison.OrdinalIgnoreCase)
                                    && IsCanvasMatch(meta.canvas_id));

            return string.IsNullOrWhiteSpace(firstInPlanta) ? null : firstInPlanta;
        }

        private async Task CrearAreaAsync()
        {
            if (_canvas is null)
            {
                _newAreaMsg = "Selecciona un canvas antes de crear un área.";
                return;
            }
            if (_canvas.laboratorio_id is null)
            {
                _newAreaMsg = "El canvas no tiene laboratorio asociado.";
                return;
            }

            var nombre = _newAreaName.Trim();
            if (string.IsNullOrWhiteSpace(nombre))
            {
                _newAreaMsg = "Ingresa un nombre para el área.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_newAreaPlantaId) || !_plantasLookup.ContainsKey(_newAreaPlantaId))
            {
                _newAreaMsg = "Selecciona una planta válida.";
                return;
            }

            _creatingArea = true;
            _newAreaMsg = null;
            try
            {
                var baseId = BuildAreaId(nombre);
                var areaId = baseId;
                var suffix = 1;
                while (_areasLookup.ContainsKey(areaId))
                {
                    areaId = $"{baseId}_{suffix++}";
                }

                Pg.UseSheet("areas");
                var payload = new Dictionary<string, object>
                {
                    ["area_id"] = areaId,
                    ["nombre_areas"] = nombre,
                    ["planta_id"] = _newAreaPlantaId!,
                    ["canvas_id"] = _canvas.canvas_id,
                    ["laboratorio_id"] = _canvas.laboratorio_id.Value,
                    ["altura_m"] = DBNull.Value,
                    ["area_total_m2"] = 0m,
                    ["anotaciones_del_area"] = "SIN MODIFICACIONES"
                };
                await Pg.CreateAsync(payload);

                _areasLookup[areaId] = nombre;
                _areasMeta[areaId] = new AreaMeta
                {
                    area_id = areaId,
                    planta_id = _newAreaPlantaId,
                    canvas_id = _canvas.canvas_id,
                    laboratorio_id = _canvas.laboratorio_id,
                    altura_m = null,
                    anotaciones = "SIN MODIFICACIONES"
                };

                _drawAreaId = areaId;
                _newAreaName = string.Empty;
                _newAreaMsg = "Área creada.";
            }
            catch (Exception ex)
            {
                _newAreaMsg = "Error al crear el área.";
                Console.Error.WriteLine($"[CrearAreaAsync] {ex}");
            }
            finally
            {
                _creatingArea = false;
                StateHasChanged();
            }
        }

        private static string BuildAreaId(string name)
        {
            var normalized = name.Trim().ToLowerInvariant();
            var formD = normalized.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in formD)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_') sb.Append('_');
            }
            var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_{2,}", "_").Trim('_');
            return string.IsNullOrWhiteSpace(slug) ? $"area_{Guid.NewGuid():N}".Substring(0, 9) : slug;
        }

        private bool CanTranslateY(Poly p, decimal dy)
        {
            if (dy == 0) return true;
            var newY = Clamp(0m, Hm - p.alto_m, p.y_m + dy);
            var test = new Poly { poly_id = p.poly_id, x_m = p.x_m, y_m = newY, ancho_m = p.ancho_m, alto_m = p.alto_m };
            foreach (var o in VisiblePolys())
            {
                if (o.poly_id == p.poly_id) continue;
                if (RectOverlap(test, o)) return false;
            }
            return true;
        }
    }
}

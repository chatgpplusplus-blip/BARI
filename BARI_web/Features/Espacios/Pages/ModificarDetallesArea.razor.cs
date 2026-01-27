using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using BARI_web.General_Services.DataBaseConnection;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class ModificarDetallesArea : ComponentBase
    {
        [Parameter] public string AreaSlug { get; set; } = "";
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;

        // =========================
        // Modelos base
        // =========================
        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal largo_m, decimal margen_m);

        private readonly record struct Point(decimal X, decimal Y);

        private Dictionary<string, string> _subcatsInstLookup = new(StringComparer.OrdinalIgnoreCase);

        // ✅ Categorías reales de instalaciones en tu BD
        private static readonly string[] CategoriaInstIds = new[]
        {
    "ins-agua",
    "ins-clima",
    "ins-electrica",
    "ins-gas",
    "ins-mobiliario",
    "ins-teleco"
};

        private record Poly(
            string poly_id,
            string canvas_id,
            string? area_id,
            decimal x_m,
            decimal y_m,
            decimal ancho_m,
            decimal largo_m,
            int z_order,
            string? etiqueta,
            string? color_hex,
            List<Point> puntos
        );

        private class AreaDraw
        {
            public string AreaId { get; init; } = "";
            public List<Poly> Polys { get; } = new();
            public decimal MinX { get; set; }
            public decimal MinY { get; set; }
            public decimal MaxX { get; set; }
            public decimal MaxY { get; set; }
            public string Label { get; set; } = "";
            public string Fill { get; set; } = "#E6E6E6";
            public List<(decimal x1, decimal y1, decimal x2, decimal y2)> Outline { get; } = new();
        }

        // OJO: la tabla poligonos_interiores NO estaba en tu SQL pegado.
        // Mantengo las columnas que tú ya estabas usando en el proyecto (para no romper el runtime).
        private class InnerItem
        {
            public string poly_in_id { get; set; } = "";
            public string area_poly_id { get; set; } = "";

            // "tenancy"
            public string canvas_id { get; set; } = "";
            public string area_id { get; set; } = "";

            // geom (relativo al parent)
            public decimal eje_x_rel_m { get; set; }
            public decimal eje_y_rel_m { get; set; }

            public decimal ancho_m { get; set; }
            public decimal largo_m { get; set; }

            // display
            public string? label { get; set; }
            public string fill { get; set; } = "#4B5563";
            public decimal opacidad { get; set; } = 0.35m;
            public int z_order { get; set; } = 0;

            // calculados (absolutos en canvas)
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }

            // vínculos (XOR en UI)
            public string? meson_id { get; set; }
            public string? instalacion_id { get; set; }
        }

        private class BlockItem
        {
            public string bloque_id { get; set; } = "";
            public string canvas_id { get; set; } = "";

            // XOR: uno u otro
            public string? instalacion_id { get; set; }
            public string? meson_id { get; set; }

            public string? etiqueta { get; set; }
            public string? color_hex { get; set; }
            public int z_order { get; set; }

            public decimal pos_x { get; set; }
            public decimal pos_y { get; set; }
            public decimal ancho { get; set; }
            public decimal largo { get; set; }
            public decimal? altura { get; set; }   // NUEVO (opcional)

            public decimal offset_x { get; set; }
            public decimal offset_y { get; set; }

            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
        }
        private class Door { public decimal x_m, y_m, largo_m; public string orientacion = "E"; }
        private class Win { public decimal x_m, y_m, largo_m; public string orientacion = "E"; }

        // =========================
        // Mesones / Instalaciones (SEGÚN TU SQL)
        // =========================
        private class Meson
        {
            public string meson_id { get; set; } = "";
            public string area_id { get; set; } = "";
            public string nombre_meson { get; set; } = "";
            public int? niveles_totales { get; set; }
            public int laboratorio_id { get; set; }

            public string? imagen_url { get; set; }

        }


        private class Instalacion
        {
            public string instalacion_id { get; set; } = "";
            public string nombre { get; set; } = "";

            public string? subcategoria_id { get; set; }

            public int laboratorio_id { get; set; }
            public string? area_id { get; set; }

            public string? canvas_id { get; set; }

            public string? estado_id { get; set; }
            public DateTime? fecha_instalacion { get; set; }
            public DateTime? fecha_ultima_revision { get; set; }
            public DateTime? fecha_proxima_revision { get; set; }

            public string? proveedor_servicio { get; set; }
            public string? observaciones { get; set; }
            public string? descripcion { get; set; }
            public string? imagen_url { get; set; }
        }

        // =========================
        // Estado / caches
        // =========================
        private CanvasLab? _canvas;
        private AreaDraw? _area;

        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();

        private readonly List<InnerItem> _inners = new();
        private readonly Dictionary<string, InnerItem> _mapIn = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<BlockItem> _blocks = new();
        private readonly Dictionary<string, BlockItem> _blocksById = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Meson> _mesones = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Instalacion> _instalaciones = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _mesonesLookup = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _estadosLookup = new(StringComparer.OrdinalIgnoreCase);

        // =========================
        // Viewbox / pan-zoom
        // =========================
        private decimal VX, VY, VW, VH;
        private double _zoom = 1.0;
        private decimal _panX = 0m, _panY = 0m;
        private (double x, double y)? _panStart;
        private bool _panMoved = false;
        private ElementReference _svgRef;

        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.largo_m ?? 10m;

        private string ViewBox()
        {
            var vw = (decimal)((double)Wm / _zoom);
            var vh = (decimal)((double)Hm / _zoom);
            return $"{S(_panX)} {S(_panY)} {S(vw)} {S(vh)}";
        }

        private string AspectRatioString()
        {
            var vw = VW <= 0 ? 1 : VW;
            var vh = VH <= 0 ? 1 : VH;
            var ar = (double)vw / (double)vh;
            return $"{ar:0.###} / 1";
        }

        private decimal AreaCenterX => _area is null ? 0m : (_area.MinX + _area.MaxX) / 2m;
        private decimal AreaCenterY => _area is null ? 0m : (_area.MinY + _area.MaxY) / 2m;

        private decimal GridStartX, GridEndX, GridStartY, GridEndY;

        // =========================
        // Selección / drag
        // =========================
        private string? _selIn;
        private string? _hoverIn;
        private string? _selBlock;
        private string? _hoverBlock;

        private (decimal x, decimal y)? _dragStart;
        private (decimal dx, decimal dy)? _grab;

        private InnerItem? _dragIn;
        private Poly? _dragParent;
        private (decimal x, decimal y, decimal w, decimal h)? _beforeDragIn;
        private Handle _activeHandle = Handle.None;

        private BlockItem? _dragBlock;
        private (decimal x, decimal y, decimal w, decimal h)? _beforeDragBlock;
        private (decimal dx, decimal dy)? _blockGrab;
        private Handle _blockHandle = Handle.None;

        private enum Handle { NW, NE, SW, SE, None }

        // =========================
        // Guardar
        // =========================
        private bool _saving = false;
        private string _saveMsg = "";

        // =========================
        // UI: crear bloque (solo asignar existente)
        // =========================
        private bool _showBlockCreator = false;
        private bool _newBlockAssignInstalacion = false;
        private bool _newBlockAssignMeson = false;

        private string? _newBlockInstalacionId;
        private string? _newBlockMesonId;

        private string _newBlockEtiqueta = "";
        private decimal _newBlockAncho = 0.6m;
        private decimal _newBlockLargo = 0.4m;
        private decimal _newBlockOffsetX = 0m;
        private decimal _newBlockOffsetY = 0m;
        private string _newBlockColor = "#2563eb";
        private string? _blockMsg;

        // =========================
        // UI: crear mesón (SQL)
        // =========================
        private string _nuevoMesonNombre = "";
        private int? _nuevoMesonNiveles = null;
        private string? _nuevoMesonMsg;

        private string _nuevoMesonImagenUrl = string.Empty;
        private decimal? _newBlockAltura = null;
        public string? imagen_url { get; set; }

        // =========================
        // UI: crear instalación (SQL)
        // =========================
        private string _nuevoIns_Nombre = "";
        private string? _nuevoIns_SubcategoriaId = "";
        private string? _nuevoIns_EstadoId = "";
        private string? _nuevoIns_FechaInstalacion = "";
        private string? _nuevoIns_FechaUltRev = "";
        private string? _nuevoIns_FechaProxRev = "";
        private string? _nuevoIns_ProveedorServicio = "";
        private string? _nuevoIns_Observaciones = "";
        private string? _nuevoIns_Descripcion = "";
        private string? _nuevoIns_ImagenUrl = "";
        private string? _nuevoIns_Msg;
        // Esta propiedad actúa como puente entre el Input y el String
        private DateTime? FechaInstalacionWrapper
        {
            get => DateTime.TryParse(_nuevoIns_FechaInstalacion, out var dt) ? dt : null;
            set => _nuevoIns_FechaInstalacion = value?.ToString("yyyy-MM-dd");
        }


        private DateTime? FechaUltRevWrapper
        {
            get => DateTime.TryParse(_nuevoIns_FechaUltRev, out var dt) ? dt : null;
            set => _nuevoIns_FechaUltRev = value?.ToString("yyyy-MM-dd");
        }

        private DateTime? FechaProxRevWrapper
        {
            get => DateTime.TryParse(_nuevoIns_FechaProxRev, out var dt) ? dt : null;
            set => _nuevoIns_FechaProxRev = value?.ToString("yyyy-MM-dd");
        }

        // laboratorio (tenancy)
        private int _laboratorioId = 1;

        // =========================
        // Tolerancias / colisiones
        // =========================
        private const decimal OutlineStroke = 0.28m;
        private const decimal EPS_MINW = 0.10m;
        private const decimal EPS_SEP = 0.002m; // separación mínima en colisión (~2mm)
        private const decimal PAD_AREA = 0.0005m;

        private enum InnerKind { None, Meson, Instalacion }


        private sealed record TextLayout(decimal FontSize, decimal LineHeight, List<string> Lines);

        private TextLayout LayoutLabel(string text, decimal boxW, decimal boxH)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return new TextLayout(0.18m, 1.15m, new List<string>());

            // “Padding” interno para que no pegue al borde
            var pad = 0.18m;
            var availW = Math.Max(0.10m, boxW - pad);
            var availH = Math.Max(0.10m, boxH - pad);

            var lineHeight = 1.15m;

            // Tamaño máximo razonable según el rect
            var maxFs = Math.Min(0.45m, Math.Min(availW, availH) * 0.45m);
            var minFs = 0.12m;
            var step = 0.02m;

            for (var fs = maxFs; fs >= minFs; fs -= step)
            {
                var lines = WrapByWidth(text, availW, fs);
                var neededH = lines.Count * (fs * lineHeight);

                // Si entra en largo, lo damos por bueno
                if (neededH <= availH)
                    return new TextLayout(fs, lineHeight, lines);
            }

            // Fallback: mínimo size (y si aun así sobran líneas, recorta)
            var minLines = WrapByWidth(text, availW, minFs);
            var maxLines = Math.Max(1, (int)Math.Floor(availH / (minFs * lineHeight)));

            if (minLines.Count > maxLines)
            {
                minLines = minLines.Take(maxLines).ToList();
                // opcional: elipsis en la última línea
                var last = minLines[^1];
                if (!last.EndsWith("…"))
                    minLines[^1] = last.Length > 1 ? last[..Math.Max(1, last.Length - 1)] + "…" : "…";
            }

            return new TextLayout(minFs, lineHeight, minLines);
        }

        private List<string> WrapByWidth(string text, decimal widthM, decimal fontSizeM)
        {
            // aproximación: ancho promedio por carácter ~ 0.55em
            var charW = fontSizeM * 0.55m;
            var maxChars = (int)Math.Max(1, Math.Floor(widthM / charW));

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var current = "";

            foreach (var w in words)
            {
                if (current.Length == 0)
                {
                    current = w;
                }
                else if ((current.Length + 1 + w.Length) <= maxChars)
                {
                    current += " " + w;
                }
                else
                {
                    lines.Add(current);
                    current = w;
                }

                // Si una palabra sola es más larga que el máximo, la partimos
                while (current.Length > maxChars)
                {
                    lines.Add(current.Substring(0, maxChars));
                    current = current.Substring(maxChars);
                }
            }

            if (current.Length > 0)
                lines.Add(current);

            return lines;
        }


        // =========================
        // INIT
        // =========================
        protected override async Task OnInitializedAsync()
        {
            try
            {
                var targetAreaId = await ResolveAreaIdFromSlug(AreaSlug);
                _laboratorioId = await LoadLaboratorioIdForArea(targetAreaId);

                var canvasIdForArea = await ResolveCanvasForArea(targetAreaId);

                Pg.UseSheet("canvas_lab");
                var canvases = await Pg.ReadAllAsync();
                var c = !string.IsNullOrWhiteSpace(canvasIdForArea)
                    ? canvases.FirstOrDefault(row => string.Equals(Get(row, "canvas_id"), canvasIdForArea, StringComparison.OrdinalIgnoreCase))
                    : canvases.FirstOrDefault();

                if (c is null)
                {
                    _saveMsg = "No hay canvas_lab.";
                    return;
                }

                _canvas = new CanvasLab(
                    Get(c, "canvas_id"),
                    Get(c, "nombre"),
                    Dec(Get(c, "ancho_m", "0")),
                    Dec(Get(c, "largo_m", "0")),
                    Dec(Get(c, "margen_m", "0"))
                );

                // Polígonos del área
                var pointsByPoly = await LoadPolyPointsAsync();
                List<Poly> polys = new();

                Pg.UseSheet("poligonos");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                    var areaId = NullIfEmpty(Get(r, "area_id")) ?? "";
                    if (!string.Equals(areaId, targetAreaId, StringComparison.OrdinalIgnoreCase)) continue;

                    var polyId = Get(r, "poly_id");
                    var points = pointsByPoly.TryGetValue(polyId, out var list) ? list : new List<Point>();

                    decimal x, y, w, h;

                    if (points.Count >= 3)
                    {
                        var bounds = BoundsOfPointList(points);
                        x = bounds.minX;
                        y = bounds.minY;
                        w = Math.Max(0.1m, bounds.maxX - bounds.minX);
                        h = Math.Max(0.1m, bounds.maxY - bounds.minY);
                    }
                    else
                    {
                        x = Dec(Get(r, "x_m", "0"));
                        y = Dec(Get(r, "y_m", "0"));
                        w = Dec(Get(r, "ancho_m", "0"));
                        h = Dec(Get(r, "largo_m", "0"));

                        if (w < 0m) { x += w; w = -w; }
                        if (h < 0m) { y += h; h = -h; }
                        points = BuildRectPoints(x, y, w, h);
                    }

                    polys.Add(new Poly(
                        polyId,
                        Get(r, "canvas_id"),
                        areaId,
                        x, y, w, h,
                        Int(Get(r, "z_order", "0")),
                        NullIfEmpty(Get(r, "etiqueta")),
                        NullIfEmpty(Get(r, "color_hex")),
                        points
                    ));
                }

                // Construir área
                var a = new AreaDraw { AreaId = targetAreaId };
                a.Polys.AddRange(polys.Where(p => p.ancho_m > 0m && p.largo_m > 0m).OrderBy(p => p.z_order));

                if (a.Polys.Count == 0)
                {
                    _saveMsg = "No hay polígonos válidos para el área.";
                    return;
                }

                a.MinX = a.Polys.Min(p => p.x_m);
                a.MinY = a.Polys.Min(p => p.y_m);
                a.MaxX = a.Polys.Max(p => p.x_m + p.ancho_m);
                a.MaxY = a.Polys.Max(p => p.y_m + p.largo_m);

                try
                {
                    var lookup = await Pg.GetLookupAsync("areas", "area_id", "nombre_areas");
                    a.Label = (lookup.TryGetValue(a.AreaId, out var n) ? n : a.AreaId).ToUpperInvariant();
                }
                catch
                {
                    a.Label = a.AreaId.ToUpperInvariant();
                }

                a.Fill = a.Polys.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";
                BuildAreaOutlineFromPoints(a);

                _area = a;

                // Pan/zoom
                FitViewBoxToAreaWithAspect(a, 0.25m);
                UpdateViewMetrics();
                CacheGrid();

                // Lookups UI
                try
                {
                    var catNames = await Pg.GetLookupAsync("categorias", "categoria_id", "nombre");

                    Pg.UseSheet("subcategorias");
                    var rows = await Pg.ReadAllAsync();

                    var instCats = new HashSet<string>(CategoriaInstIds, StringComparer.OrdinalIgnoreCase);

                    _subcatsInstLookup = rows
                        .Where(r => instCats.Contains(Get(r, "categoria_id").Trim()))
                        .OrderBy(r =>
                        {
                            var cid = Get(r, "categoria_id").Trim();
                            return catNames.TryGetValue(cid, out var cn) ? cn : cid;
                        }, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => Get(r, "nombre"), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            r => Get(r, "subcategoria_id"),
                            r =>
                            {
                                var cid = Get(r, "categoria_id").Trim();
                                var cat = catNames.TryGetValue(cid, out var cn) ? cn : cid;
                                return $"{cat} · {Get(r, "nombre")}";
                            },
                            StringComparer.OrdinalIgnoreCase
                        );
                }
                catch
                {
                    _subcatsInstLookup = new(StringComparer.OrdinalIgnoreCase);
                }


                try { _estadosLookup = await Pg.GetLookupAsync("estados_activo", "estado_id", "nombre"); }
                catch { _estadosLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }

                // Cargas por área
                await LoadMesonesForArea(a.AreaId);
                await LoadInstalacionesForArea(a.AreaId);
                await LoadInnerItemsForArea(a);
                await LoadBlocksForCanvas();
                await LoadDoorsAndWindowsForArea(a);


                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                _saveMsg = "Error al cargar (ver consola).";
                Console.Error.WriteLine($"[ModificarDetallesArea.OnInitializedAsync] {ex}");
            }
        }

        // =========================
        // Cargas DB
        // =========================
        private async Task LoadMesonesForArea(string areaId)
        {
            _mesones.Clear();
            _mesonesLookup.Clear();

            Pg.UseSheet("mesones");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var rArea = Get(r, "area_id").Trim();
                if (!string.Equals(rArea, areaId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // blindaje por laboratorio
                var labRaw = Get(r, "laboratorio_id", _laboratorioId.ToString(CultureInfo.InvariantCulture)).Trim();
                if (int.TryParse(labRaw, out var lab) && lab != _laboratorioId)
                    continue;

                var id = Get(r, "meson_id").Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var nombre = Get(r, "nombre_meson").Trim();

                int? niveles = null;
                var nivelesRaw = NullIfEmpty(Get(r, "niveles_totales"));
                if (!string.IsNullOrWhiteSpace(nivelesRaw) && int.TryParse(nivelesRaw, out var n))
                    niveles = n;

                _mesones[id] = new Meson
                {
                    meson_id = id,
                    area_id = areaId,
                    nombre_meson = nombre,
                    niveles_totales = niveles,
                    laboratorio_id = _laboratorioId,
                    imagen_url = NullIfEmpty(Get(r, "imagen_url")) // ✅ NUEVO
                };


                _mesonesLookup[id] = nombre;
            }
        }
        private IEnumerable<Meson> MesonesDelAreaActual()
        {
            if (_area is null) return Enumerable.Empty<Meson>();

            return _mesones.Values
                .Where(m =>
                    string.Equals(m.area_id, _area.AreaId, StringComparison.OrdinalIgnoreCase) &&
                    m.laboratorio_id == _laboratorioId)
                .OrderBy(m => m.nombre_meson, StringComparer.OrdinalIgnoreCase);
        }



        private async Task LoadInstalacionesForArea(string areaId)
        {
            _instalaciones.Clear();

            Pg.UseSheet("instalaciones");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(NullIfEmpty(Get(r, "area_id")) ?? "", areaId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = Get(r, "instalacion_id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                _instalaciones[id] = new Instalacion
                {
                    instalacion_id = id,
                    nombre = Get(r, "nombre"),
                    subcategoria_id = NullIfEmpty(Get(r, "subcategoria_id")),
                    laboratorio_id = Int(Get(r, "laboratorio_id", _laboratorioId.ToString(CultureInfo.InvariantCulture))),
                    area_id = NullIfEmpty(Get(r, "area_id")),
                    canvas_id = NullIfEmpty(Get(r, "canvas_id")),
                    estado_id = NullIfEmpty(Get(r, "estado_id")),
                    fecha_instalacion = ParseDate(NullIfEmpty(Get(r, "fecha_instalacion"))),
                    fecha_ultima_revision = ParseDate(NullIfEmpty(Get(r, "fecha_ultima_revision"))),
                    fecha_proxima_revision = ParseDate(NullIfEmpty(Get(r, "fecha_proxima_revision"))),
                    proveedor_servicio = NullIfEmpty(Get(r, "proveedor_servicio")),
                    observaciones = NullIfEmpty(Get(r, "observaciones")),
                    descripcion = NullIfEmpty(Get(r, "descripcion")),
                    imagen_url = NullIfEmpty(Get(r, "imagen_url"))
                };
            }
        }

        private async Task LoadInnerItemsForArea(AreaDraw a)
        {
            _inners.Clear();
            _mapIn.Clear();

            var areaPolys = a.Polys.ToDictionary(p => p.poly_id, p => p, StringComparer.OrdinalIgnoreCase);

            Pg.UseSheet("poligonos_interiores");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var area_poly_id = Get(r, "area_poly_id");
                if (!areaPolys.TryGetValue(area_poly_id, out var parentPoly)) continue;

                var mesonId = NullIfEmpty(Get(r, "meson_id"));
                var instId = NullIfEmpty(Get(r, "instalacion_id"));
                // XOR defensivo: si vienen ambos, priorizo mesón
                if (!string.IsNullOrWhiteSpace(mesonId) && !string.IsNullOrWhiteSpace(instId))
                    instId = null;

                var it = new InnerItem
                {
                    poly_in_id = Get(r, "poly_in_id"),
                    area_poly_id = area_poly_id,

                    canvas_id = Get(r, "canvas_id", _canvas?.canvas_id ?? ""),
                    area_id = Get(r, "area_id", a.AreaId),

                    eje_x_rel_m = Dec(Get(r, "eje_x_rel_m", "0")),
                    eje_y_rel_m = Dec(Get(r, "eje_y_rel_m", "0")),

                    ancho_m = Dec(Get(r, "ancho_m", "0.6")),
                    largo_m = Dec(Get(r, "largo_m", "0.4")),

                    z_order = Int(Get(r, "z_order", "0")),
                    label = NullIfEmpty(Get(r, "etiqueta")),
                    fill = NullIfEmpty(Get(r, "color_hex")) ?? "#4B5563",
                    opacidad = Dec(Get(r, "opacidad_0_1", "0.35")),

                    meson_id = mesonId,
                    instalacion_id = instId
                };

                it.abs_x = parentPoly.x_m + it.eje_x_rel_m;
                it.abs_y = parentPoly.y_m + it.eje_y_rel_m;

                // clamp real (contorno) + colisiones iniciales
                var w = Math.Max(EPS_MINW, it.ancho_m);
                var h = Math.Max(EPS_MINW, it.largo_m);
                var (cx, cy, newParent) = ClampInnerToAreaAndCollisions(it, parentPoly, it.abs_x, it.abs_y, w, h);
                it.area_poly_id = newParent.poly_id;
                it.abs_x = cx;
                it.abs_y = cy;
                it.ancho_m = w;
                it.largo_m = h;
                it.eje_x_rel_m = Math.Round(it.abs_x - newParent.x_m, 3, MidpointRounding.AwayFromZero);
                it.eje_y_rel_m = Math.Round(it.abs_y - newParent.y_m, 3, MidpointRounding.AwayFromZero);

                _inners.Add(it);
                _mapIn[it.poly_in_id] = it;
            }
        }

        private void NormalizeInner(InnerItem it)
        {
            if (_area is null) return;

            // Parent actual (si no existe, toma el primero)
            var parent = _area.Polys.FirstOrDefault(p =>
                string.Equals(p.poly_id, it.area_poly_id, StringComparison.OrdinalIgnoreCase))
                ?? _area.Polys.First();

            // 1) Tamaños mínimos
            it.ancho_m = Math.Max(EPS_MINW, it.ancho_m);
            it.largo_m = Math.Max(EPS_MINW, it.largo_m);

            // 2) Clamp absoluto dentro del contorno real + colisiones (usa tu pipeline existente)
            var (cx, cy, newParent) = ClampInnerToAreaAndCollisions(it, parent, it.abs_x, it.abs_y, it.ancho_m, it.largo_m);

            it.area_poly_id = newParent.poly_id;
            it.abs_x = cx;
            it.abs_y = cy;

            // 3) Recalcular relativos respecto al parent final
            it.eje_x_rel_m = Math.Round(it.abs_x - newParent.x_m, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(it.abs_y - newParent.y_m, 3, MidpointRounding.AwayFromZero);

            
            StateHasChanged();
        }

        private async Task LoadBlocksForCanvas()
        {
            _blocks.Clear();
            _blocksById.Clear();

            if (_canvas is null) return;

            Pg.UseSheet("bloques_int");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
                    continue;

                var instalacionId = NullIfEmpty(Get(r, "instalacion_id"));
                var mesonId = NullIfEmpty(Get(r, "meson_id"));

                // XOR defensivo
                if (!string.IsNullOrWhiteSpace(instalacionId) && !string.IsNullOrWhiteSpace(mesonId))
                    mesonId = null;

                var offsetX = Dec(Get(r, "offset_x", "0"));
                var offsetY = Dec(Get(r, "offset_y", "0"));
                var absX = Dec(Get(r, "pos_x", "0"));
                var absY = Dec(Get(r, "pos_y", "0"));

                // fallback offsets si vienen 0
                if (offsetX == 0m && offsetY == 0m && (absX != 0m || absY != 0m))
                {
                    offsetX = absX - AreaCenterX;
                    offsetY = absY - AreaCenterY;
                }

                var b = new BlockItem
                {
                    bloque_id = Get(r, "bloque_id"),
                    canvas_id = Get(r, "canvas_id"),
                    instalacion_id = instalacionId,
                    meson_id = mesonId,
                    etiqueta = NullIfEmpty(Get(r, "etiqueta")),
                    color_hex = NullIfEmpty(Get(r, "color_hex")) ?? "#2563eb",
                    z_order = Int(Get(r, "z_order", "0")),
                    pos_x = absX,
                    pos_y = absY,
                    ancho = Dec(Get(r, "ancho", "0.6")),
                    largo = Dec(Get(r, "largo", "0.4")),
                    altura = ParseNullableDecimal(Get(r, "altura")),
                    offset_x = offsetX,
                    offset_y = offsetY
                };

                UpdateBlockAbs(b);

                // clamp real al contorno del área si existe
                if (_area is not null)
                {
                    var (nx, ny) = ClampRectInAreaUnion(_area, b.abs_x, b.abs_y, b.ancho, b.largo);
                    b.abs_x = nx;
                    b.abs_y = ny;
                    b.offset_x = b.abs_x - AreaCenterX;
                    b.offset_y = b.abs_y - AreaCenterY;
                    b.pos_x = b.abs_x;
                    b.pos_y = b.abs_y;
                }

                _blocks.Add(b);
                _blocksById[b.bloque_id] = b;
            }
        }

        private async Task LoadDoorsAndWindowsForArea(AreaDraw a)
        {
            _doors.Clear();
            _windows.Clear();
            if (_canvas is null) return;

            static (string orient, decimal len) AxisAndLen(decimal x1, decimal y1, decimal x2, decimal y2)
            {
                if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    return ("E", Math.Abs(x2 - x1));
                else
                    return ("N", Math.Abs(y2 - y1));
            }

            Pg.UseSheet("puertas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (axis, len) = AxisAndLen(x1, y1, x2, y2);

                _doors.Add(new Door { x_m = x1, y_m = y1, largo_m = Math.Max(0.4m, len), orientacion = axis });
            }

            Pg.UseSheet("ventanas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (axis, len) = AxisAndLen(x1, y1, x2, y2);

                _windows.Add(new Win { x_m = x1, y_m = y1, largo_m = Math.Max(0.4m, len), orientacion = axis });
            }
        }

        private async Task<int> LoadLaboratorioIdForArea(string areaId)
        {
            try
            {
                Pg.UseSheet("areas");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "area_id"), areaId, StringComparison.OrdinalIgnoreCase)) continue;
                    var labRaw = Get(r, "laboratorio_id");
                    if (int.TryParse(labRaw, out var labId)) return labId;
                }
            }
            catch { }
            return 1;
        }

        // =========================
        // UI helpers (vínculos)
        // =========================
        private void SeleccionarPorMeson(string mesonId)
        {
            var inner = _inners.FirstOrDefault(i => string.Equals(i.meson_id, mesonId, StringComparison.OrdinalIgnoreCase));
            if (inner is null) return;

            _selIn = inner.poly_in_id;
            _selBlock = null;
            _showBlockCreator = false;
            StateHasChanged();
        }

        private void SeleccionarPorInstalacion(string insId)
        {
            var inner = _inners.FirstOrDefault(i => string.Equals(i.instalacion_id, insId, StringComparison.OrdinalIgnoreCase));
            if (inner is null) return;

            _selIn = inner.poly_in_id;
            _selBlock = null;
            _showBlockCreator = false;
            StateHasChanged();
        }

        private void AssignInnerMeson(InnerItem it, string? mesonId)
        {
            var id = string.IsNullOrWhiteSpace(mesonId) ? null : mesonId;
            it.meson_id = id;
            if (id is not null) it.instalacion_id = null; // XOR
            StateHasChanged();
        }

        private void AssignInnerInstalacion(InnerItem it, string? instalacionId)
        {
            var id = string.IsNullOrWhiteSpace(instalacionId) ? null : instalacionId;
            it.instalacion_id = id;
            if (id is not null) it.meson_id = null; // XOR
            StateHasChanged();
        }

        // =========================
        // Crear mesón / instalación (SQL)
        // =========================
        private void CrearMesonRegistro()
        {
            if (_area is null) return;

            var nombre = (_nuevoMesonNombre ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nombre))
                nombre = PickNombreUnico(_area.AreaId, _mesones.Values);

            var mesonId = $"mes_{Guid.NewGuid():N}".Substring(0, 12);

            _mesones[mesonId] = new Meson
            {
                meson_id = mesonId,
                area_id = _area.AreaId,
                nombre_meson = nombre,
                niveles_totales = _nuevoMesonNiveles,
                laboratorio_id = _laboratorioId,
                imagen_url = string.IsNullOrWhiteSpace(_nuevoMesonImagenUrl) ? null : _nuevoMesonImagenUrl.Trim() // ✅ NUEVO
            };

            _mesonesLookup[mesonId] = nombre;

            _nuevoMesonNombre = "";
            _nuevoMesonImagenUrl = "";
            _nuevoMesonNiveles = null;
            _nuevoMesonMsg = "Mesón registrado (recuerda guardar).";
            StateHasChanged();
        }

        private void CrearInstalacionRegistro()
        {
            if (_area is null || _canvas is null) return;

            var nombre = (_nuevoIns_Nombre ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nombre))
            {
                _nuevoIns_Msg = "Ingresa un nombre.";
                return;
            }

            var id = $"ins_{Guid.NewGuid():N}".Substring(0, 12);

            _instalaciones[id] = new Instalacion
            {
                instalacion_id = id,
                nombre = nombre,
                subcategoria_id = string.IsNullOrWhiteSpace(_nuevoIns_SubcategoriaId) ? null : _nuevoIns_SubcategoriaId,
                laboratorio_id = _laboratorioId,
                area_id = _area.AreaId,
                canvas_id = _canvas.canvas_id,
                estado_id = string.IsNullOrWhiteSpace(_nuevoIns_EstadoId) ? null : _nuevoIns_EstadoId,
                fecha_instalacion = ParseDate(_nuevoIns_FechaInstalacion),
                fecha_ultima_revision = ParseDate(_nuevoIns_FechaUltRev),
                fecha_proxima_revision = ParseDate(_nuevoIns_FechaProxRev),
                proveedor_servicio = string.IsNullOrWhiteSpace(_nuevoIns_ProveedorServicio) ? null : _nuevoIns_ProveedorServicio,
                observaciones = string.IsNullOrWhiteSpace(_nuevoIns_Observaciones) ? null : _nuevoIns_Observaciones,
                descripcion = string.IsNullOrWhiteSpace(_nuevoIns_Descripcion) ? null : _nuevoIns_Descripcion,
                imagen_url = string.IsNullOrWhiteSpace(_nuevoIns_ImagenUrl) ? null : _nuevoIns_ImagenUrl
            };

            _nuevoIns_Nombre = "";
            _nuevoIns_SubcategoriaId = "";
            _nuevoIns_EstadoId = "";
            _nuevoIns_FechaInstalacion = "";
            _nuevoIns_FechaUltRev = "";
            _nuevoIns_FechaProxRev = "";
            _nuevoIns_ProveedorServicio = "";
            _nuevoIns_Observaciones = "";
            _nuevoIns_Descripcion = "";
            _nuevoIns_ImagenUrl = "";
            _nuevoIns_Msg = "Instalación registrada (recuerda guardar).";
            StateHasChanged();
        }

        // =========================
        // Bloques: disponibles + update XOR
        // =========================
        private IEnumerable<KeyValuePair<string, string>> MesonesDisponiblesParaBloque(string? bloqueId)
        {
            var usados = _blocks
                .Where(b => !string.Equals(b.bloque_id, bloqueId, StringComparison.OrdinalIgnoreCase))
                .Where(b => !string.IsNullOrWhiteSpace(b.meson_id))
                .Select(b => b.meson_id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _mesonesLookup.Where(kv => !usados.Contains(kv.Key));
        }

        private IEnumerable<KeyValuePair<string, string>> InstalacionesDisponiblesParaBloque(string? bloqueId)
        {
            var usados = _blocks
                .Where(b => !string.Equals(b.bloque_id, bloqueId, StringComparison.OrdinalIgnoreCase))
                .Where(b => !string.IsNullOrWhiteSpace(b.instalacion_id))
                .Select(b => b.instalacion_id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _instalaciones
                .Where(kv => !usados.Contains(kv.Key))
                .OrderBy(kv => kv.Value.nombre)
                .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.nombre));
        }

        private void ToggleNewBlockAssignInstalacion(bool v)
        {
            _newBlockAssignInstalacion = v;
            if (v)
            {
                _newBlockAssignMeson = false;
                _newBlockMesonId = null;
            }
            if (!v) _newBlockInstalacionId = null;
        }

        private void ToggleNewBlockAssignMeson(bool v)
        {
            _newBlockAssignMeson = v;
            if (v)
            {
                _newBlockAssignInstalacion = false;
                _newBlockInstalacionId = null;
            }
            if (!v) _newBlockMesonId = null;
        }

        private void OnSelectBlockInstalacion(string? value)
        {
            _newBlockInstalacionId = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_newBlockInstalacionId is not null)
            {
                _newBlockMesonId = null;
                _newBlockAssignInstalacion = true;
                _newBlockAssignMeson = false;
            }
        }

        private void OnSelectBlockMeson(string? value)
        {
            _newBlockMesonId = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_newBlockMesonId is not null)
            {
                _newBlockInstalacionId = null;
                _newBlockAssignMeson = true;
                _newBlockAssignInstalacion = false;
            }
        }

        private void UpdateBlockInstalacion(BlockItem block, string? value)
        {
            var selected = string.IsNullOrWhiteSpace(value) ? null : value;

            if (!string.IsNullOrWhiteSpace(selected) &&
                _blocks.Any(b => !string.Equals(b.bloque_id, block.bloque_id, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(b.instalacion_id, selected, StringComparison.OrdinalIgnoreCase)))
            {
                _blockMsg = "Esa instalación ya tiene un bloque asociado.";
                return;
            }

            block.instalacion_id = selected;
            if (selected is not null) block.meson_id = null;
            _blockMsg = null;
        }

        private void UpdateBlockMeson(BlockItem block, string? value)
        {
            var selected = string.IsNullOrWhiteSpace(value) ? null : value;

            if (!string.IsNullOrWhiteSpace(selected) &&
                _blocks.Any(b => !string.Equals(b.bloque_id, block.bloque_id, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(b.meson_id, selected, StringComparison.OrdinalIgnoreCase)))
            {
                _blockMsg = "Ese mesón ya tiene un bloque asociado.";
                return;
            }

            block.meson_id = selected;
            if (selected is not null) block.instalacion_id = null;
            _blockMsg = null;
        }

        private void MostrarNuevoBloque()
        {
            _showBlockCreator = true;
            _selBlock = null;
            _selIn = null;
            _blockMsg = null;

            _newBlockAssignInstalacion = false;
            _newBlockAssignMeson = false;
            _newBlockInstalacionId = null;
            _newBlockMesonId = null;
        }

        private void AgregarBloque()
        {
            if (_canvas is null || _area is null)
            {
                _blockMsg = "No hay canvas/área activa.";
                return;
            }

            if (_newBlockAssignInstalacion && _newBlockAssignMeson)
            {
                _blockMsg = "Selecciona solo una opción (instalación o mesón).";
                return;
            }

            if (!_newBlockAssignInstalacion && !_newBlockAssignMeson)
            {
                _blockMsg = "Selecciona si el bloque se asocia a una instalación o a un mesón.";
                return;
            }

            if (_newBlockAssignInstalacion && string.IsNullOrWhiteSpace(_newBlockInstalacionId))
            {
                _blockMsg = "Selecciona una instalación.";
                return;
            }

            if (_newBlockAssignMeson && string.IsNullOrWhiteSpace(_newBlockMesonId))
            {
                _blockMsg = "Selecciona un mesón.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_newBlockInstalacionId) &&
                _blocks.Any(b => string.Equals(b.instalacion_id, _newBlockInstalacionId, StringComparison.OrdinalIgnoreCase)))
            {
                _blockMsg = "Esa instalación ya tiene un bloque asociado.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_newBlockMesonId) &&
                _blocks.Any(b => string.Equals(b.meson_id, _newBlockMesonId, StringComparison.OrdinalIgnoreCase)))
            {
                _blockMsg = "Ese mesón ya tiene un bloque asociado.";
                return;
            }

            var it = new BlockItem
            {
                bloque_id = $"block_{Guid.NewGuid():N}".Substring(0, 12),
                canvas_id = _canvas.canvas_id,
                instalacion_id = _newBlockAssignInstalacion ? _newBlockInstalacionId : null,
                meson_id = _newBlockAssignMeson ? _newBlockMesonId : null,
                etiqueta = string.IsNullOrWhiteSpace(_newBlockEtiqueta) ? null : _newBlockEtiqueta.Trim(),
                color_hex = string.IsNullOrWhiteSpace(_newBlockColor) ? "#2563eb" : _newBlockColor,
                z_order = _blocks.Count == 0 ? 0 : _blocks.Max(b => b.z_order) + 1,
                ancho = Clamp(EPS_MINW, 10m, _newBlockAncho),
                largo = Clamp(EPS_MINW, 10m, _newBlockLargo),
                altura = _newBlockAltura, // ✅ NUEVO
                offset_x = _newBlockOffsetX,
                offset_y = _newBlockOffsetY
            };

            UpdateBlockAbs(it);

            // clamp contorno real
            var (nx, ny) = ClampRectInAreaUnion(_area, it.abs_x, it.abs_y, it.ancho, it.largo);
            it.abs_x = nx;
            it.abs_y = ny;
            it.offset_x = it.abs_x - AreaCenterX;
            it.offset_y = it.abs_y - AreaCenterY;
            it.pos_x = it.abs_x;
            it.pos_y = it.abs_y;
            _blocks.Add(it);
            _blocksById[it.bloque_id] = it;

            

            _showBlockCreator = false;
            _selBlock = it.bloque_id;
            _selIn = null;

            _blockMsg = "Bloque agregado (recuerda guardar).";
        }

        private void UpdateBlockAbs(BlockItem it)
        {
            it.abs_x = AreaCenterX + it.offset_x;
            it.abs_y = AreaCenterY + it.offset_y;
            it.pos_x = it.abs_x;
            it.pos_y = it.abs_y;
        }

        // =========================
        // RenderFragments (handles)
        // =========================
        private RenderFragment CornerHandle(InnerItem it, decimal lx, decimal ly, Handle h) => builder =>
        {
            var size = 0.30m;
            var x = lx - size / 2m;
            var y = ly - size / 2m;

            var cursor = h switch
            {
                Handle.NW => "nwse-resize",
                Handle.SE => "nwse-resize",
                Handle.NE => "nesw-resize",
                Handle.SW => "nesw-resize",
                _ => "nwse-resize"
            };

            int seq = 0;
            builder.OpenElement(seq++, "rect");
            builder.AddAttribute(seq++, "x", S(x));
            builder.AddAttribute(seq++, "y", S(y));
            builder.AddAttribute(seq++, "width", S(size));
            builder.AddAttribute(seq++, "height", S(size));
            builder.AddAttribute(seq++, "fill", "#13a076");
            builder.AddAttribute(seq++, "stroke", "#0b6b50");
            builder.AddAttribute(seq++, "stroke-width", S(0.03m));
            builder.AddAttribute(seq++, "style", $"cursor:{cursor}");
            builder.AddAttribute(seq++, "onpointerdown:preventDefault", true);
            builder.AddAttribute(seq++, "onpointerdown:stopPropagation", true);
            builder.AddAttribute(seq++, "onpointerdown",
                EventCallback.Factory.Create<PointerEventArgs>(this, (PointerEventArgs e) => OnPointerDownResizeInner(e, it.poly_in_id, h)));
            builder.CloseElement();
        };

        private RenderFragment BlockCornerHandle(BlockItem it, decimal x, decimal y, Handle h) => builder =>
        {
            var size = 0.30m;
            var rx = x - size / 2m;
            var ry = y - size / 2m;

            var cursor = h switch
            {
                Handle.NW => "nwse-resize",
                Handle.SE => "nwse-resize",
                Handle.NE => "nesw-resize",
                Handle.SW => "nesw-resize",
                _ => "nwse-resize"
            };

            int seq = 0;
            builder.OpenElement(seq++, "rect");
            builder.AddAttribute(seq++, "x", S(rx));
            builder.AddAttribute(seq++, "y", S(ry));
            builder.AddAttribute(seq++, "width", S(size));
            builder.AddAttribute(seq++, "height", S(size));
            builder.AddAttribute(seq++, "fill", "#2563eb");
            builder.AddAttribute(seq++, "stroke", "#1e3a8a");
            builder.AddAttribute(seq++, "stroke-width", S(0.03m));
            builder.AddAttribute(seq++, "style", $"cursor:{cursor}");
            builder.AddAttribute(seq++, "onpointerdown:preventDefault", true);
            builder.AddAttribute(seq++, "onpointerdown:stopPropagation", true);
            builder.AddAttribute(seq++, "onpointerdown",
                EventCallback.Factory.Create<PointerEventArgs>(this, (PointerEventArgs e) => OnPointerDownResizeBlock(e, it.bloque_id, h)));
            builder.CloseElement();
        };

        // =========================
        // Pointer: iniciar drag
        // =========================
        private void OnPointerDownMoveInner(PointerEventArgs e, string id)
        {
            if (_area is null) return;
            if (!_mapIn.TryGetValue(id, out var it)) return;

            _selIn = id;
            _selBlock = null;
            _showBlockCreator = false;

            _dragIn = it;
            _dragParent = _area.Polys.First(p => string.Equals(p.poly_id, it.area_poly_id, StringComparison.OrdinalIgnoreCase));
            _activeHandle = Handle.None;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);
            _beforeDragIn = (it.abs_x, it.abs_y, it.ancho_m, it.largo_m);
            _grab = (wx - it.abs_x, wy - it.abs_y);

            StateHasChanged();
        }

        private void OnPointerDownResizeInner(PointerEventArgs e, string id, Handle h)
        {
            if (_area is null) return;
            if (!_mapIn.TryGetValue(id, out var it)) return;

            _selIn = id;
            _selBlock = null;
            _showBlockCreator = false;

            _dragIn = it;
            _dragParent = _area.Polys.First(p => string.Equals(p.poly_id, it.area_poly_id, StringComparison.OrdinalIgnoreCase));
            _activeHandle = h;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);
            _beforeDragIn = (it.abs_x, it.abs_y, it.ancho_m, it.largo_m);
            _grab = null;

            StateHasChanged();
        }

        private void OnPointerDownMoveBlock(PointerEventArgs e, string id)
        {
            if (_area is null) return;
            if (!_blocksById.TryGetValue(id, out var b)) return;

            _selBlock = id;
            _selIn = null;
            _showBlockCreator = false;

            _dragBlock = b;
            _blockHandle = Handle.None;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);
            _beforeDragBlock = (b.abs_x, b.abs_y, b.ancho, b.largo);
            _blockGrab = (wx - b.abs_x, wy - b.abs_y);

            _blockMsg = null;
            StateHasChanged();
        }

        private void OnPointerDownResizeBlock(PointerEventArgs e, string id, Handle h)
        {
            if (_area is null) return;
            if (!_blocksById.TryGetValue(id, out var b)) return;

            _selBlock = id;
            _selIn = null;
            _showBlockCreator = false;

            _dragBlock = b;
            _blockHandle = h;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);
            _beforeDragBlock = (b.abs_x, b.abs_y, b.ancho, b.largo);
            _blockGrab = null;

            _blockMsg = null;
            StateHasChanged();
        }

        private void OnPointerDownBackground(PointerEventArgs e)
        {
            _selIn = null;
            _selBlock = null;
            _showBlockCreator = false;
            BeginPan(e);
        }

        private void BeginPan(PointerEventArgs e)
        {
            _panStart = (e.OffsetX, e.OffsetY);
            _panMoved = false;
        }

        // =========================
        // Pointer: move (PAN / DRAG / RESIZE)
        // =========================
        private void OnPointerMove(PointerEventArgs e)
        {
            // ===== Pan =====
            if (_panStart is not null && _dragIn is null && _dragBlock is null)
            {
                var (sx, sy) = _panStart.Value;
                var dxPx = e.OffsetX - sx;
                var dyPx = e.OffsetY - sy;

                if (!_panMoved && (Math.Abs(dxPx) > 3 || Math.Abs(dyPx) > 3)) _panMoved = true;
                _panStart = (e.OffsetX, e.OffsetY);

                var metersX = (decimal)(dxPx / (PxPerM() * _zoom));
                var metersY = (decimal)(dyPx / (PxPerM() * _zoom));

                var vw = (decimal)((double)Wm / _zoom);
                var vh = (decimal)((double)Hm / _zoom);

                _panX = Clamp(0m, Math.Max(0m, Wm - vw), _panX - metersX);
                _panY = Clamp(0m, Math.Max(0m, Hm - vh), _panY - metersY);

                UpdateViewMetrics();
                StateHasChanged();
                return;
            }

            // ===== Drag/Resize BLOQUE =====
            if (_dragBlock is not null && _dragStart is not null && _beforeDragBlock is not null && _area is not null)
            {
                var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
                var dx = wx - _dragStart.Value.x;
                var dy = wy - _dragStart.Value.y;

                var blockBaseX = _beforeDragBlock.Value.x;
                var blockBaseY = _beforeDragBlock.Value.y;
                var blockBaseW = _beforeDragBlock.Value.w;
                var blockBaseH = _beforeDragBlock.Value.h;

                decimal blockPropX = blockBaseX;
                decimal blockPropY = blockBaseY;
                decimal blockPropW = blockBaseW;
                decimal blockPropH = blockBaseH;

                if (_blockHandle == Handle.None)
                {
                    if (_blockGrab is not null)
                    {
                        blockPropX = wx - _blockGrab.Value.dx;
                        blockPropY = wy - _blockGrab.Value.dy;
                    }
                    else
                    {
                        blockPropX = blockBaseX + dx;
                        blockPropY = blockBaseY + dy;
                    }
                }
                else
                {
                    switch (_blockHandle)
                    {
                        case Handle.NE:
                            {
                                var bottom = blockBaseY + blockBaseH;
                                blockPropW = Math.Max(EPS_MINW, blockBaseW + dx);
                                blockPropH = Math.Max(EPS_MINW, blockBaseH - dy);
                                blockPropX = blockBaseX;
                                blockPropY = bottom - blockPropH;
                                break;
                            }
                        case Handle.SE:
                            {
                                blockPropW = Math.Max(EPS_MINW, blockBaseW + dx);
                                blockPropH = Math.Max(EPS_MINW, blockBaseH + dy);
                                blockPropX = blockBaseX;
                                blockPropY = blockBaseY;
                                break;
                            }
                        case Handle.NW:
                            {
                                var right = blockBaseX + blockBaseW;
                                var bottom = blockBaseY + blockBaseH;
                                blockPropW = Math.Max(EPS_MINW, blockBaseW - dx);
                                blockPropH = Math.Max(EPS_MINW, blockBaseH - dy);
                                blockPropX = right - blockPropW;
                                blockPropY = bottom - blockPropH;
                                break;
                            }
                        case Handle.SW:
                            {
                                var right = blockBaseX + blockBaseW;
                                blockPropW = Math.Max(EPS_MINW, blockBaseW - dx);
                                blockPropH = Math.Max(EPS_MINW, blockBaseH + dy);
                                blockPropX = right - blockPropW;
                                blockPropY = blockBaseY;
                                break;
                            }
                    }
                }

                var (clampedX, clampedY) = ClampRectInAreaUnion(_area, blockPropX, blockPropY, blockPropW, blockPropH);

                _dragBlock.ancho = blockPropW;
                _dragBlock.largo = blockPropH;
                _dragBlock.abs_x = clampedX;
                _dragBlock.abs_y = clampedY;

                _dragBlock.offset_x = clampedX - AreaCenterX;
                _dragBlock.offset_y = clampedY - AreaCenterY;

                _dragBlock.pos_x = _dragBlock.abs_x;
                _dragBlock.pos_y = _dragBlock.abs_y;

                
                StateHasChanged();
                return;

            }

            // ===== Drag/Resize INNER =====
            if (_dragIn is null || _dragParent is null || _dragStart is null || _beforeDragIn is null || _area is null)
                return;

            var (wxIn, wyIn) = ScreenToWorld(e.OffsetX, e.OffsetY);
            var dxIn = wxIn - _dragStart.Value.x;
            var dyIn = wyIn - _dragStart.Value.y;

            var innerBaseAbsX = _beforeDragIn.Value.x;
            var innerBaseAbsY = _beforeDragIn.Value.y;
            var innerBaseW = _beforeDragIn.Value.w;
            var innerBaseH = _beforeDragIn.Value.h;

            decimal innerPropAbsX = innerBaseAbsX;
            decimal innerPropAbsY = innerBaseAbsY;
            decimal innerPropW = innerBaseW;
            decimal innerPropH = innerBaseH;

            if (_activeHandle == Handle.None)
            {
                if (_grab is not null)
                {
                    innerPropAbsX = wxIn - _grab.Value.dx;
                    innerPropAbsY = wyIn - _grab.Value.dy;
                }
                else
                {
                    innerPropAbsX = innerBaseAbsX + dxIn;
                    innerPropAbsY = innerBaseAbsY + dyIn;
                }
            }
            else
            {
                switch (_activeHandle)
                {
                    case Handle.NE:
                        {
                            var bottom = innerBaseAbsY + innerBaseH;
                            innerPropW = Math.Max(EPS_MINW, innerBaseW + dxIn);
                            innerPropH = Math.Max(EPS_MINW, innerBaseH - dyIn);
                            innerPropAbsX = innerBaseAbsX;
                            innerPropAbsY = bottom - innerPropH;
                            break;
                        }
                    case Handle.SE:
                        {
                            innerPropW = Math.Max(EPS_MINW, innerBaseW + dxIn);
                            innerPropH = Math.Max(EPS_MINW, innerBaseH + dyIn);
                            innerPropAbsX = innerBaseAbsX;
                            innerPropAbsY = innerBaseAbsY;
                            break;
                        }
                    case Handle.NW:
                        {
                            var right = innerBaseAbsX + innerBaseW;
                            var bottom = innerBaseAbsY + innerBaseH;
                            innerPropW = Math.Max(EPS_MINW, innerBaseW - dxIn);
                            innerPropH = Math.Max(EPS_MINW, innerBaseH - dyIn);
                            innerPropAbsX = right - innerPropW;
                            innerPropAbsY = bottom - innerPropH;
                            break;
                        }
                    case Handle.SW:
                        {
                            var right = innerBaseAbsX + innerBaseW;
                            innerPropW = Math.Max(EPS_MINW, innerBaseW - dxIn);
                            innerPropH = Math.Max(EPS_MINW, innerBaseH + dyIn);
                            innerPropAbsX = right - innerPropW;
                            innerPropAbsY = innerBaseAbsY;
                            break;
                        }
                }
            }

            var (cx2, cy2, newParent) = ClampInnerToAreaAndCollisions(_dragIn, _dragParent, innerPropAbsX, innerPropAbsY, innerPropW, innerPropH);

            _dragIn.area_poly_id = newParent.poly_id;
            _dragIn.abs_x = cx2;
            _dragIn.abs_y = cy2;
            _dragIn.ancho_m = innerPropW;
            _dragIn.largo_m = innerPropH;

            _dragIn.eje_x_rel_m = Math.Round(_dragIn.abs_x - newParent.x_m, 3, MidpointRounding.AwayFromZero);
            _dragIn.eje_y_rel_m = Math.Round(_dragIn.abs_y - newParent.y_m, 3, MidpointRounding.AwayFromZero);

            _dragParent = newParent;

            StateHasChanged();
        }


        private void OnPointerUp(PointerEventArgs e)
        {
            var finishingInner = _dragIn;
            var finishingParent = _dragParent;
            var finishingBlock = _dragBlock;

            _dragStart = null;
            _dragIn = null;
            _dragParent = null;
            _beforeDragIn = null;
            _grab = null;
            _activeHandle = Handle.None;

            _dragBlock = null;
            _beforeDragBlock = null;
            _blockGrab = null;
            _blockHandle = Handle.None;

            if (_panStart is not null && !_panMoved)
            {
                _selIn = null;
                _selBlock = null;
                _showBlockCreator = false;
            }
            _panStart = null;

            // harden rel coords al soltar
            if (finishingInner is not null && finishingParent is not null)
            {
                finishingInner.eje_x_rel_m = Math.Round(finishingInner.abs_x - finishingParent.x_m, 3, MidpointRounding.AwayFromZero);
                finishingInner.eje_y_rel_m = Math.Round(finishingInner.abs_y - finishingParent.y_m, 3, MidpointRounding.AwayFromZero);
            }

            if (finishingBlock is not null)
            {
                finishingBlock.offset_x = finishingBlock.abs_x - AreaCenterX;
                finishingBlock.offset_y = finishingBlock.abs_y - AreaCenterY;
                finishingBlock.pos_x = finishingBlock.abs_x;
                finishingBlock.pos_y = finishingBlock.abs_y;
            }

            StateHasChanged();
        }

        // =========================
        // Wheel / zoom
        // =========================
        private void OnWheel(WheelEventArgs e)
        {
            var f = Math.Sign(e.DeltaY) < 0 ? 1.1 : (1 / 1.1);
            _zoom = Math.Clamp(_zoom * f, 0.3, 6.0);
            ClampPanToBounds();
            UpdateViewMetrics();
            StateHasChanged();
        }

        private void ZoomOut()
        {
            _zoom = Math.Clamp(_zoom / 1.1, 0.3, 6.0);
            ClampPanToBounds();
            UpdateViewMetrics();
            StateHasChanged();
        }

        private void ZoomIn()
        {
            _zoom = Math.Clamp(_zoom * 1.1, 0.3, 6.0);
            ClampPanToBounds();
            UpdateViewMetrics();
            StateHasChanged();
        }

        private void CenterView()
        {
            ClampPanToBounds();
            UpdateViewMetrics();
            StateHasChanged();
        }

        private void UpdateViewMetrics()
        {
            VW = (decimal)((double)Wm / _zoom);
            VH = (decimal)((double)Hm / _zoom);

            VX = _panX;
            VY = _panY;

            CacheGrid();
        }

        private void CacheGrid()
        {
            var vw = (decimal)((double)Wm / _zoom);
            var vh = (decimal)((double)Hm / _zoom);

            GridStartX = (decimal)Math.Floor((double)_panX);
            GridEndX = (decimal)Math.Ceiling((double)(_panX + vw));
            GridStartY = (decimal)Math.Floor((double)_panY);
            GridEndY = (decimal)Math.Ceiling((double)(_panY + vh));
        }

        private void ClampPanToBounds()
        {
            var vw = (decimal)((double)Wm / _zoom);
            var vh = (decimal)((double)Hm / _zoom);
            _panX = Clamp(0m, Math.Max(0m, Wm - vw), _panX);
            _panY = Clamp(0m, Math.Max(0m, Hm - vh), _panY);
        }

        // =========================
        // Guardar (DB)
        // =========================
        private async Task Guardar()
        {
            try
            {
                if (_area is null || _canvas is null)
                {
                    _saveMsg = "No hay área/canvas.";
                    return;
                }

                _saving = true;
                _saveMsg = "Guardando…";
                StateHasChanged();

                // 1) MESONES
                if (_mesones.Count > 0)
                {
                    // asegurar nombres únicos por área
                    foreach (var g in _mesones.Values.GroupBy(m => m.area_id, StringComparer.OrdinalIgnoreCase))
                    {
                        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in g)
                        {
                            if (string.IsNullOrWhiteSpace(m.nombre_meson) || !vistos.Add(m.nombre_meson))
                                m.nombre_meson = PickNombreUnico(g.Key, _mesones.Values);
                        }
                    }

                    Pg.UseSheet("mesones");
                    foreach (var m in _mesones.Values)
                    {
                        var toSave = new Dictionary<string, object>
                        {
                            ["area_id"] = m.area_id,
                            ["nombre_meson"] = m.nombre_meson,
                            ["niveles_totales"] = m.niveles_totales.HasValue ? m.niveles_totales.Value : (object)DBNull.Value,
                            ["laboratorio_id"] = _laboratorioId,
                            ["imagen_url"] = string.IsNullOrWhiteSpace(m.imagen_url) ? (object)DBNull.Value : m.imagen_url! // ✅ NUEVO
                        };


                        var ok = await Pg.UpdateByIdAsync("meson_id", m.meson_id, toSave);
                        if (!ok)
                        {
                            toSave["meson_id"] = m.meson_id;
                            await Pg.CreateAsync(toSave);
                        }

                        _mesonesLookup[m.meson_id] = m.nombre_meson;
                    }
                }

                // 2) INSTALACIONES
                if (_instalaciones.Count > 0)
                {
                    Pg.UseSheet("instalaciones");
                    foreach (var ins in _instalaciones.Values)
                    {
                        var toSave = new Dictionary<string, object>
                        {
                            ["nombre"] = ins.nombre,

                            ["subcategoria_id"] = string.IsNullOrWhiteSpace(ins.subcategoria_id) ? (object)DBNull.Value : ins.subcategoria_id!,
                            ["laboratorio_id"] = _laboratorioId,
                            ["area_id"] = string.IsNullOrWhiteSpace(ins.area_id) ? (object)DBNull.Value : ins.area_id!,
                            ["canvas_id"] = string.IsNullOrWhiteSpace(ins.canvas_id) ? (object)DBNull.Value : ins.canvas_id!,


                            ["estado_id"] = string.IsNullOrWhiteSpace(ins.estado_id) ? (object)DBNull.Value : ins.estado_id!,
                            ["fecha_instalacion"] = ins.fecha_instalacion.HasValue ? ins.fecha_instalacion.Value.Date : (object)DBNull.Value,
                            ["fecha_ultima_revision"] = ins.fecha_ultima_revision.HasValue ? ins.fecha_ultima_revision.Value.Date : (object)DBNull.Value,
                            ["fecha_proxima_revision"] = ins.fecha_proxima_revision.HasValue ? ins.fecha_proxima_revision.Value.Date : (object)DBNull.Value,

                            ["proveedor_servicio"] = string.IsNullOrWhiteSpace(ins.proveedor_servicio) ? (object)DBNull.Value : ins.proveedor_servicio!,
                            ["observaciones"] = string.IsNullOrWhiteSpace(ins.observaciones) ? (object)DBNull.Value : ins.observaciones!,
                            ["descripcion"] = string.IsNullOrWhiteSpace(ins.descripcion) ? (object)DBNull.Value : ins.descripcion!,
                            ["imagen_url"] = string.IsNullOrWhiteSpace(ins.imagen_url) ? (object)DBNull.Value : ins.imagen_url!
                        };

                        var ok = await Pg.UpdateByIdAsync("instalacion_id", ins.instalacion_id, toSave);
                        if (!ok)
                        {
                            toSave["instalacion_id"] = ins.instalacion_id;
                            await Pg.CreateAsync(toSave);
                        }
                    }
                }

                // 3) POLIGONOS_INTERIORES
                Pg.UseSheet("poligonos_interiores");
                foreach (var it in _inners)
                {
                    // XOR fuerte al guardar
                    if (!string.IsNullOrWhiteSpace(it.meson_id))
                        it.instalacion_id = null;
                    else if (!string.IsNullOrWhiteSpace(it.instalacion_id))
                        it.meson_id = null;

                    var parent = _area.Polys.First(p => string.Equals(p.poly_id, it.area_poly_id, StringComparison.OrdinalIgnoreCase));

                    // clamp final (contorno real + colisiones)
                    var (cx, cy, newParent) = ClampInnerToAreaAndCollisions(it, parent, it.abs_x, it.abs_y, it.ancho_m, it.largo_m);
                    it.area_poly_id = newParent.poly_id;
                    it.abs_x = cx;
                    it.abs_y = cy;
                    it.eje_x_rel_m = Math.Round(it.abs_x - newParent.x_m, 3, MidpointRounding.AwayFromZero);
                    it.eje_y_rel_m = Math.Round(it.abs_y - newParent.y_m, 3, MidpointRounding.AwayFromZero);

                    var toSave = new Dictionary<string, object>
                    {
                        ["area_poly_id"] = it.area_poly_id,
                        ["canvas_id"] = it.canvas_id,
                        ["area_id"] = it.area_id,

                        ["eje_x_rel_m"] = it.eje_x_rel_m,
                        ["eje_y_rel_m"] = it.eje_y_rel_m,

                        ["ancho_m"] = it.ancho_m,
                        ["largo_m"] = it.largo_m,

                        ["z_order"] = it.z_order,
                        ["etiqueta"] = string.IsNullOrWhiteSpace(it.label) ? (object)DBNull.Value : it.label!,
                        ["color_hex"] = it.fill,
                        ["opacidad_0_1"] = it.opacidad,

                        ["meson_id"] = string.IsNullOrWhiteSpace(it.meson_id) ? (object)DBNull.Value : it.meson_id!,
                        ["instalacion_id"] = string.IsNullOrWhiteSpace(it.instalacion_id) ? (object)DBNull.Value : it.instalacion_id!
                    };

                    var ok = await Pg.UpdateByIdAsync("poly_in_id", it.poly_in_id, toSave);
                    if (!ok)
                    {
                        toSave["poly_in_id"] = it.poly_in_id;
                        await Pg.CreateAsync(toSave);
                    }
                }

                // 4) BLOQUES
                Pg.UseSheet("bloques_int");
                foreach (var b in _blocks)
                {
                    // XOR fuerte al guardar
                    if (!string.IsNullOrWhiteSpace(b.meson_id))
                        b.instalacion_id = null;
                    else if (!string.IsNullOrWhiteSpace(b.instalacion_id))
                        b.meson_id = null;

                    UpdateBlockAbs(b);

                    // clamp contorno real
                    var (bx, by) = ClampRectInAreaUnion(_area, b.abs_x, b.abs_y, b.ancho, b.largo);
                    b.abs_x = bx;
                    b.abs_y = by;
                    b.offset_x = b.abs_x - AreaCenterX;
                    b.offset_y = b.abs_y - AreaCenterY;
                    b.pos_x = b.abs_x;
                    b.pos_y = b.abs_y;

                    var toSave = new Dictionary<string, object>
                    {
                        ["canvas_id"] = b.canvas_id,
                        ["instalacion_id"] = string.IsNullOrWhiteSpace(b.instalacion_id) ? (object)DBNull.Value : b.instalacion_id!,
                        ["meson_id"] = string.IsNullOrWhiteSpace(b.meson_id) ? (object)DBNull.Value : b.meson_id!,
                        ["etiqueta"] = string.IsNullOrWhiteSpace(b.etiqueta) ? (object)DBNull.Value : b.etiqueta!,
                        ["color_hex"] = string.IsNullOrWhiteSpace(b.color_hex) ? (object)DBNull.Value : b.color_hex!,
                        ["z_order"] = b.z_order,
                        ["pos_x"] = b.pos_x,
                        ["pos_y"] = b.pos_y,
                        ["ancho"] = b.ancho,
                        ["largo"] = b.largo,
                        ["altura"] = b.altura.HasValue ? b.altura.Value : (object)DBNull.Value,
                        ["offset_x"] = b.offset_x,
                        ["offset_y"] = b.offset_y
                    };

                    var ok = await Pg.UpdateByIdAsync("bloque_id", b.bloque_id, toSave);
                    if (!ok)
                    {
                        toSave["bloque_id"] = b.bloque_id;
                        await Pg.CreateAsync(toSave);
                    }
                }

                _saveMsg = "Guardado ✔";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ModificarDetallesArea.Guardar] {ex}");
                _saveMsg = "Error al guardar (ver consola).";
            }
            finally
            {
                _saving = false;
                StateHasChanged();
            }
        }

        // =========================
        // Eliminar
        // =========================
        private async Task EliminarInner()
        {
            if (_selIn is null) return;

            try
            {
                Pg.UseSheet("poligonos_interiores");
                await Pg.DeleteByIdAsync("poly_in_id", _selIn);
            }
            catch { }

            _inners.RemoveAll(i => string.Equals(i.poly_in_id, _selIn, StringComparison.OrdinalIgnoreCase));
            _mapIn.Remove(_selIn);
            _selIn = null;
            _saveMsg = "Elemento interior eliminado.";
            StateHasChanged();
        }

        private async Task EliminarBloque(string bloqueId)
        {
            if (string.IsNullOrWhiteSpace(bloqueId)) return;

            try
            {
                Pg.UseSheet("bloques_int");
                await Pg.DeleteByIdAsync("bloque_id", bloqueId);
            }
            catch { }

            _blocks.RemoveAll(b => string.Equals(b.bloque_id, bloqueId, StringComparison.OrdinalIgnoreCase));
            _blocksById.Remove(bloqueId);

            if (string.Equals(_selBlock, bloqueId, StringComparison.OrdinalIgnoreCase))
                _selBlock = null;

            _blockMsg = "Bloque eliminado.";
            StateHasChanged();
        }

        private async Task EliminarBloqueSeleccionado()
        {
            if (string.IsNullOrWhiteSpace(_selBlock)) return;
            await EliminarBloque(_selBlock);
        }

        // =========================
        // Colisiones + contorno real
        // =========================
        private InnerKind GetKind(InnerItem it)
        {
            if (!string.IsNullOrWhiteSpace(it.meson_id)) return InnerKind.Meson;
            if (!string.IsNullOrWhiteSpace(it.instalacion_id)) return InnerKind.Instalacion;
            return InnerKind.None;
        }

        private (decimal x, decimal y, Poly parent) ClampInnerToAreaAndCollisions(InnerItem it, Poly currentParent, decimal absX, decimal absY, decimal w, decimal h)
        {
            if (_area is null) return (absX, absY, currentParent);

            // 1) rehome: escoger el mejor poly por intersección de bbox
            var parent = BestPolyForRect(_area, absX, absY, w, h, currentParent);

            // 2) clamp al contorno real del parent (si es polígono real)
            var (cx, cy) = ClampRectToPolyShape(parent, absX, absY, w, h);

            // 3) colisiones (solo con el mismo tipo: mesón-mesón / instalación-instalación)
            var kind = GetKind(it);
            if (kind != InnerKind.None)
            {
                (cx, cy) = ResolveSameTypeCollisions(it.poly_in_id, kind, parent, cx, cy, w, h);
            }

            // 4) clamp final al contorno real (por si colisión lo movió)
            (cx, cy) = ClampRectToPolyShape(parent, cx, cy, w, h);

            return (cx, cy, parent);
        }

        private (decimal x, decimal y) ResolveSameTypeCollisions(string movingId, InnerKind kind, Poly parent, decimal x, decimal y, decimal w, decimal h)
        {
            // iteración simple: empuja afuera por mínima penetración
            for (int pass = 0; pass < 12; pass++)
            {
                var hit = FindFirstOverlapSameType(movingId, kind, x, y, w, h);
                if (hit is null) break;

                var (ox, oy, ow, oh) = hit.Value;

                var aL = x; var aT = y; var aR = x + w; var aB = y + h;
                var bL = ox; var bT = oy; var bR = ox + ow; var bB = oy + oh;

                var overlapX = Math.Min(aR, bR) - Math.Max(aL, bL);
                var overlapY = Math.Min(aB, bB) - Math.Max(aT, bT);

                if (overlapX <= 0m || overlapY <= 0m) break;

                var aCx = aL + w / 2m;
                var aCy = aT + h / 2m;
                var bCx = bL + ow / 2m;
                var bCy = bT + oh / 2m;

                if (overlapX < overlapY)
                {
                    // mover en X
                    if (aCx < bCx) x -= (overlapX + EPS_SEP);
                    else x += (overlapX + EPS_SEP);
                }
                else
                {
                    // mover en Y
                    if (aCy < bCy) y -= (overlapY + EPS_SEP);
                    else y += (overlapY + EPS_SEP);
                }

                // clamp dentro del parent (contorno real)
                (x, y) = ClampRectToPolyShape(parent, x, y, w, h);
            }

            return (x, y);
        }

        private (decimal x, decimal y, decimal w, decimal h)? FindFirstOverlapSameType(string movingId, InnerKind kind, decimal x, decimal y, decimal w, decimal h)
        {
            foreach (var other in _inners)
            {
                if (string.Equals(other.poly_in_id, movingId, StringComparison.OrdinalIgnoreCase)) continue;
                if (GetKind(other) != kind) continue;

                var ox = other.abs_x;
                var oy = other.abs_y;
                var ow = other.ancho_m;
                var oh = other.largo_m;

                if (RectsOverlap(x, y, w, h, ox, oy, ow, oh))
                    return (ox, oy, ow, oh);
            }
            return null;
        }

        private static bool RectsOverlap(decimal ax, decimal ay, decimal aw, decimal ah, decimal bx, decimal by, decimal bw, decimal bh)
        {
            return ax < (bx + bw) && (ax + aw) > bx && ay < (by + bh) && (ay + ah) > by;
        }

        private (decimal x, decimal y) ClampRectInAreaUnion(AreaDraw area, decimal x, decimal y, decimal w, decimal h)
        {
            // Estrategia:
            // 1) clamp al bbox del mejor poly (rápido)
            var best = BestPolyForRect(area, x, y, w, h, current: null);
            var (cx, cy) = ClampRectToPolyShape(best, x, y, w, h);

            // 2) Si por contorno real todavía no está dentro de la unión, intenta acercar al centro del poly
            if (!RectInsideAreaUnion(area, cx, cy, w, h))
            {
                var (tx, ty) = NudgeRectTowardsPolyInterior(best, cx, cy, w, h);
                cx = tx; cy = ty;
            }

            // 3) fallback: clamp bbox del área (para no volarse lejos)
            if (!RectInsideAreaUnion(area, cx, cy, w, h))
            {
                var minX = area.MinX;
                var minY = area.MinY;
                var maxX = area.MaxX - w;
                var maxY = area.MaxY - h;
                cx = Clamp(minX, maxX, cx);
                cy = Clamp(minY, maxY, cy);
            }

            return (cx, cy);
        }

        private (decimal x, decimal y) ClampRectToPolyShape(Poly p, decimal x, decimal y, decimal w, decimal h)
        {
            // clamp primero a bbox del poly
            var minX = p.x_m;
            var minY = p.y_m;
            var maxX = p.x_m + Math.Max(0m, p.ancho_m - w);
            var maxY = p.y_m + Math.Max(0m, p.largo_m - h);

            x = Clamp(minX, maxX, x);
            y = Clamp(minY, maxY, y);

            // si es polígono real, exigimos 4 esquinas dentro
            if (p.puntos.Count >= 3)
            {
                if (RectInsidePoly(p, x, y, w, h)) return (x, y);

                // nudges hacia el centro del polígono
                var (nx, ny) = NudgeRectTowardsPolyInterior(p, x, y, w, h);
                nx = Clamp(minX, maxX, nx);
                ny = Clamp(minY, maxY, ny);

                // si todavía no, último clamp bbox (ya está)
                return (nx, ny);
            }

            return (x, y);
        }

        private (decimal x, decimal y) NudgeRectTowardsPolyInterior(Poly p, decimal x, decimal y, decimal w, decimal h)
        {
            var (cxPoly, cyPoly) = PolyCentroidApprox(p);
            var step = 0.03m;

            var minX = p.x_m;
            var minY = p.y_m;
            var maxX = p.x_m + Math.Max(0m, p.ancho_m - w);
            var maxY = p.y_m + Math.Max(0m, p.largo_m - h);

            decimal curX = x, curY = y;

            for (int i = 0; i < 30; i++)
            {
                if (RectInsidePoly(p, curX, curY, w, h)) break;

                var rcx = curX + w / 2m;
                var rcy = curY + h / 2m;

                var dirX = Math.Sign(cxPoly - rcx);
                var dirY = Math.Sign(cyPoly - rcy);

                // intenta mover en el eje "más necesario"
                if (dirX != 0) curX += dirX * step;
                if (dirY != 0) curY += dirY * step;

                curX = Clamp(minX, maxX, curX);
                curY = Clamp(minY, maxY, curY);
            }

            return (curX, curY);
        }

        private static (decimal cx, decimal cy) PolyCentroidApprox(Poly p)
        {
            if (p.puntos.Count < 3)
            {
                return (p.x_m + p.ancho_m / 2m, p.y_m + p.largo_m / 2m);
            }

            // centroide simple promedio (suficiente para "nudge")
            decimal sx = 0m, sy = 0m;
            foreach (var pt in p.puntos) { sx += pt.X; sy += pt.Y; }
            return (sx / p.puntos.Count, sy / p.puntos.Count);
        }

        private bool RectInsideAreaUnion(AreaDraw area, decimal x, decimal y, decimal w, decimal h)
        {
            // 4 esquinas dentro de la unión
            return IsPointInsideAreaUnion(area, x + PAD_AREA, y + PAD_AREA)
                && IsPointInsideAreaUnion(area, x + w - PAD_AREA, y + PAD_AREA)
                && IsPointInsideAreaUnion(area, x + PAD_AREA, y + h - PAD_AREA)
                && IsPointInsideAreaUnion(area, x + w - PAD_AREA, y + h - PAD_AREA);
        }

        private bool IsPointInsideAreaUnion(AreaDraw area, decimal x, decimal y)
        {
            foreach (var p in area.Polys)
            {
                if (PointInsidePoly(p, x, y)) return true;
            }
            return false;
        }

        private static bool RectInsidePoly(Poly p, decimal x, decimal y, decimal w, decimal h)
        {
            // 4 esquinas dentro del poly
            return PointInsidePoly(p, x + PAD_AREA, y + PAD_AREA)
                && PointInsidePoly(p, x + w - PAD_AREA, y + PAD_AREA)
                && PointInsidePoly(p, x + PAD_AREA, y + h - PAD_AREA)
                && PointInsidePoly(p, x + w - PAD_AREA, y + h - PAD_AREA);
        }

        private static bool PointInsidePoly(Poly p, decimal x, decimal y)
        {
            if (p.puntos.Count < 3)
            {
                return x >= p.x_m && x <= p.x_m + p.ancho_m
                    && y >= p.y_m && y <= p.y_m + p.largo_m;
            }

            // Ray casting
            var pts = p.puntos;
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var xi = pts[i].X; var yi = pts[i].Y;
                var xj = pts[j].X; var yj = pts[j].Y;

                var intersect = ((yi > y) != (yj > y)) &&
                                (x < (xj - xi) * (y - yi) / ((yj - yi) == 0m ? 0.0000001m : (yj - yi)) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static Poly BestPolyForRect(AreaDraw area, decimal absX, decimal absY, decimal w, decimal h, Poly? current)
        {
            Poly best = current ?? area.Polys[0];
            decimal bestScore = -1m;

            var rect = (L: absX, T: absY, R: absX + w, B: absY + h);

            foreach (var p in area.Polys)
            {
                var pb = (L: p.x_m, T: p.y_m, R: p.x_m + p.ancho_m, B: p.y_m + p.largo_m);
                var inter = RectIntersectArea(pb, rect);

                // bias suave para mantener el current
                if (current is not null && string.Equals(p.poly_id, current.poly_id, StringComparison.OrdinalIgnoreCase))
                    inter *= 1.10m;

                if (inter > bestScore)
                {
                    bestScore = inter;
                    best = p;
                }
            }

            return best;
        }

        private static decimal RectIntersectArea(
            (decimal L, decimal T, decimal R, decimal B) a,
            (decimal L, decimal T, decimal R, decimal B) b)
        {
            var x1 = Math.Max(a.L, b.L);
            var y1 = Math.Max(a.T, b.T);
            var x2 = Math.Min(a.R, b.R);
            var y2 = Math.Min(a.B, b.B);
            var W = x2 - x1;
            var H = y2 - y1;
            return (W > 0m && H > 0m) ? (W * H) : 0m;
        }


        // =========================
        // Helpers geometría / texto
        // =========================
        private static decimal FitInnerText(InnerItem it)
        {
            var pad = 0.10m;
            var w = Math.Max(0.10m, it.ancho_m - 2 * pad);
            var h = Math.Max(0.10m, it.largo_m - 2 * pad);
            var len = Math.Max(1, it.label?.Length ?? 1);

            var fsByH = h * 0.60m;
            var fsByW = (decimal)w / (decimal)(0.65 * len);
            var fs = Math.Min(fsByH, fsByW);

            return Clamp(0.18m, 5m, fs);
        }

        private string PointsString(IEnumerable<Point> points)
            => string.Join(" ", points.Select(p => $"{S(p.X)},{S(p.Y)}"));

        private static List<Point> BuildRectPoints(decimal x, decimal y, decimal w, decimal h)
            => new()
            {
                new Point(x, y),
                new Point(x + w, y),
                new Point(x + w, y + h),
                new Point(x, y + h)
            };

        private static (decimal minX, decimal minY, decimal maxX, decimal maxY) BoundsOfPointList(IReadOnlyList<Point> points)
        {
            var minX = points.Min(pt => pt.X);
            var minY = points.Min(pt => pt.Y);
            var maxX = points.Max(pt => pt.X);
            var maxY = points.Max(pt => pt.Y);
            return (minX, minY, maxX, maxY);
        }

        private static void BuildAreaOutlineFromPoints(AreaDraw a)
        {
            a.Outline.Clear();
            foreach (var p in a.Polys)
            {
                var points = p.puntos.Count >= 3
                    ? p.puntos
                    : BuildRectPoints(p.x_m, p.y_m, p.ancho_m, p.largo_m);

                if (points.Count < 2) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var start = points[i];
                    var end = points[(i + 1) % points.Count];
                    if (start.X == end.X && start.Y == end.Y) continue;
                    a.Outline.Add((start.X, start.Y, end.X, end.Y));
                }
            }
        }

        // =========================
        // Helpers canvas conversion
        // =========================
        private double PxPerM()
        {
            const double nominalSvgPx = 1000.0;
            return nominalSvgPx / (double)Wm;
        }

        private (decimal x, decimal y) ScreenToWorld(double offsetX, double offsetY)
            => (_panX + (decimal)(offsetX / (PxPerM() * _zoom)),
                _panY + (decimal)(offsetY / (PxPerM() * _zoom)));

        // =========================
        // Helpers puertas/ventanas
        // =========================
        private static decimal DoorEndX(Door d) => d.x_m + ((d.orientacion is "E" or "W") ? d.largo_m : 0m);
        private static decimal DoorEndY(Door d) => d.y_m + ((d.orientacion is "N" or "S") ? d.largo_m : 0m);
        private static decimal WinEndX(Win w) => w.x_m + ((w.orientacion is "E" or "W") ? w.largo_m : 0m);
        private static decimal WinEndY(Win w) => w.y_m + ((w.orientacion is "N" or "S") ? w.largo_m : 0m);

        // =========================
        // Helpers DB / parsing
        // =========================
        private static decimal Clamp(decimal min, decimal max, decimal v) => Math.Max(min, Math.Min(max, v));
        private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static int Int(object? o) => int.TryParse(o?.ToString(), out var n) ? n : 0;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static string Get(Dictionary<string, string> d, string key, string fallback = "")
            => d.TryGetValue(key, out var v) ? (v ?? fallback) : fallback;

        private static decimal? ParseNullableDecimal(string? s)
        {
            s = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, out var d)) return d.Date;
            return null;
        }

        private static string DateToInput(DateTime? d)
    => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "";

        // ✅ Normaliza URL (http o ruta relativa tipo /uploads/...)
        private static string ResolveUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            return url.StartsWith("/", StringComparison.OrdinalIgnoreCase) ? url : $"/{url}";
        }

        private static bool Bool(object? value)
        {
            return value switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => false
            };
        }

        private static decimal ParseFlexible(object? v)
        {
            var s = (v?.ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0m;
            s = s.Replace(" ", "");
            if (s.Count(ch => ch == ',' || ch == '.') > 1) s = s.Replace(".", "").Replace(",", ".");
            else s = s.Replace(",", ".");
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        private async Task<Dictionary<string, List<Point>>> LoadPolyPointsAsync()
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

            return pointsByPoly.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(item => item.orden).Select(item => item.point).ToList(),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private async Task<string> ResolveAreaIdFromSlug(string slugFromUrl)
        {
            var slug = Slugify((slugFromUrl ?? "").Trim());
            var candidate = slug.Replace('-', '_');

            var nameLookup = await Pg.GetLookupAsync("areas", "area_id", "nombre_areas");
            foreach (var kv in nameLookup)
            {
                var nameSlug = Slugify(kv.Value).Replace('-', '_');
                if (string.Equals(nameSlug, candidate, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
            return candidate;
        }

        private async Task<string?> ResolveCanvasForArea(string areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId)) return null;

            try
            {
                Pg.UseSheet("areas");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "area_id"), areaId, StringComparison.OrdinalIgnoreCase)) continue;
                    return NullIfEmpty(Get(r, "canvas_id"));
                }
            }
            catch { }
            return null;
        }

        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "sin-area";
            var normalized = s.Trim().ToLowerInvariant();
            var formD = normalized.Normalize(NormalizationForm.FormD);

            var sb = new StringBuilder();
            foreach (var ch in formD)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }

            var cleaned = sb.ToString().Normalize(NormalizationForm.FormC);

            var sb2 = new StringBuilder(cleaned.Length);
            foreach (var ch in cleaned)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-') sb2.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '_' || ch == '/') sb2.Append('-');
            }

            var slug = sb2.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "sin-area" : slug;
        }

        private static string PickNombreUnico(string areaId, IEnumerable<Meson> existentes)
        {
            var usados = existentes
                .Where(m => string.Equals(m.area_id, areaId, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.nombre_meson)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            const string baseName = "MESON";
            if (!usados.Contains(baseName)) return baseName;

            for (int i = 2; i < 999; i++)
            {
                var cand = $"{baseName} {i:00}";
                if (!usados.Contains(cand)) return cand;
            }
            return $"{baseName}_{Guid.NewGuid():N}".Substring(0, 14);
        }

        // =========================
        // Fit view to area
        // =========================
        private void FitViewBoxToAreaWithAspect(AreaDraw a, decimal pad)
        {
            var minX = Math.Max(0m, a.MinX - pad);
            var minY = Math.Max(0m, a.MinY - pad);
            var maxX = Math.Min(Wm, a.MaxX + pad);
            var maxY = Math.Min(Hm, a.MaxY + pad);

            var bboxW = Math.Max(0.001m, maxX - minX);
            var bboxH = Math.Max(0.001m, maxY - minY);

            var cx = (minX + maxX) / 2m;
            var cy = (minY + maxY) / 2m;

            var canvasRatio = Wm / Hm;
            var areaRatio = bboxW / bboxH;

            decimal vw, vh;
            if (areaRatio > canvasRatio) { vw = bboxW; vh = vw / canvasRatio; }
            else { vh = bboxH; vw = vh * canvasRatio; }

            var vx = cx - vw / 2m;
            var vy = cy - vh / 2m;

            if (vx < 0m) vx = 0m;
            if (vy < 0m) vy = 0m;
            if (vx + vw > Wm) vx = Math.Max(0m, Wm - vw);
            if (vy + vh > Hm) vy = Math.Max(0m, Hm - vh);

            _panX = vx;
            _panY = vy;
            _zoom = (double)((vw <= 0m) ? 1m : (Wm / vw));
        }
    }
}

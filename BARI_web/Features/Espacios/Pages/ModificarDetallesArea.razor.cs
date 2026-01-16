using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using BARI_web.General_Services.DataBaseConnection;
using System.Text.Json;
using System;



namespace BARI_web.Features.Espacios.Pages
{
    public partial class ModificarDetallesArea : ComponentBase
    {
        [Parameter] public string AreaSlug { get; set; } = "";
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;

        // ===== Modelos =====
        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal alto_m, decimal margen_m);
        // snapshot de arrastre/redimensionamiento (base)
        private (decimal x, decimal y, decimal w, decimal h)? _beforeDragIn;
        private record Poly(string poly_id, string canvas_id, string? area_id,
                            decimal x_m, decimal y_m, decimal ancho_m, decimal alto_m,
                            int z_order, string? etiqueta, string? color_hex);

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

        private class InnerItem
        {
            public string poly_in_id { get; set; } = "";
            public string area_poly_id { get; set; } = "";

            // Requeridos por DB (NOT NULL)
            public string canvas_id { get; set; } = "";  // << NUEVO
            public string area_id { get; set; } = "";    // << NUEVO
            public decimal eje_x_rel_m { get; set; }
            public decimal eje_y_rel_m { get; set; }
            public decimal eje_z_rel_m { get; set; } = 0m;   // << NUEVO
            public decimal ancho_m { get; set; }
            public decimal profundo_m { get; set; } = 0.6m;  // << NUEVO (profundidad por defecto)
            public decimal alto_m { get; set; }
            public decimal yaw_deg { get; set; } = 0m;       // << NUEVO
            public string pivot_kind { get; set; } = "center"; // << NUEVO
            public decimal offset_x_m { get; set; } = 0m;    // << NUEVO
            public decimal offset_y_m { get; set; } = 0m;    // << NUEVO
            public decimal offset_z_m { get; set; } = 0m;    // << NUEVO
            public int z_order { get; set; } = 0;

            // Opcionales / display
            public string? label { get; set; }
            public string fill { get; set; } = "#4B5563";
            public decimal opacidad { get; set; } = 0.35m;

            // calculados
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }

            // vínculos opcionales
            public string? meson_id { get; set; }
            public string? instalacion_id { get; set; }
        }

        private class BlockItem
        {
            public string bloque_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? material_id { get; set; }
            public string? etiqueta { get; set; }
            public string? color_hex { get; set; }
            public int z_order { get; set; }
            public decimal pos_x { get; set; }
            public decimal pos_y { get; set; }
            public decimal ancho { get; set; }
            public decimal alto { get; set; }
            public decimal offset_x { get; set; }
            public decimal offset_y { get; set; }

            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
        }

        private class Door { public decimal x_m, y_m, largo_m; public string orientacion = "E"; }
        private class Win { public decimal x_m, y_m, largo_m; public string orientacion = "E"; }

        // Mesones (resumen de columnas que editamos)
        private class Meson
        {
            public string meson_id { get; set; } = "";
            public string area_id { get; set; } = "";          // NOT NULL en DB
            public string nombre_meson { get; set; } = "";      // NOT NULL en DB
            public int ancho_cm { get; set; }
            public int largo_cm { get; set; }
            public int profundidad_cm { get; set; }
            public int niveles_totales { get; set; }
        }


        // Instalaciones (resumen de columnas editables)
        private class Instalacion
        {
            public string instalacion_id { get; set; } = "";
            public string nombre { get; set; } = "";
            public string? tipo_id { get; set; }
            public string? notas { get; set; }
            public bool requiere_mantenimiento { get; set; }
        }

        // ===== Estado =====
        private CanvasLab? _canvas;
        private AreaDraw? _area;
        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();
        private readonly List<InnerItem> _inners = new();
        private readonly Dictionary<string, InnerItem> _mapIn = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BlockItem> _blocks = new();
        private readonly Dictionary<string, BlockItem> _blocksById = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _materialesLookup = new(StringComparer.OrdinalIgnoreCase);

        // vínculos
        private readonly Dictionary<string, Meson> _mesones = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Instalacion> _instalaciones = new(StringComparer.OrdinalIgnoreCase);

        // viewbox
        private decimal VX, VY, VW, VH;
        private string ViewBox()
        {
            var vw = (decimal)((double)Wm / _zoom);
            var vh = (decimal)((double)Hm / _zoom);
            return $"{S(_panX)} {S(_panY)} {S(vw)} {S(vh)}";
        }
        private string AspectRatioString() { var vw = VW <= 0 ? 1 : VW; var vh = VH <= 0 ? 1 : VH; var ar = (double)vw / (double)vh; return $"{ar:0.###} / 1"; }
        private decimal AreaCenterX => _area is null ? 0m : (_area.MinX + _area.MaxX) / 2m;
        private decimal AreaCenterY => _area is null ? 0m : (_area.MinY + _area.MaxY) / 2m;

        // grilla cache
        private decimal GridStartX, GridEndX, GridStartY, GridEndY;

        // pan/zoom
        private double _zoom = 1.0;
        private decimal _panX = 0m, _panY = 0m;
        private (double x, double y)? _panStart;
        private bool _panMoved = false;
        private ElementReference _svgRef;

        // selección / drag
        private string? _selIn;
        private string? _hoverIn;
        private (decimal x, decimal y)? _dragStart;
        private InnerItem? _dragIn;
        private Poly? _dragParent;
        private bool _resizing = false;
        private Handle _activeHandle = Handle.None;

        // guardar
        private bool _saving = false;
        private string _saveMsg = "";

        // === UI: paneles bajo el lienzo
        private bool _showNuevaInstalacionPanel = false;
        private bool _showTipoInstalacionPanel = false;

        // === Catálogo: tipos de instalación (id -> nombre)
        private Dictionary<string, string>? _tiposInstalacion;

        // === Formulario: Nueva instalación
        private string _nuevoIns_TipoId = "";
        private string _nuevoIns_Nombre = "";
        private string? _nuevoIns_Notas = null;
        private bool _nuevoIns_RequiereMantenimiento = false;

        // === Formulario: Nuevo tipo de instalación
        private string _nuevoTipo_Nombre = "";
        private string? _nuevoTipo_Descripcion = null;

        private string? _newBlockMaterialId;
        private string _newBlockEtiqueta = "";
        private decimal _newBlockAncho = 0.6m;
        private decimal _newBlockAlto = 0.4m;
        private decimal _newBlockOffsetX = 0m;
        private decimal _newBlockOffsetY = 0m;
        private string _newBlockColor = "#2563eb";
        private string? _blockMsg;


        // ===== Apariencia / tolerancias =====
        private const decimal OutlineStroke = 0.28m;
        private const decimal Tolerance = 0.004m; // 4mm

        private enum Handle { NW, NE, SW, SE, None }

        // ================== INIT ==================
        protected override async Task OnInitializedAsync()
        {
            try
            {
                // ===== Canvas
                Pg.UseSheet("canvas_lab");
                var c = (await Pg.ReadAllAsync()).FirstOrDefault();
                if (c is null) { _saveMsg = "No hay canvas_lab."; return; }
                _canvas = new CanvasLab(c["canvas_id"], c["nombre"], Dec(c["ancho_m"]), Dec(c["alto_m"]), Dec(c["margen_m"]));

                // ===== Resolver área desde slug
                var targetAreaId = await ResolveAreaIdFromSlug(AreaSlug);

                // ===== Polígonos del área
                List<Poly> polys = new();
                Pg.UseSheet("poligonos");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(r["canvas_id"], _canvas.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;
                    var areaId = NullIfEmpty(r["area_id"]) ?? "";
                    if (!string.Equals(areaId, targetAreaId, StringComparison.OrdinalIgnoreCase)) continue;

                    // --- normalización aquí ---
                    var x = Dec(r["x_m"]);
                    var y = Dec(r["y_m"]);
                    var w = Dec(r["ancho_m"]);
                    var h = Dec(r["alto_m"]);

                    if (w < 0m) { x += w; w = -w; }
                    if (h < 0m) { y += h; h = -h; }

                    polys.Add(new Poly(
                        r["poly_id"], r["canvas_id"], areaId,
                        x, y, w, h,
                        Int(Get(r, "z_order", "0")),
                        NullIfEmpty(Get(r, "etiqueta")),
                        NullIfEmpty(Get(r, "color_hex"))
                    ));
                }


                // ===== Construir AreaDraw (con defensas por polígonos degenerados)
                var a = new AreaDraw { AreaId = targetAreaId };
                a.Polys.AddRange(polys
                    .Where(p => p.ancho_m > 0m && p.alto_m > 0m)
                    .OrderBy(p => p.z_order));

                if (a.Polys.Count == 0) { _saveMsg = "Todos los polígonos del área tienen tamaño 0."; return; }

                a.MinX = a.Polys.Min(p => p.x_m);
                a.MinY = a.Polys.Min(p => p.y_m);
                a.MaxX = a.Polys.Max(p => p.x_m + p.ancho_m);
                a.MaxY = a.Polys.Max(p => p.y_m + p.alto_m);

                try
                {
                    var lookup = await Pg.GetLookupAsync("areas", "area_id", "nombre_areas");
                    a.Label = (lookup.TryGetValue(a.AreaId, out var n) ? n : a.AreaId).ToUpperInvariant();
                }
                catch { a.Label = targetAreaId.ToUpperInvariant(); }

                a.Fill = a.Polys.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";

                // Outline robusto: que nunca falle aunque haya tolerancias raras
                try { BuildAreaOutlineSafe(a); } catch { a.Outline.Clear(); }

                _area = a;

                // ===== Viewbox / pan-zoom
                FitViewBoxToAreaWithAspect(a, 0.25m);
                UpdateViewMetrics();
                CacheGrid();
                await InvokeAsync(StateHasChanged);
                CacheGrid();

                // ===== Cargas dependientes (inners, puertas/ventanas, vínculos)
                try { await LoadInnerItemsForArea(a); } catch (Exception ex) { Console.Error.WriteLine($"[LoadInnerItemsForArea] {ex}"); }
                try { await LoadBlocksForArea(a); } catch (Exception ex) { Console.Error.WriteLine($"[LoadBlocksForArea] {ex}"); }
                try { await LoadDoorsAndWindowsForArea(a); } catch (Exception ex) { Console.Error.WriteLine($"[LoadDoorsAndWindowsForArea] {ex}"); }
                try { await LoadMesonesLinks(); } catch (Exception ex) { Console.Error.WriteLine($"[LoadMesonesLinks] {ex}"); }
                try { await LoadInstalacionesLinks(); } catch (Exception ex) { Console.Error.WriteLine($"[LoadInstalacionesLinks] {ex}"); }

                // Catálogo de tipos
                try { _tiposInstalacion = await Pg.GetLookupAsync("instalaciones_tipo", "tipo_id", "nombre"); }
                catch { _tiposInstalacion = new Dictionary<string, string>(); }
            }
            catch (Exception ex)
            {
                // Si algo rompe, deja un mensaje pero no bloquea el render
                _saveMsg = "Error al cargar (ver consola).";
                Console.Error.WriteLine($"[ModificarDetallesArea.OnInitializedAsync] {ex}");
            }
        }


        // ================== CARGAS ==================
        private async Task LoadInnerItemsForArea(AreaDraw a)
        {
            _inners.Clear(); _mapIn.Clear();
            var areaPolys = a.Polys.ToDictionary(p => p.poly_id, p => p);

            Pg.UseSheet("poligonos_interiores");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var area_poly_id = Get(r, "area_poly_id");
                if (!areaPolys.TryGetValue(area_poly_id, out var parentPoly)) continue;

                var it = new InnerItem
                {
                    poly_in_id = Get(r, "poly_in_id"),
                    area_poly_id = area_poly_id,

                    canvas_id = Get(r, "canvas_id", _canvas?.canvas_id ?? ""),
                    area_id = Get(r, "area_id", a.AreaId),

                    eje_x_rel_m = Dec(Get(r, "eje_x_rel_m", "0")),
                    eje_y_rel_m = Dec(Get(r, "eje_y_rel_m", "0")),
                    eje_z_rel_m = Dec(Get(r, "eje_z_rel_m", "0")),

                    ancho_m = Dec(Get(r, "ancho_m", "0")),
                    profundo_m = Dec(Get(r, "profundo_m", "0.6")),
                    alto_m = Dec(Get(r, "alto_m", "0")),

                    yaw_deg = Dec(Get(r, "yaw_deg", "0")),
                    pivot_kind = NullIfEmpty(Get(r, "pivot_kind")) ?? "center",

                    offset_x_m = Dec(Get(r, "offset_x_m", "0")),
                    offset_y_m = Dec(Get(r, "offset_y_m", "0")),
                    offset_z_m = Dec(Get(r, "offset_z_m", "0")),

                    z_order = Int(Get(r, "z_order", "0")),

                    label = NullIfEmpty(Get(r, "etiqueta")),
                    fill = NullIfEmpty(Get(r, "color_hex")) ?? "#4B5563",
                    opacidad = Dec(Get(r, "opacidad_0_1", "0.35")),

                    meson_id = NullIfEmpty(Get(r, "meson_id")),
                    instalacion_id = NullIfEmpty(Get(r, "instalacion_id"))
                };


                it.abs_x = parentPoly.x_m + it.eje_x_rel_m + it.offset_x_m;
                it.abs_y = parentPoly.y_m + it.eje_y_rel_m + it.offset_y_m;


                _inners.Add(it);
                _mapIn[it.poly_in_id] = it;
            }
        }

        private async Task LoadBlocksForArea(AreaDraw a)
        {
            _blocks.Clear();
            _blocksById.Clear();
            _materialesLookup.Clear();

            Pg.UseSheet("materiales_montaje");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "area_id"), a.AreaId, StringComparison.OrdinalIgnoreCase)) continue;
                var materialId = Get(r, "material_id");
                if (string.IsNullOrWhiteSpace(materialId)) continue;
                _materialesLookup[materialId] = Get(r, "nombre");
            }

            Pg.UseSheet("bloques_int");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas?.canvas_id ?? "", StringComparison.OrdinalIgnoreCase)) continue;

                var materialId = NullIfEmpty(Get(r, "material_id"));
                if (!string.IsNullOrWhiteSpace(materialId) && !_materialesLookup.ContainsKey(materialId!))
                {
                    continue;
                }

                var offsetX = Dec(Get(r, "offset_x", "0"));
                var offsetY = Dec(Get(r, "offset_y", "0"));
                var absX = Dec(Get(r, "pos_x", "0"));
                var absY = Dec(Get(r, "pos_y", "0"));

                if (offsetX == 0m && offsetY == 0m && (absX != 0m || absY != 0m))
                {
                    offsetX = absX - AreaCenterX;
                    offsetY = absY - AreaCenterY;
                }

                var it = new BlockItem
                {
                    bloque_id = Get(r, "bloque_id"),
                    canvas_id = Get(r, "canvas_id"),
                    material_id = materialId,
                    etiqueta = NullIfEmpty(Get(r, "etiqueta")),
                    color_hex = NullIfEmpty(Get(r, "color_hex")) ?? "#2563eb",
                    z_order = Int(Get(r, "z_order", "0")),
                    pos_x = absX,
                    pos_y = absY,
                    ancho = Dec(Get(r, "ancho", "0.6")),
                    alto = Dec(Get(r, "alto", "0.4")),
                    offset_x = offsetX,
                    offset_y = offsetY
                };
                UpdateBlockAbs(it);
                _blocks.Add(it);
                _blocksById[it.bloque_id] = it;
            }
        }

        private void UpdateBlockAbs(BlockItem it)
        {
            it.abs_x = AreaCenterX + it.offset_x;
            it.abs_y = AreaCenterY + it.offset_y;
            it.pos_x = it.abs_x;
            it.pos_y = it.abs_y;
        }

        private async Task LoadDoorsAndWindowsForArea(AreaDraw a)
        {
            _doors.Clear(); _windows.Clear();

            static (string orient, decimal len) AxisAndLen(decimal x1, decimal y1, decimal x2, decimal y2)
            {
                if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                    return ("E", Math.Abs(x2 - x1));
                else
                    return ("N", Math.Abs(y2 - y1));
            }

            // puertas
            Pg.UseSheet("puertas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(r["canvas_id"], _canvas!.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

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

            // ventanas
            Pg.UseSheet("ventanas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(r["canvas_id"], _canvas!.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

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

        private async Task LoadMesonesLinks()
        {
            var needed = _inners.Select(i => i.meson_id)
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (needed.Count == 0) return;

            Pg.UseSheet("mesones");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = Get(r, "meson_id");
                if (!needed.Contains(id)) continue;

                _mesones[id] = new Meson
                {
                    meson_id = id,
                    area_id = Get(r, "area_id"),                      // NOT NULL
                    nombre_meson = Get(r, "nombre_meson"),            // NOT NULL
                    ancho_cm = Int(Get(r, "ancho_cm", "0")),
                    largo_cm = Int(Get(r, "largo_cm", "0")),
                    profundidad_cm = Int(Get(r, "profundidad_cm", "0")),
                    niveles_totales = Int(Get(r, "niveles_totales", "0"))
                };
            }
        }


        private async Task LoadInstalacionesLinks()
        {
            var needed = _inners.Select(i => i.instalacion_id)
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (needed.Count == 0) return;

            Pg.UseSheet("instalaciones");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = Get(r, "instalacion_id"); // PK correcta
                if (!needed.Contains(id)) continue;

                _instalaciones[id] = new Instalacion
                {
                    instalacion_id = id,
                    nombre = Get(r, "nombre"),
                    tipo_id = NullIfEmpty(Get(r, "tipo_id")),
                    notas = NullIfEmpty(Get(r, "notas")),
                    requiere_mantenimiento = string.Equals(Get(r, "requiere_mantenimiento", "false"), "true", StringComparison.OrdinalIgnoreCase)
                };
            }
        }




        // ================== Canvas helpers ==================
        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.alto_m ?? 10m;

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

            // <<< CLAVE: en vez de VX/VY/VW/VH, setea pan/zoom reales del viewBox >>>
            _panX = vx;
            _panY = vy;
            _zoom = (double)((vw <= 0m) ? 1m : (Wm / vw));
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


        private void CenterView()
        {
            ClampPanToBounds();
            UpdateViewMetrics();
        }


        // ================== Outline (del área) ==================
        private static decimal RoundTol(decimal v) => Math.Round(v / Tolerance) * Tolerance;
        private static void BuildAreaOutlineSafe(AreaDraw a)
        {
            const decimal Tol = Tolerance; // 0.004m
            static decimal RT(decimal v) => Math.Round(v / Tol) * Tol;

            var H = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var V = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var p in a.Polys)
            {
                if (p.ancho_m <= 0m || p.alto_m <= 0m) continue;

                var L = p.x_m; var T = p.y_m; var R = p.x_m + p.ancho_m; var B = p.y_m + p.alto_m;

                var yTop = RT(T); var yBot = RT(B);
                var xLft = RT(L); var xRgt = RT(R);

                var x1 = RT(Math.Min(L, R)); var x2 = RT(Math.Max(L, R));
                var y1 = RT(Math.Min(T, B)); var y2 = RT(Math.Max(T, B));

                if (!H.ContainsKey(yTop)) H[yTop] = new(); if (!H.ContainsKey(yBot)) H[yBot] = new();
                H[yTop].Add((x1, x2)); H[yBot].Add((x1, x2));

                if (!V.ContainsKey(xLft)) V[xLft] = new(); if (!V.ContainsKey(xRgt)) V[xRgt] = new();
                V[xLft].Add((y1, y2)); V[xRgt].Add((y1, y2));
            }

            a.Outline.Clear();

            void SweepHoriz()
            {
                foreach (var (y, spans) in H)
                {
                    if (spans.Count == 0) continue;
                    var xs = new SortedSet<decimal>();
                    foreach (var (a1, a2) in spans) { var lo = Math.Min(a1, a2); var hi = Math.Max(a1, a2); xs.Add(lo); xs.Add(hi); }
                    var xList = xs.ToList();
                    for (int i = 0; i < xList.Count - 1; i++)
                    {
                        var s = xList[i]; var e = xList[i + 1];
                        if (e <= s + Tol / 10m) continue;
                        int count = 0;
                        foreach (var (a1, a2) in spans)
                        {
                            var lo = Math.Min(a1, a2); var hi = Math.Max(a1, a2);
                            if (s >= lo - Tol / 2m && e <= hi + Tol / 2m) count++;
                        }
                        if ((count % 2) == 1) a.Outline.Add((s, y, e, y));
                    }
                }
            }

            void SweepVert()
            {
                foreach (var (x, spans) in V)
                {
                    if (spans.Count == 0) continue;
                    var ys = new SortedSet<decimal>();
                    foreach (var (b1, b2) in spans) { var lo = Math.Min(b1, b2); var hi = Math.Max(b1, b2); ys.Add(lo); ys.Add(hi); }
                    var yList = ys.ToList();
                    for (int i = 0; i < yList.Count - 1; i++)
                    {
                        var s = yList[i]; var e = yList[i + 1];
                        if (e <= s + Tol / 10m) continue;
                        int count = 0;
                        foreach (var (b1, b2) in spans)
                        {
                            var lo = Math.Min(b1, b2); var hi = Math.Max(b1, b2);
                            if (s >= lo - Tol / 2m && e <= hi + Tol / 2m) count++;
                        }
                        if ((count % 2) == 1) a.Outline.Add((x, s, x, e));
                    }
                }
            }

            try { SweepHoriz(); SweepVert(); }
            catch { a.Outline.Clear(); }
        }


        // ================== Interacción ==================
        private RenderFragment CornerHandle(InnerItem it, decimal lx, decimal ly, Handle h) => builder =>
        {
            // OJO: el <g> padre ya está en translate(abs_x, abs_y),
            // así que aquí usamos coordenadas LOCALES (0..ancho, 0..alto).
            var size = 0.30m;
            var x = lx - size / 2m;
            var y = ly - size / 2m;
            int seq = 0;

            var cursor = h switch
            {
                Handle.NW => "nwse-resize",
                Handle.SE => "nwse-resize",
                Handle.NE => "nesw-resize",
                Handle.SW => "nesw-resize",
                _ => "nwse-resize"
            };

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
                EventCallback.Factory.Create<PointerEventArgs>(this,
                    (PointerEventArgs e) => OnPointerDownResizeInner(e, it.poly_in_id, h)));
            builder.CloseElement();
        };



        private void OnPointerDownMoveInner(PointerEventArgs e, string id)
        {
            _selIn = id; _dragIn = _mapIn[id]; _dragParent = _area!.Polys.First(p => p.poly_id == _dragIn.area_poly_id);
            _activeHandle = Handle.None;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);

            // SNAPSHOT BASE EN ABS (¡no rel!)
            _beforeDragIn = (_dragIn.abs_x, _dragIn.abs_y, _dragIn.ancho_m, _dragIn.alto_m);

            // offset para seguir el cursor
            _grab = (wx - _dragIn.abs_x, wy - _dragIn.abs_y);

            StateHasChanged();
        }

        private void OnPointerDownResizeInner(PointerEventArgs e, string id, Handle h)
        {
            _selIn = id; _dragIn = _mapIn[id]; _dragParent = _area!.Polys.First(p => p.poly_id == _dragIn.area_poly_id);
            _activeHandle = h; _resizing = true;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            _dragStart = (wx, wy);

            // SNAPSHOT BASE EN ABS (¡no rel!)
            _beforeDragIn = (_dragIn.abs_x, _dragIn.abs_y, _dragIn.ancho_m, _dragIn.alto_m);

            // en resize no usamos grab
            _grab = null;

            StateHasChanged();
        }


        // tolerancias
        const decimal EPS_JOIN = 0.012m; // ~1.2 cm de tolerancia 
        const decimal EPS_MINW = 0.10m;    // tamaño mínimo

        private void OnPointerMove(PointerEventArgs e)
        {
            // ===== Pan =====
            if (_panStart is not null && _dragIn is null)
            {
                var (sx, sy) = _panStart.Value;
                var dxPx = e.OffsetX - sx; var dyPx = e.OffsetY - sy;
                if (!_panMoved && (Math.Abs(dxPx) > 3 || Math.Abs(dyPx) > 3)) _panMoved = true;
                _panStart = (e.OffsetX, e.OffsetY);

                var metersX = (decimal)(dxPx / (PxPerM() * _zoom));
                var metersY = (decimal)(dyPx / (PxPerM() * _zoom));

                var vw = (decimal)((double)Wm / _zoom);
                var vh = (decimal)((double)Hm / _zoom);
                _panX = Clamp(0m, Wm - vw, _panX - metersX);
                _panY = Clamp(0m, Hm - vh, _panY - metersY);
                StateHasChanged();
                return;
            }

            // ===== Nada que arrastrar =====
            if (_dragIn is null || _dragParent is null || _dragStart is null || _beforeDragIn is null || _area is null) return;

            var (wx, wy) = ScreenToWorld(e.OffsetX, e.OffsetY);
            var dx = wx - _dragStart.Value.x;
            var dy = wy - _dragStart.Value.y;

            var baseAbsX = _beforeDragIn.Value.x;
            var baseAbsY = _beforeDragIn.Value.y;
            var baseW = _beforeDragIn.Value.w;
            var baseH = _beforeDragIn.Value.h;

            decimal propAbsX = baseAbsX, propAbsY = baseAbsY;
            decimal propW = baseW, propH = baseH;

            if (_activeHandle == Handle.None)
            {
                // MOVE: seguir cursor usando ABS + grab
                if (_grab is not null)
                {
                    propAbsX = wx - _grab.Value.dx;
                    propAbsY = wy - _grab.Value.dy;
                }
                else
                {
                    propAbsX = baseAbsX + dx;
                    propAbsY = baseAbsY + dy;
                }
            }
            else
            {
                // RESIZE: calcula desde ABS base
                switch (_activeHandle)
                {
                    case Handle.NE:
                        {
                            var bottom = baseAbsY + baseH;
                            propW = Math.Max(EPS_MINW, baseW + dx);
                            propH = Math.Max(EPS_MINW, baseH - dy);
                            propAbsX = baseAbsX;
                            propAbsY = bottom - propH;
                            break;
                        }
                    case Handle.SE:
                        {
                            propW = Math.Max(EPS_MINW, baseW + dx);
                            propH = Math.Max(EPS_MINW, baseH + dy);
                            propAbsX = baseAbsX;
                            propAbsY = baseAbsY;
                            break;
                        }
                    case Handle.NW:
                        {
                            var right = baseAbsX + baseW;
                            var bottom = baseAbsY + baseH;
                            propW = Math.Max(EPS_MINW, baseW - dx);
                            propH = Math.Max(EPS_MINW, baseH - dy);
                            propAbsX = right - propW;
                            propAbsY = bottom - propH;
                            break;
                        }
                    case Handle.SW:
                        {
                            var right = baseAbsX + baseW;
                            propW = Math.Max(EPS_MINW, baseW - dx);
                            propH = Math.Max(EPS_MINW, baseH + dy);
                            propAbsX = right - propW;
                            propAbsY = baseAbsY;
                            break;
                        }
                }
            }

            // 1) Coord deseada en ABS (seguir puntero)
            var desiredX = propAbsX;
            var desiredY = propAbsY;

            // 2) Solo elegimos el mejor padre para “pintar” el frame, SIN clampear
            var (best, _, _) = SoftClampToAreaUnion(_area!, desiredX, desiredY, propW, propH, _dragParent!);

            // 2.5) Clamp duro al polígono elegido para respetar paredes durante el drag
            var (clampedX, clampedY) = ClampRectIn(best, desiredX, desiredY, propW, propH);

            // 3) Actualiza la pose dentro del área
            CommitToUnion(_dragIn!, _area!, best, clampedX, clampedY, propW, propH, allowRehome: false);

            // 4) Que el “padre de drag” siga al cursor (así ves cruzar el seam)
            _dragParent = best;

            StateHasChanged();
        }






        private static string PickNombreUnico(string areaId, IEnumerable<Meson> existentes)
        {
            var usados = existentes.Where(m => string.Equals(m.area_id, areaId, StringComparison.OrdinalIgnoreCase))
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


        private void OnPointerUp(PointerEventArgs e)
        {
            var finishing = _dragIn;
            var parent = _dragParent;

            // limpia estados de drag
            _dragStart = null; _dragIn = null; _dragParent = null; _resizing = false; _activeHandle = Handle.None;
            _beforeDragIn = null; _grab = null;

            if (_panStart is not null && !_panMoved) { _selIn = null; }
            _panStart = null;

            if (finishing is not null && _area is not null && parent is not null)
            {
                // Clamp duro para que nunca quede fuera del área al soltar.
                var (cx, cy) = ClampRectIn(parent, finishing.abs_x, finishing.abs_y,
                                          finishing.ancho_m, finishing.alto_m);

                var relHardX = Math.Round(cx - parent.x_m, 3, MidpointRounding.AwayFromZero);
                var relHardY = Math.Round(cy - parent.y_m, 3, MidpointRounding.AwayFromZero);

                // Actualiza ABS y elimina offset (queda dentro del área)
                finishing.abs_x = cx;
                finishing.abs_y = cy;
                finishing.offset_x_m = 0m;
                finishing.offset_y_m = 0m;

                // Guarda el padre y las relativas "válidas"
                finishing.area_poly_id = parent.poly_id;
                finishing.eje_x_rel_m = relHardX;
                finishing.eje_y_rel_m = relHardY;

                // OJO: no llames NormalizeInner(finishing) aquí (no queremos mover abs)
            }


            StateHasChanged();
        }

        // Solape (área) entre rect (absX,absY,w,h) y polígono p
        private static decimal OverlapWithPoly(Poly p, decimal absX, decimal absY, decimal w, decimal h)
        {
            var a = (L: absX, T: absY, R: absX + w, B: absY + h);
            var b = (L: p.x_m, T: p.y_m, R: p.x_m + p.ancho_m, B: p.y_m + p.alto_m);
            var x1 = Math.Max(a.L, b.L);
            var y1 = Math.Max(a.T, b.T);
            var x2 = Math.Min(a.R, b.R);
            var y2 = Math.Min(a.B, b.B);
            var W = x2 - x1;
            var H = y2 - y1;
            return (W > 0m && H > 0m) ? (W * H) : 0m;
        }

        private (Poly best, decimal ovBest, decimal ovCurrent) BestPolyByOverlap(AreaDraw area, Poly current, decimal absX, decimal absY, decimal w, decimal h)
        {
            Poly best = current;
            decimal bestOv = OverlapWithPoly(current, absX, absY, w, h);
            foreach (var p in area.Polys)
            {
                var ov = OverlapWithPoly(p, absX, absY, w, h);
                if (ov > bestOv) { bestOv = ov; best = p; }
            }
            var ovCur = OverlapWithPoly(current, absX, absY, w, h);
            return (best, bestOv, ovCur);
        }

        // Regla de histéresis:
        // - Requiere que el centro caiga dentro del candidato
        // - Y que el solape nuevo supere al actual por un delta:
        //   delta = max(12% del área del item, 0.015 m² aprox)  <-- ajusta si quieres
        private bool ShouldRehome(AreaDraw area, Poly current, Poly candidate, decimal absX, decimal absY, decimal w, decimal h, decimal ovCur, decimal ovNew)
        {
            if (candidate.poly_id == current.poly_id) return false;

            var cx = absX + w / 2m;
            var cy = absY + h / 2m;
            if (!PointInsidePoly(candidate, cx, cy, 0m))
                return false;

            var itemArea = Math.Max(0m, w * h);
            var delta = Math.Max(itemArea * 0.12m, 0.015m); // 12% ó ~150 cm²
            return ovNew > (ovCur + delta);
        }




        private void OnPointerDownBackground(PointerEventArgs e)
        {
            // Deselect al clickear fondo y comenzar pan
            _selIn = null;
            BeginPan(e);
        }
        private void BeginPan(PointerEventArgs e) { _panStart = (e.OffsetX, e.OffsetY); _panMoved = false; }

        private void OnWheel(WheelEventArgs e)
        {
            var f = Math.Sign(e.DeltaY) < 0 ? 1.1 : (1 / 1.1);
            _zoom = Math.Clamp(_zoom * f, 0.3, 6.0);
            CenterView();
            StateHasChanged();
        }
        private void UpdateViewMetrics()
        {
            // tamaños visibles (en metros) con el zoom actual
            VW = (decimal)((double)Wm / _zoom);
            VH = (decimal)((double)Hm / _zoom);

            // origen visible
            VX = _panX;
            VY = _panY;

            CacheGrid();   // ya usa _panX/_panY/_zoom
        }

        private void ZoomOut()
        {
            _zoom = Math.Clamp(_zoom / 1.1, 0.3, 6.0);
            CenterView();
            StateHasChanged();
        }
        private void ZoomIn()
        {
            _zoom = Math.Clamp(_zoom * 1.1, 0.3, 6.0);
            CenterView();
            StateHasChanged();
        }


        // ================== Lógica de movimiento/resize ==================
        private void MoveInnerClamped(InnerItem it, Poly currentParent, decimal nxRel, decimal nyRel)
        {
            var area = _area!;                   // misma área en pantalla
            var w = it.ancho_m; var h = it.alto_m;

            // destino absoluto pedido (respecto al padre actual)
            var targetAbsX = currentParent.x_m + nxRel;
            var targetAbsY = currentParent.y_m + nyRel;

            // elegir polígono destino dentro de la MISMA área
            var dest = PickPolyForInnerRect(area, targetAbsX, targetAbsY, w, h);

            // clamping final dentro del polígono elegido
            var (absX, absY) = ClampRectIn(dest, targetAbsX, targetAbsY, w, h);

            // actualizar parent + coords relativas
            it.area_poly_id = dest.poly_id;
            it.eje_x_rel_m = Math.Round(absX - dest.x_m, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(absY - dest.y_m, 3, MidpointRounding.AwayFromZero);
            it.abs_x = absX; it.abs_y = absY;
        }

        private void ResizeInnerClamped(InnerItem it, Poly parent, decimal dx, decimal dy, Handle h)
        {
            var baseX = it.eje_x_rel_m; var baseY = it.eje_y_rel_m;
            var baseW = it.ancho_m; var baseH = it.alto_m;

            decimal newX = baseX, newY = baseY, newW = baseW, newH = baseH;

            switch (h)
            {
                case Handle.NE:
                    newW = baseW + dx; newY = baseY + dy; newH = baseH - dy; break;
                case Handle.SE:
                    newW = baseW + dx; newH = baseH + dy; break;
                case Handle.NW:
                    newX = baseX + dx; newW = baseW - dx; newY = baseY + dy; newH = baseH - dy; break;
                case Handle.SW:
                    newX = baseX + dx; newW = baseW - dx; newH = baseH + dy; break;
            }

            // mínimos y clamps a los bordes del parent
            newW = Math.Max(0.10m, newW);
            newH = Math.Max(0.10m, newH);

            // Clamp para que (newX,newY,newW,newH) no salgan del parent
            if (newX < 0m) { newW += newX; newX = 0m; }
            if (newY < 0m) { newH += newY; newY = 0m; }
            if (newX + newW > parent.ancho_m) newW = parent.ancho_m - newX;
            if (newY + newH > parent.alto_m) newH = parent.alto_m - newY;

            it.eje_x_rel_m = Math.Round(newX, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(newY, 3, MidpointRounding.AwayFromZero);
            it.ancho_m = Math.Round(newW, 3, MidpointRounding.AwayFromZero);
            it.alto_m = Math.Round(newH, 3, MidpointRounding.AwayFromZero);
            it.abs_x = parent.x_m + it.eje_x_rel_m;
            it.abs_y = parent.y_m + it.eje_y_rel_m;
        }

        private void NormalizeInner(InnerItem it)
        {
            var parent = _area!.Polys.First(p => p.poly_id == it.area_poly_id);
            it.area_id = _area.AreaId;
            if (_canvas is not null) it.canvas_id = _canvas.canvas_id;

            // NO mover abs_x/abs_y aquí
            // Solo recomputa relativas desde ABS (permitiendo “respirar” en memoria)
            var relX = it.abs_x - parent.x_m;
            var relY = it.abs_y - parent.y_m;

            // No clamp duro; si quieres, limita suave en memoria:
            const decimal BREATH = EPS_JOIN; // ~1.2 cm
            var maxX = parent.ancho_m - it.ancho_m;
            var maxY = parent.alto_m - it.alto_m;

            relX = Clamp(-BREATH, maxX + BREATH, relX);
            relY = Clamp(-BREATH, maxY + BREATH, relY);

            it.eje_x_rel_m = Math.Round(relX, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(relY, 3, MidpointRounding.AwayFromZero);

            // Mantén tamaño acotado pero sin recolocar:
            it.ancho_m = Clamp(0.10m, parent.ancho_m, it.ancho_m);
            it.alto_m = Clamp(0.10m, parent.alto_m, it.alto_m);
        }



        // ================== CRUD Inner ==================


        private async Task EliminarInner()
        {
            if (_selIn is null) return;

            if (_mapIn.TryGetValue(_selIn, out var it))
            {
                // Si está vinculado a mesón: eliminar el mesón también
                if (!string.IsNullOrWhiteSpace(it.meson_id))
                {
                    try
                    {
                        Pg.UseSheet("mesones");
                        await Pg.DeleteByIdAsync("meson_id", it.meson_id);
                        _mesones.Remove(it.meson_id);
                    }
                    catch { /* silenciar: si no existía aún en DB no pasa nada */ }
                }

            }

            // Borrar el polígono interior
            try
            {
                Pg.UseSheet("poligonos_interiores");
                await Pg.DeleteByIdAsync("poly_in_id", _selIn);
            }
            catch { }

            _inners.RemoveAll(i => i.poly_in_id == _selIn);
            _mapIn.Remove(_selIn);
            _selIn = null; _saveMsg = "Elemento interior eliminado";
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

            _blocks.RemoveAll(b => b.bloque_id == bloqueId);
            _blocksById.Remove(bloqueId);
            _blockMsg = "Bloque eliminado.";
        }

        private void AgregarBloque()
        {
            if (_canvas is null || _area is null)
            {
                _blockMsg = "No hay canvas/área activa.";
                return;
            }

            var ancho = Clamp(0.1m, 10m, _newBlockAncho);
            var alto = Clamp(0.1m, 10m, _newBlockAlto);
            var offsetX = _newBlockOffsetX;
            var offsetY = _newBlockOffsetY;

            if (!string.IsNullOrWhiteSpace(_newBlockMaterialId)
                && _blocks.Any(b => string.Equals(b.material_id, _newBlockMaterialId, StringComparison.OrdinalIgnoreCase)))
            {
                _blockMsg = "Ese material ya tiene un bloque asociado.";
                return;
            }

            var it = new BlockItem
            {
                bloque_id = $"block_{Guid.NewGuid():N}".Substring(0, 12),
                canvas_id = _canvas.canvas_id,
                material_id = string.IsNullOrWhiteSpace(_newBlockMaterialId) ? null : _newBlockMaterialId,
                etiqueta = string.IsNullOrWhiteSpace(_newBlockEtiqueta) ? null : _newBlockEtiqueta.Trim(),
                color_hex = string.IsNullOrWhiteSpace(_newBlockColor) ? "#2563eb" : _newBlockColor,
                z_order = _blocks.Count == 0 ? 0 : _blocks.Max(b => b.z_order) + 1,
                ancho = ancho,
                alto = alto,
                offset_x = offsetX,
                offset_y = offsetY
            };
            UpdateBlockAbs(it);

            _blocks.Add(it);
            _blocksById[it.bloque_id] = it;
            _blockMsg = "Bloque agregado (recuerda guardar).";
        }



        // ================== Guardar ==================
        private async Task Guardar()
        {
            try
            {
                _saving = true; _saveMsg = "Guardando…"; StateHasChanged();

                // 1) — MESONES (padre)
                if (_mesones.Count > 0)
                    // 1) — MESONES (padre)
                    if (_mesones.Count > 0)
                    {
                        // antes del foreach (defensa extra por si llega repetido)
                        foreach (var g in _mesones.Values.GroupBy(m => m.area_id, StringComparer.OrdinalIgnoreCase))
                        {
                            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var m in g)
                                if (string.IsNullOrWhiteSpace(m.nombre_meson) || !vistos.Add(m.nombre_meson))
                                    m.nombre_meson = PickNombreUnico(g.Key, _mesones.Values);
                        }

                        Pg.UseSheet("mesones");
                        foreach (var m in _mesones.Values)
                        {
                            var toSave = new Dictionary<string, object>
                            {
                                ["area_id"] = m.area_id,
                                ["nombre_meson"] = m.nombre_meson,
                                ["ancho_cm"] = m.ancho_cm,
                                ["largo_cm"] = m.largo_cm,
                                ["profundidad_cm"] = m.profundidad_cm,
                                ["niveles_totales"] = m.niveles_totales
                            };

                            var ok = await Pg.UpdateByIdAsync("meson_id", m.meson_id, toSave);
                            if (!ok)
                            {
                                toSave["meson_id"] = m.meson_id;   // <-- importante
                                await Pg.CreateAsync(toSave);
                            }
                        }

                    }


                // 2) — INSTALACIONES (padre)
                if (_instalaciones.Count > 0)
                {
                    Pg.UseSheet("instalaciones");
                    foreach (var ins in _instalaciones.Values)
                    {
                        var toSave = new Dictionary<string, object>
                        {
                            ["nombre"] = ins.nombre,
                            ["tipo_id"] = string.IsNullOrWhiteSpace(ins.tipo_id) ? (object)DBNull.Value : ins.tipo_id!,
                            ["notas"] = ins.notas ?? (object)DBNull.Value,
                            ["requiere_mantenimiento"] = ins.requiere_mantenimiento
                        };

                        // PK real: instalacion_id
                        var ok = await Pg.UpdateByIdAsync("instalacion_id", ins.instalacion_id, toSave);
                        if (!ok)
                        {
                            toSave["instalacion_id"] = ins.instalacion_id;
                            await Pg.CreateAsync(toSave);
                        }
                    }
                }

                // 3) — POLIGONOS_INTERIORES (hijo) — AHORA SI
                Pg.UseSheet("poligonos_interiores");
                foreach (var it in _inners)
                {
                    var parent = _area!.Polys.First(p => p.poly_id == it.area_poly_id);

                    // Clamp SUAVE a ±EPS_JOIN SOLO para persistir
                    var (cx, cy) = ClampRectInSoft(parent, it.abs_x, it.abs_y, it.ancho_m, it.alto_m, EPS_JOIN);
                    var relX = Math.Round(cx - parent.x_m, 3, MidpointRounding.AwayFromZero);
                    var relY = Math.Round(cy - parent.y_m, 3, MidpointRounding.AwayFromZero);

                    var toSave = new Dictionary<string, object>
                    {
                        ["area_poly_id"] = it.area_poly_id,
                        ["canvas_id"] = it.canvas_id,
                        ["area_id"] = it.area_id,

                        ["eje_x_rel_m"] = relX,
                        ["eje_y_rel_m"] = relY,
                        ["eje_z_rel_m"] = it.eje_z_rel_m,

                        ["ancho_m"] = it.ancho_m,
                        ["profundo_m"] = it.profundo_m,
                        ["alto_m"] = it.alto_m,

                        ["yaw_deg"] = it.yaw_deg,
                        ["pivot_kind"] = it.pivot_kind,

                        ["offset_x_m"] = it.offset_x_m,
                        ["offset_y_m"] = it.offset_y_m,
                        ["offset_z_m"] = it.offset_z_m,

                        ["z_order"] = it.z_order,
                        ["etiqueta"] = it.label ?? (object)DBNull.Value,
                        ["color_hex"] = it.fill,
                        ["opacidad_0_1"] = it.opacidad,

                        ["meson_id"] = string.IsNullOrWhiteSpace(it.meson_id) ? (object)DBNull.Value : it.meson_id!,
                        ["instalacion_id"] = string.IsNullOrWhiteSpace(it.instalacion_id) ? (object)DBNull.Value : it.instalacion_id!
                    };

                    var ok = await Pg.UpdateByIdAsync("poly_in_id", it.poly_in_id, toSave);
                    if (!ok) { toSave["poly_in_id"] = it.poly_in_id; await Pg.CreateAsync(toSave); }
                }

                // 4) — BLOQUES INTERNOS (materiales_montaje)
                Pg.UseSheet("bloques_int");
                foreach (var b in _blocks)
                {
                    UpdateBlockAbs(b);

                    var toSave = new Dictionary<string, object>
                    {
                        ["canvas_id"] = b.canvas_id,
                        ["material_id"] = string.IsNullOrWhiteSpace(b.material_id) ? (object)DBNull.Value : b.material_id!,
                        ["etiqueta"] = string.IsNullOrWhiteSpace(b.etiqueta) ? (object)DBNull.Value : b.etiqueta!,
                        ["color_hex"] = string.IsNullOrWhiteSpace(b.color_hex) ? (object)DBNull.Value : b.color_hex!,
                        ["z_order"] = b.z_order,
                        ["pos_x"] = b.pos_x,
                        ["pos_y"] = b.pos_y,
                        ["ancho"] = b.ancho,
                        ["alto"] = b.alto,
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
            finally { _saving = false; StateHasChanged(); }
        }


        // ================== Helpers ==================
        private double PxPerM() { const double nominalSvgPx = 1000.0; return nominalSvgPx / (double)Wm; }
        private (decimal x, decimal y) ScreenToWorld(double offsetX, double offsetY)
            => (_panX + (decimal)(offsetX / (PxPerM() * _zoom)),
                _panY + (decimal)(offsetY / (PxPerM() * _zoom)));
        private static bool RangeOverlap(decimal a1, decimal a2, decimal b1, decimal b2)
        {
            if (a1 > a2) (a1, a2) = (a2, a1);
            if (b1 > b2) (b1, b2) = (b2, b1);
            return a1 < b2 && a2 > b1;
        }

        // Redimensionar hacia la derecha manteniendo X base (no salir del parent)
        private decimal ClampRightInner(InnerItem it, Poly parent, decimal baseX, decimal baseY, decimal widthTarget)
        {
            var right = Math.Min(baseX + widthTarget, parent.ancho_m);
            // (Si en el futuro hubiera otros inners que actuaran de obstáculo, aquí se revisarían)
            return Math.Max(0.10m, right - baseX);
        }

        // Redimensionar hacia la izquierda moviendo X para mantener el borde derecho fijo
        private decimal ClampLeftInner(InnerItem it, Poly parent, decimal baseRight, decimal baseY, decimal widthTarget)
        {
            var left = Math.Max(baseRight - widthTarget, 0m);
            return Math.Max(0.10m, baseRight - left);
        }

        // Redimensionar hacia abajo manteniendo Y base (no salir del parent)
        private decimal ClampBottomInner(InnerItem it, Poly parent, decimal baseX, decimal baseY, decimal heightTarget)
        {
            var bottom = Math.Min(baseY + heightTarget, parent.alto_m);
            return Math.Max(0.10m, bottom - baseY);
        }

        // Redimensionar hacia arriba moviendo Y para mantener el borde inferior fijo
        private decimal ClampTopInner(InnerItem it, Poly parent, decimal baseX, decimal baseBottom, decimal heightTarget)
        {
            var top = Math.Max(baseBottom - heightTarget, 0m);
            return Math.Max(0.10m, baseBottom - top);
        }

        private static decimal Clamp(decimal min, decimal max, decimal v) => Math.Max(min, Math.Min(max, v));
        private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(object? o) => int.TryParse(o?.ToString(), out var n) ? n : 0;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;

        private static decimal DoorEndX(Door d) => d.x_m + ((d.orientacion is "E" or "W") ? d.largo_m : 0m);
        private static decimal DoorEndY(Door d) => d.y_m + ((d.orientacion is "N" or "S") ? d.largo_m : 0m);
        private static decimal WinEndX(Win w) => w.x_m + ((w.orientacion is "E" or "W") ? w.largo_m : 0m);
        private static decimal WinEndY(Win w) => w.y_m + ((w.orientacion is "N" or "S") ? w.largo_m : 0m);

        private async Task AgregarMeson()
        {
            if (_area is null || _area.Polys.Count == 0 || _canvas is null) return;

            var parent = _area.Polys[0];

            // tamaño por defecto (m)
            var w = Math.Min(1.2m, parent.ancho_m * 0.4m);
            var h = Math.Min(0.8m, parent.alto_m * 0.4m);

            // centrar dentro del parent
            var rx = Math.Max(0m, (parent.ancho_m - w) / 2m);
            var ry = Math.Max(0m, (parent.alto_m - h) / 2m);

            var innerId = $"pin_{Guid.NewGuid():N}".Substring(0, 12);
            var mesonId = $"mes_{Guid.NewGuid():N}".Substring(0, 12);

            var it = new InnerItem
            {
                poly_in_id = innerId,
                area_poly_id = parent.poly_id,

                canvas_id = _canvas.canvas_id,
                area_id = _area.AreaId,

                eje_x_rel_m = rx,
                eje_y_rel_m = ry,
                eje_z_rel_m = 0m,

                ancho_m = w,
                profundo_m = 0.6m,
                alto_m = h,

                yaw_deg = 0m,
                pivot_kind = "center",
                offset_x_m = 0m,
                offset_y_m = 0m,
                offset_z_m = 0m,

                z_order = 50,

                label = "MESÓN",
                fill = "#4B5563",
                opacidad = 0.35m,

                meson_id = mesonId,
                instalacion_id = null
            };
            it.abs_x = parent.x_m + it.eje_x_rel_m;
            it.abs_y = parent.y_m + it.eje_y_rel_m;

            _inners.Add(it);
            _mapIn[it.poly_in_id] = it;
            _selIn = it.poly_in_id;

            _mesones[mesonId] = new Meson
            {
                meson_id = mesonId,
                area_id = _area.AreaId,
                nombre_meson = PickNombreUnico(_area.AreaId, _mesones.Values), // <-- clave
                ancho_cm = 100,
                largo_cm = 200,
                profundidad_cm = 60,
                niveles_totales = 1
            };


            StateHasChanged();
        }




        private async Task AgregarInstalacion()
        {
            if (_area is null || _area.Polys.Count == 0 || _canvas is null) return;

            if (_tiposInstalacion is null || _tiposInstalacion.Count == 0)
            {
                try { _tiposInstalacion = await Pg.GetLookupAsync("instalaciones_tipo", "tipo_id", "nombre"); }
                catch { _tiposInstalacion = new Dictionary<string, string>(); }
            }

            var parent = _area.Polys[0];

            var w = Math.Min(1.0m, parent.ancho_m * 0.35m);
            var h = Math.Min(1.0m, parent.alto_m * 0.35m);
            var rx = Math.Max(0m, (parent.ancho_m - w) / 2m);
            var ry = Math.Max(0m, (parent.alto_m - h) / 2m);

            var innerId = $"pin_{Guid.NewGuid():N}".Substring(0, 12);
            var instId = $"ins_{Guid.NewGuid():N}".Substring(0, 12);
            string? defaultTipoId = _tiposInstalacion.Count > 0 ? _tiposInstalacion.First().Key : null;

            var it = new InnerItem
            {
                poly_in_id = innerId,
                area_poly_id = parent.poly_id,

                canvas_id = _canvas.canvas_id,
                area_id = _area.AreaId,

                eje_x_rel_m = rx,
                eje_y_rel_m = ry,
                eje_z_rel_m = 0m,

                ancho_m = w,
                profundo_m = 0.6m,
                alto_m = h,

                yaw_deg = 0m,
                pivot_kind = "center",
                offset_x_m = 0m,
                offset_y_m = 0m,
                offset_z_m = 0m,

                z_order = 50,

                label = "INSTALACIÓN",
                fill = "#10B981",
                opacidad = 0.35m,

                meson_id = null,
                instalacion_id = instId
            };
            it.abs_x = parent.x_m + it.eje_x_rel_m;
            it.abs_y = parent.y_m + it.eje_y_rel_m;

            _inners.Add(it);
            _mapIn[it.poly_in_id] = it;
            _selIn = it.poly_in_id;

            _instalaciones[instId] = new Instalacion
            {
                instalacion_id = instId,
                nombre = "Nueva instalación",
                tipo_id = defaultTipoId,
                notas = null,
                requiere_mantenimiento = false
            };

            StateHasChanged();
        }


        private static bool RectTouchesPoly(Poly p, decimal x, decimal y, decimal w, decimal h, decimal pad)
        {
            var L = p.x_m - pad; var T = p.y_m - pad;
            var R = p.x_m + p.ancho_m + pad; var B = p.y_m + p.alto_m + pad;

            var aL = x; var aT = y; var aR = x + w; var aB = y + h;
            return (aL < R && aR > L && aT < B && aB > T);
        }

        // Clamp “suave”: permite ±pad al entrar/salir por juntas para evitar pegotes
        private static (decimal cx, decimal cy) ClampRectInSoft(Poly p, decimal absX, decimal absY, decimal w, decimal h, decimal pad)
        {
            var minX = p.x_m - pad;
            var minY = p.y_m - pad;
            var maxX = p.x_m + p.ancho_m + pad - w;
            var maxY = p.y_m + p.alto_m + pad - h;

            var cx = absX; if (cx < minX) cx = minX; if (cx > maxX) cx = maxX;
            var cy = absY; if (cy < minY) cy = minY; if (cy > maxY) cy = maxY;
            return (cx, cy);
        }

        // Escribe en el inner sin NormalizeInner, con clamp relativo SUAVE y reposición abs coherente
        private static void CommitToParent(InnerItem it, Poly parent, decimal absX, decimal absY, decimal w, decimal h)
        {
            var relX = absX - parent.x_m;
            var relY = absY - parent.y_m;

            var maxX = parent.ancho_m - w;
            var maxY = parent.alto_m - h;

            // permite pequeña “respiración” +-EPS_JOIN y luego clamp duro a [0,max]
            if (relX < -EPS_JOIN) relX = -EPS_JOIN;
            if (relY < -EPS_JOIN) relY = -EPS_JOIN;
            if (relX > maxX + EPS_JOIN) relX = maxX + EPS_JOIN;
            if (relY > maxY + EPS_JOIN) relY = maxY + EPS_JOIN;

            var relXHard = Clamp(0m, maxX, relX);
            var relYHard = Clamp(0m, maxY, relY);

            it.area_poly_id = parent.poly_id;
            it.eje_x_rel_m = Math.Round(relXHard, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(relYHard, 3, MidpointRounding.AwayFromZero);
            it.ancho_m = Math.Round(w, 3, MidpointRounding.AwayFromZero);
            it.alto_m = Math.Round(h, 3, MidpointRounding.AwayFromZero);

            it.abs_x = parent.x_m + it.eje_x_rel_m;
            it.abs_y = parent.y_m + it.eje_y_rel_m;
        }

        // Elije el polígono con menor desplazamiento desde la propuesta ABS
        private static (Poly poly, decimal cx, decimal cy) SoftClampToAreaUnion(AreaDraw area, decimal absX, decimal absY, decimal w, decimal h, Poly current)
        {
            Poly best = current;
            decimal bestX = absX, bestY = absY;
            double bestCost = double.MaxValue;

            foreach (var p in area.Polys)
            {
                var (cx, cy) = ClampRectInSoft(p, absX, absY, w, h, EPS_JOIN);
                var dx = (double)(cx - absX);
                var dy = (double)(cy - absY);

                // sesgo para mantener el padre actual si el costo es parecido
                var bias = (p.poly_id == current.poly_id) ? 0.80 : 1.00;
                var cost = (dx * dx + dy * dy) * bias;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = p;
                    bestX = cx; bestY = cy;
                }
            }
            return (best, bestX, bestY);
        }



        private void AbrirNuevoTipoInstalacion()
        {
            _nuevoTipo_Nombre = "";
            _nuevoTipo_Descripcion = null;
            _showTipoInstalacionPanel = true;
        }

        private async Task ConfirmarCrearTipoInstalacion()
        {
            try
            {
                Pg.UseSheet("instalaciones_tipo");
                var newId = $"tipo_{Guid.NewGuid():N}".Substring(0, 12);

                var toSave = new Dictionary<string, object>
                {
                    ["tipo_id"] = newId,
                    ["nombre"] = _nuevoTipo_Nombre,
                    ["notas"] = string.IsNullOrWhiteSpace(_nuevoTipo_Descripcion) ? (object)DBNull.Value : _nuevoTipo_Descripcion!
                };
                await Pg.CreateAsync(toSave);

                // actualizar catálogo local
                _tiposInstalacion ??= new Dictionary<string, string>();
                _tiposInstalacion[newId] = _nuevoTipo_Nombre;

                if (_showNuevaInstalacionPanel)
                    _nuevoIns_TipoId = newId;

                _showTipoInstalacionPanel = false;
                _saveMsg = "Tipo de instalación creado ✔";
                StateHasChanged();
            }
            catch
            {
                _saveMsg = "No se pudo crear el tipo en 'instalaciones_tipo'.";
                StateHasChanged();
            }
        }

        private async Task CrearMesonParaSeleccion()
        {
            if (_selIn is null || !_mapIn.TryGetValue(_selIn, out var it) || _area is null) return;

            var newId = $"mes_{Guid.NewGuid():N}".Substring(0, 12);

            // Nombre por defecto único dentro del área (evita UNIQUE area_id+nombre_meson)
            string BaseName(string area) => "MESON";
            string PickName(string area)
            {
                var prefix = BaseName(area);
                var existing = _mesones.Values.Where(m => string.Equals(m.area_id, area, StringComparison.OrdinalIgnoreCase))
                                              .Select(m => m.nombre_meson)
                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!existing.Contains(prefix)) return prefix;
                for (int i = 2; i < 999; i++)
                {
                    var candidate = $"{prefix} {i:00}";
                    if (!existing.Contains(candidate)) return candidate;
                }
                return $"{prefix} {Guid.NewGuid():N}".Substring(0, 8);
            }

            var areaId = _area.AreaId;
            var nombre = PickName(areaId);

            // Cache local (todavía sin escribir en DB hasta Guardar)
            _mesones[newId] = new Meson
            {
                meson_id = newId,
                area_id = areaId,              // NOT NULL
                nombre_meson = nombre,         // NOT NULL
                ancho_cm = 100,
                largo_cm = 200,
                profundidad_cm = 60,
                niveles_totales = 1
            };

            // Vincular al inner recién creado/seleccionado
            it.meson_id = newId;

            StateHasChanged();
        }

        // offset cursor vs. origen ABS del item al comenzar drag
        private (decimal dx, decimal dy)? _grab;


        // Guarda ABS como fuente de verdad y relativas con margen suave al padre elegido.
        // ¡NO recalcula abs desde rel! así el rect puede “asomar” por la junta durante el drag.
        // firma nueva: allowRehome por defecto = false
        private void CommitToUnion(InnerItem it, AreaDraw area, Poly parent,
                           decimal absX, decimal absY, decimal w, decimal h,
                           bool allowRehome = false)
        {
            if (!allowRehome)
            {
                // DURANTE DRAG: no clamp a ningún polígono
                it.abs_x = absX;
                it.abs_y = absY;
                it.ancho_m = w;
                it.alto_m = h;

                // Mantén coords relativas coherentes al padre “visual” del frame
                it.eje_x_rel_m = Math.Round(absX - parent.x_m, 3, MidpointRounding.AwayFromZero);
                it.eje_y_rel_m = Math.Round(absY - parent.y_m, 3, MidpointRounding.AwayFromZero);
                // OJO: NO tocar area_poly_id aquí.
                return;
            }

            // DROP: clamp suave al padre definitivo y re-home real
            var (cx, cy) = ClampRectInSoft(parent, absX, absY, w, h, EPS_JOIN);

            it.abs_x = cx;
            it.abs_y = cy;
            it.ancho_m = w;
            it.alto_m = h;

            // Recalcula relativas respecto al padre final
            it.eje_x_rel_m = Math.Round(cx - parent.x_m, 3, MidpointRounding.AwayFromZero);
            it.eje_y_rel_m = Math.Round(cy - parent.y_m, 3, MidpointRounding.AwayFromZero);

            if (!string.Equals(it.area_poly_id, parent.poly_id, StringComparison.OrdinalIgnoreCase))
            {
                it.area_poly_id = parent.poly_id;
            }
        }




        private async Task CrearInstalacionParaSeleccion()
        {
            if (_selIn is null || !_mapIn.TryGetValue(_selIn, out var it)) return;

            var newId = $"ins_{Guid.NewGuid():N}".Substring(0, 12);

            // TIP: si tienes una tabla de tipos, reemplaza null por el id “default”
            var tipoId = (string?)null;

            Pg.UseSheet("instalaciones");
            var toSave = new Dictionary<string, object>
            {
                ["instalacion_id"] = newId,
                ["nombre"] = "Nueva instalación",
                ["tipo_id"] = (object?)tipoId ?? DBNull.Value,
                ["notas"] = DBNull.Value,
                ["requiere_mantenimiento"] = false
            };
            await Pg.CreateAsync(toSave);

            it.instalacion_id = newId;
            _instalaciones[newId] = new Instalacion
            {
                instalacion_id = newId,
                nombre = "Nueva instalación",
                tipo_id = tipoId,
                notas = null,
                requiere_mantenimiento = false
            };

            StateHasChanged();
        }

        private async Task CrearTipoInstalacion()
        {
            try
            {
                Pg.UseSheet("instalaciones_tipo");
                var newId = $"tipo_{Guid.NewGuid():N}".Substring(0, 12);

                var toSave = new Dictionary<string, object>
                {
                    ["tipo_id"] = newId,
                    ["nombre"] = "Tipo nuevo",
                    ["notas"] = DBNull.Value
                };
                await Pg.CreateAsync(toSave);

                _saveMsg = "Tipo de instalación creado ✔";
            }
            catch
            {
                _saveMsg = "No se pudo crear el tipo en 'instalaciones_tipo'.";
            }
            finally
            {
                StateHasChanged();
            }
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


       

        // ---- Re-home helpers para moverte entre polígonos de la misma área
        private static (decimal L, decimal T, decimal R, decimal B) Bounds(Poly p)
            => (p.x_m, p.y_m, p.x_m + p.ancho_m, p.y_m + p.alto_m);

        private static decimal RectIntersectArea(
            (decimal L, decimal T, decimal R, decimal B) a,
            (decimal L, decimal T, decimal R, decimal B) b)
        {
            var x1 = Math.Max(a.L, b.L);
            var y1 = Math.Max(a.T, b.T);
            var x2 = Math.Min(a.R, b.R);
            var y2 = Math.Min(a.B, b.B);
            var w = x2 - x1;
            var h = y2 - y1;
            return (w > 0m && h > 0m) ? (w * h) : 0m;
        }

        private static bool RectInside((decimal L, decimal T, decimal R, decimal B) outer,
                                       decimal x, decimal y, decimal w, decimal h, decimal pad = 0m)
        {
            return (x >= outer.L - pad) && (y >= outer.T - pad)
                && (x + w <= outer.R + pad) && (y + h <= outer.B + pad);
        }

        // Devuelve el polígono del área que mejor “contiene” el rect (x,y,w,h) en absolutos.
        // Preferimos el que lo contiene totalmente; si ninguno, el de mayor solape.
        private Poly BestPolyForRect(AreaDraw area, decimal absX, decimal absY, decimal w, decimal h)
        {
            Poly? best = null;
            decimal bestScore = -1m;

            var rect = (L: absX, T: absY, R: absX + w, B: absY + h);

            foreach (var p in area.Polys)
            {
                var pb = Bounds(p);

                // primero: ¿cabe completo?
                if (RectInside(pb, absX, absY, w, h, 0m))
                {
                    // puntuamos por “margen” (más grande = mejor, para evitar bordes)
                    var margin = Math.Min(
                        Math.Min(absX - pb.L, pb.R - (absX + w)),
                        Math.Min(absY - pb.T, pb.B - (absY + h))
                    );
                    var score = 1_000_000m + Math.Max(0m, margin);
                    if (score > bestScore) { bestScore = score; best = p; }
                    continue;
                }

                // si no cabe, medimos solape
                var inter = RectIntersectArea(pb, rect);
                if (inter > bestScore)
                {
                    bestScore = inter;
                    best = p;
                }
            }

            // Como fallback, si por alguna razón no hay ninguno, vuelve al primero
            return best ?? area.Polys[0];
        }

        // Reubica el inner en el polígono adecuado según una posición absoluta deseada.
        // Hace el clamp en ese nuevo padre y actualiza _dragParent.
        private void RehomeAndClampInner(InnerItem it, decimal desiredAbsX, decimal desiredAbsY)
        {
            if (_area is null) return;

            // Elegir el mejor padre para esa posición
            var newParent = BestPolyForRect(_area, desiredAbsX, desiredAbsY, it.ancho_m, it.alto_m);

            // Coordenadas relativas al NUEVO padre
            var rx = desiredAbsX - newParent.x_m;
            var ry = desiredAbsY - newParent.y_m;

            // Clamp dentro del NUEVO padre
            var minX = 0m; var minY = 0m;
            var maxX = Math.Max(0m, newParent.ancho_m - it.ancho_m);
            var maxY = Math.Max(0m, newParent.alto_m - it.alto_m);

            it.eje_x_rel_m = Clamp(minX, maxX, rx);
            it.eje_y_rel_m = Clamp(minY, maxY, ry);

            // Absolutos a partir del nuevo padre
            it.abs_x = newParent.x_m + it.eje_x_rel_m;
            it.abs_y = newParent.y_m + it.eje_y_rel_m;

            // Actualiza el padre de drag
            _dragParent = newParent;

            // Normalización final por si el tamaño cambió en otro flujo
            NormalizeInner(it);
        }

        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "sin-area";
            var normalized = s.Trim().ToLowerInvariant();
            var formD = normalized.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in formD)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
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
        private static bool PointInsidePoly(Poly p, decimal x, decimal y, decimal pad = 0m)
        {
            return x >= p.x_m - pad && x <= p.x_m + p.ancho_m + pad
                && y >= p.y_m - pad && y <= p.y_m + p.alto_m + pad;
        }

        // Tamaño de fuente ajustado a alto y ancho del elemento interior
        private static decimal FitInnerText(InnerItem it)
        {
            var pad = 0.10m;
            var w = Math.Max(0.10m, it.ancho_m - 2 * pad);
            var h = Math.Max(0.10m, it.alto_m - 2 * pad);
            var len = Math.Max(1, it.label?.Length ?? 1);

            // La altura manda, pero limitamos por ancho para textos largos
            var fsByH = h * 0.60m;
            var fsByW = (decimal)w / (decimal)(0.65 * len);
            var fs = Math.Min(fsByH, fsByW);

            return Clamp(0.18m, 5m, fs);
        }

        // ¿Cabe completamente el rectángulo (absX,absY,w,h) dentro de p?
        private static bool RectFitsIn(Poly p, decimal absX, decimal absY, decimal w, decimal h, decimal eps = 0.0005m)
        {
            var L = p.x_m + eps; var T = p.y_m + eps;
            var R = p.x_m + p.ancho_m - eps; var B = p.y_m + p.alto_m - eps;
            return absX >= L && absY >= T && (absX + w) <= R && (absY + h) <= B;
        }

        // Devuelve la mejor posición clamp dentro de p para un rectángulo (absX,absY,w,h)
        private static (decimal x, decimal y) ClampRectIn(Poly p, decimal absX, decimal absY, decimal w, decimal h)
        {
            var minX = p.x_m; var minY = p.y_m;
            var maxX = p.x_m + Math.Max(0m, p.ancho_m - w);
            var maxY = p.y_m + Math.Max(0m, p.alto_m - h);
            return (Clamp(minX, maxX, absX), Clamp(minY, maxY, absY));
        }

        // Elige el polígono destino para el inner: 1) el que LO CONTIENE completo
        // 2) si ninguno lo contiene, el que deja el centro más cerca tras hacer clamp.
        private Poly PickPolyForInnerRect(AreaDraw area, decimal targetAbsX, decimal targetAbsY, decimal w, decimal h)
        {
            // 1) preferir el que lo contiene
            foreach (var p in area.Polys)
                if (RectFitsIn(p, targetAbsX, targetAbsY, w, h)) return p;

            // 2) si no cabe completo en ninguno, escoger el mejor clamp por distancia al centro deseado
            var cx = targetAbsX + w / 2m; var cy = targetAbsY + h / 2m;
            Poly? best = null; double bestDist2 = double.MaxValue;

            foreach (var p in area.Polys)
            {
                var (nx, ny) = ClampRectIn(p, targetAbsX, targetAbsY, w, h);
                var ncx = nx + w / 2m; var ncy = ny + h / 2m;
                var d2 = (double)((ncx - cx) * (ncx - cx) + (ncy - cy) * (ncy - cy));
                if (d2 < bestDist2) { bestDist2 = d2; best = p; }
            }
            return best ?? area.Polys[0];
        }




    }
}

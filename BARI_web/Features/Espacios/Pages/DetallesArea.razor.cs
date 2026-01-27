using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using BARI_web.General_Services.DataBaseConnection;
using System;
using Npgsql;

namespace BARI_web.Features.Espacios.Pages
{
    public partial class DetallesArea : ComponentBase
    {
        [Parameter] public string AreaSlug { get; set; } = "";
        [Inject] private PgCrud Pg { get; set; } = default!;
        [Inject] private NpgsqlDataSource DataSource { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;

        private const string MesonDetailsRouteTemplate = "/detalles/{0}";

        // ✅ Instalaciones (antes apuntaba a inventario de materiales)
        private const string InstalacionDetailsRouteTemplate = "/inventario-instalaciones/item/{0}";

        // ====== modelos ======
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

        private class InnerItem
        {
            public string poly_in_id { get; set; } = "";
            public string area_poly_id { get; set; } = "";
            public decimal eje_x_rel_m { get; set; }
            public decimal eje_y_rel_m { get; set; }
            public decimal ancho_m { get; set; }
            public decimal largo_m { get; set; }
            public string label { get; set; } = "";
            public string fill { get; set; } = "#4B5563";
            public decimal opacidad { get; set; } = 0.35m;
            public decimal offset_x_m { get; set; } = 0m;
            public decimal offset_y_m { get; set; } = 0m;
            public string? meson_id { get; set; }
            public string? instalacion_id { get; set; }
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
        }

        private class BlockItem
        {
            public string bloque_id { get; set; } = "";
            public string canvas_id { get; set; } = "";

            // ✅ antes: material_id (montaje). Ahora: instalacion_id
            public string? instalacion_id { get; set; }

            public string? meson_id { get; set; }
            public string? etiqueta { get; set; }
            public string? color_hex { get; set; }
            public int z_order { get; set; }
            public decimal pos_x { get; set; }
            public decimal pos_y { get; set; }
            public decimal ancho { get; set; }
            public decimal largo { get; set; }      // 2D (Y) en el canvas
            public decimal? altura { get; set; }    // 3D (opcional, no necesariamente se dibuja)
            public decimal offset_x { get; set; }
            public decimal offset_y { get; set; }
            public decimal abs_x { get; set; }
            public decimal abs_y { get; set; }
        }

        private class Door
        {
            public string door_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }
            public string? area_id_b { get; set; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E"; // E/W/N/S (eje)
            public decimal largo_m { get; set; } = 1.0m;
        }

        private class Win
        {
            public string win_id { get; set; } = "";
            public string canvas_id { get; set; } = "";
            public string? area_id_a { get; set; }
            public string? area_id_b { get; set; }
            public decimal x_m { get; set; }
            public decimal y_m { get; set; }
            public string orientacion { get; set; } = "E";
            public decimal largo_m { get; set; } = 1.0m;
        }

        // ===== estado =====
        private bool IsLoading { get; set; } = true;
        private CanvasLab? _canvas;
        private AreaDraw? _area;
        private AreaInfo? _areaInfo;

        private readonly List<InnerItem> _inners = new();
        private readonly List<BlockItem> _blocks = new();
        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();
        private readonly List<EquipoItem> _equipos = new();
        private readonly List<InstalacionItem> _instalaciones = new();
        private readonly List<SustanciaItem> _sustancias = new();

        private readonly List<SelectOption> _equiposDisponibles = new();
        private readonly List<SelectOption> _instalacionesDisponibles = new();
        private readonly List<SelectOption> _contenedoresDisponibles = new();

        private readonly Dictionary<string, string> _mesonesLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _instalacionesLookup = new(StringComparer.OrdinalIgnoreCase);

        private string? _selectedEquipoId;
        private string? _selectedInstalacionId;
        private string? _selectedContenedorId;
        private string? _assignMsg;

        private string? _nuevoEquipoModeloId;
        private string? _nuevoEquipoSerie;
        private string? _nuevoEquipoEstadoId;
        private string? _nuevoEquipoPosicion;
        private bool _nuevoEquipoRequiereCalibracion;
        private string? _nuevoEquipoMsg;

        private string? _nuevoInstalacionNombre;
        private string? _nuevoInstalacionSubcategoriaId;
        private string? _nuevoInstalacionEstadoId;
        private string? _nuevoInstalacionMsg;

        private string? _nuevoSustanciaId;
        private string? _nuevoSustanciaNombreComercial;
        private string? _nuevoSustanciaNombreQuimico;
        private string? _nuevoContenedorId;
        private string? _nuevoContenedorCantidad;
        private string? _nuevoSustanciaMsg;

        private string? _nuevoDocumentoTitulo;
        private string? _nuevoDocumentoUrl;
        private string? _nuevoDocumentoArchivoLocal;
        private string? _nuevoDocumentoNotas;
        private string? _nuevoDocumentoMsg;

        // etiqueta override tomada del rectángulo interior del mesón
        private readonly Dictionary<string, string> _mesonLabelFromInner = new(StringComparer.OrdinalIgnoreCase);

        // ===== apariencia / tolerancias =====
        private const decimal OutlineStroke = 0.28m;
        private const decimal TextPad = 0.20m;
        private const decimal Tolerance = 0.004m;
        private static decimal RoundToTolerance(decimal v) => Math.Round(v / Tolerance) * Tolerance;

        // canvas dims
        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.largo_m ?? 10m;

        // viewbox
        private decimal VX, VY, VW, VH;
        private string ViewBox() => $"{S(VX)} {S(VY)} {S(VW)} {S(VH)}";

        private string AspectRatioString()
        {
            var vw = VW <= 0m ? 1m : VW;
            var vh = VH <= 0m ? 1m : VH;
            var ar = (double)vw / (double)vh;
            return $"{ar:0.###} / 1";
        }

        // grilla cache
        private decimal GridStartX, GridEndX, GridStartY, GridEndY;

        private sealed record AreaInfo(
            string area_id,
            string nombre,
            decimal? altura_m,
            decimal? area_total_m2,
            string? anotaciones,
            string? planta_id,
            string? canvas_id,
            int laboratorio_id
        );

        private sealed record EquipoItem(
            string equipo_id,
            string nombre_modelo,
            string serie,
            string estado_id,
            string posicion
        );

        private sealed record InstalacionItem(
            string instalacion_id,
            string nombre,
            string detalle
        );

        private sealed record SustanciaItem(
            string cont_id,
            string sustancia_id,
            string nombre,
            string cantidad,
            string proveedor
        );

        private sealed record SelectOption(string id, string label);

        // ===== Ciclo de vida =====
        protected override async Task OnInitializedAsync()
        {
            try
            {
                IsLoading = true;

                // area_id desde el slug
                var targetAreaId = await ResolveAreaIdFromSlug(AreaSlug);

                _areaInfo = await LoadAreaInfoAsync(targetAreaId);
                await LoadCanvasAsync(_areaInfo?.canvas_id);

                if (_canvas is null)
                {
                    SetDefaultViewBox();
                    return;
                }

                // Polígonos del área y construcción del AreaDraw
                var polys = await LoadPolysForAreaAsync(targetAreaId);
                if (polys.Count == 0)
                {
                    _area = null;
                    SetDefaultViewBox();
                    return;
                }

                var a = BuildAreaDrawFromPolys(targetAreaId, polys);
                BuildAreaOutline(a);
                _area = a;

                // ViewBox ajustado al área
                FitViewBoxToAreaWithAspect(a, pad: 0.25m);

                // grilla
                GridStartX = (decimal)Math.Floor((double)VX);
                GridEndX = (decimal)Math.Ceiling((double)(VX + VW));
                GridStartY = (decimal)Math.Floor((double)VY);
                GridEndY = (decimal)Math.Ceiling((double)(VY + VH));

                // interiores / puertas / ventanas / mesones
                await LoadInnerItemsForArea(a);
                await LoadMesonesForArea(targetAreaId); // ✅ primero
                await LoadBlocksForArea(a);             // ✅ después
                await LoadDoorsAndWindowsForArea(a);

                if (_areaInfo is not null)
                {
                    await LoadEquiposAsync(targetAreaId);
                    await LoadInstalacionesAsync(targetAreaId);
                    await LoadSustanciasAsync(targetAreaId);
                    await LoadDisponiblesAsync(_areaInfo.laboratorio_id, targetAreaId);
                }
            }
            finally
            {
                IsLoading = false;
                StateHasChanged();
            }
        }

        // ====== NAVEGACIÓN (CLICK EN SVG + LISTAS) ======

        // Úsalo desde el .razor para fila completa de mesón
        

        // Úsalo desde el .razor para fila completa de instalación
        private void GoToInstalacion(InstalacionItem item) => GoToInstalacionById(item.instalacion_id);

        // Úsalo desde el .razor para click en bloques (mesón/instalación)
        private void OnSvgItemClick(BlockItem b)
        {
            if (!string.IsNullOrWhiteSpace(b.instalacion_id))
            {
                GoToInstalacionById(b.instalacion_id!);
                return;
            }

            if (!string.IsNullOrWhiteSpace(b.meson_id))
            {
                GoToMesonById(b.meson_id!);
                return;
            }

            // fallback: etiqueta como nombre
            if (!string.IsNullOrWhiteSpace(b.etiqueta))
            {
                GoToMesonByName(b.etiqueta!);
                return;
            }
        }

        // Úsalo desde el .razor para click en inner (mesón/instalación)
        private void OnSvgItemClick(InnerItem it)
        {
            if (!string.IsNullOrWhiteSpace(it.meson_id))
            {
                GoToMesonById(it.meson_id!);
                return;
            }

            if (!string.IsNullOrWhiteSpace(it.instalacion_id))
            {
                GoToInstalacionById(it.instalacion_id!);
                return;
            }
        }

        // Cursor para SVG (pointer si es clickeable)
        private string SvgCursor(BlockItem b)
            => (!string.IsNullOrWhiteSpace(b.instalacion_id) || !string.IsNullOrWhiteSpace(b.meson_id) || !string.IsNullOrWhiteSpace(b.etiqueta))
               ? "pointer" : "default";

        private string SvgCursor(InnerItem it)
            => (!string.IsNullOrWhiteSpace(it.meson_id) || !string.IsNullOrWhiteSpace(it.instalacion_id))
               ? "pointer" : "default";


        private void GoToMesonByName(string mesonName)
        {
            if (string.IsNullOrWhiteSpace(mesonName)) return;
            var slug = Slugify(mesonName);
            Nav.NavigateTo(string.Format(CultureInfo.InvariantCulture, MesonDetailsRouteTemplate, slug));
        }

        private void GoToInstalacionById(string instalacionId)
        {
            if (string.IsNullOrWhiteSpace(instalacionId)) return;
            Nav.NavigateTo(string.Format(CultureInfo.InvariantCulture, InstalacionDetailsRouteTemplate, instalacionId));
        }

        // ===== Carga / Construcción =====

        private async Task LoadCanvasAsync(string? canvasId)
        {
            Pg.UseSheet("canvas_lab");
            var canvases = await Pg.ReadAllAsync();
            var c = canvases.FirstOrDefault(row => string.Equals(Get(row, "canvas_id"), canvasId ?? "", StringComparison.OrdinalIgnoreCase))
                ?? canvases.FirstOrDefault();
            if (c is null) return;

            _canvas = new CanvasLab(
                c["canvas_id"], c["nombre"],
                Dec(c["ancho_m"]), Dec(c["largo_m"]), Dec(c["margen_m"])
            );
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

        // slug->area_id usando una sola lectura de "areas"
        private async Task<string> ResolveAreaIdFromSlug(string slugFromUrl)
        {
            var slug = Slugify((slugFromUrl ?? "").Trim());
            var candidateId = slug.Replace('-', '_');

            Pg.UseSheet("areas");
            var rows = await Pg.ReadAllAsync();

            // Primero busca por nombre_areas normalizado
            foreach (var r in rows)
            {
                var name = NullIfEmpty(Get(r, "nombre_areas"));
                var aid = Get(r, "area_id");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var nameSlug = Slugify(name).Replace('-', '_');
                if (string.Equals(nameSlug, candidateId, StringComparison.OrdinalIgnoreCase))
                    return aid;
            }

            // Si no, usa el propio candidate como area_id
            return candidateId;
        }

        private async Task<List<Poly>> LoadPolysForAreaAsync(string targetAreaId)
        {
            var polys = new List<Poly>();
            if (_canvas is null) return polys;

            Pg.UseSheet("poligonos");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas.canvas_id, StringComparison.OrdinalIgnoreCase))
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

        private AreaDraw BuildAreaDrawFromPolys(string areaId, List<Poly> polys)
        {
            var a = new AreaDraw { AreaId = areaId };
            var ordered = polys.OrderBy(p => p.z_order).ToList();
            a.Polys.AddRange(ordered);

            a.MinX = ordered.Min(p => p.puntos.Min(pt => pt.X));
            a.MinY = ordered.Min(p => p.puntos.Min(pt => pt.Y));
            a.MaxX = ordered.Max(p => p.puntos.Max(pt => pt.X));
            a.MaxY = ordered.Max(p => p.puntos.Max(pt => pt.Y));

            decimal sx = 0, sy = 0, sa = 0;
            foreach (var p in ordered)
            {
                var area = PolygonArea(p.puntos);
                if (area <= 0) continue;
                var centroid = PolygonCentroid(p.puntos);
                sx += centroid.x * area;
                sy += centroid.y * area;
                sa += area;
            }
            if (sa > 0) { a.Cx = sx / sa; a.Cy = sy / sa; }
            else { a.Cx = (a.MinX + a.MaxX) / 2m; a.Cy = (a.MinY + a.MaxY) / 2m; }

            var etiquetaPoly = ordered.Select(p => p.etiqueta).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var nombreArea = _areaInfo?.nombre;
            a.Label = (etiquetaPoly ?? nombreArea ?? areaId).ToUpperInvariant();
            a.Fill = ordered.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";

            return a;
        }

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

        private async Task LoadEquiposAsync(string areaId)
        {
            _equipos.Clear();
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                SELECT e.equipo_id,
                       COALESCE(m.nombre_modelo, '') AS nombre_modelo,
                       COALESCE(e.serie, '') AS serie,
                       COALESCE(e.estado_id, '') AS estado_id,
                       COALESCE(e.posicion, '') AS posicion
                FROM equipos e
                LEFT JOIN modelos_equipo m ON m.modelo_id = e.modelo_id
                WHERE e.area_id = @area_id
                ORDER BY nombre_modelo, e.equipo_id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", areaId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                _equipos.Add(new EquipoItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)
                ));
            }
        }

        private async Task LoadInstalacionesAsync(string areaId)
        {
            _instalaciones.Clear();
            _instalacionesLookup.Clear();

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sql = @"
                SELECT i.instalacion_id,
                       i.nombre,
                       COALESCE(sc.nombre, COALESCE(i.subcategoria_id, '')) AS detalle
                FROM instalaciones i
                LEFT JOIN subcategorias sc ON sc.subcategoria_id = i.subcategoria_id
                WHERE i.area_id = @area_id
                ORDER BY i.nombre, i.instalacion_id;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", areaId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var nombre = reader.GetString(1);
                var detalle = reader.IsDBNull(2) ? "" : reader.GetString(2);

                _instalacionesLookup[id] = nombre;
                _instalaciones.Add(new InstalacionItem(id, nombre, detalle));
            }
        }

        private async Task LoadSustanciasAsync(string areaId)
        {
            _sustancias.Clear();
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                SELECT c.cont_id,
                       s.sustancia_id,
                       COALESCE(NULLIF(s.nombre_comercial, ''),
                                NULLIF(s.nombre_quimico, ''),
                                s.sustancia_id) AS nombre,
                       COALESCE(c.cantidad_reactivo_actual, c.cantidad_reactivo_nominal, 0)::text AS cantidad,
                       COALESCE(c.proveedor, '') AS proveedor
                FROM contenedores c
                JOIN sustancias s ON s.sustancia_id = c.sustancia_id
                WHERE c.area_id = @area_id
                ORDER BY nombre";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", areaId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                _sustancias.Add(new SustanciaItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)
                ));
            }
        }
        private sealed record TextLayout(decimal FontSize, decimal LineHeight, List<string> Lines);

        private static bool ShouldRotate(decimal w, decimal h)
        {
            if (w <= 0) return false;
            var ratio = h / w;
            return ratio >= 1.6m; // 👈 ajusta si quieres (1.4–2.0)
        }

        private TextLayout LayoutLabel(string text, decimal boxW, decimal boxH)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return new TextLayout(0.18m, 1.15m, new List<string>());

            var pad = 0.18m;
            var availW = Math.Max(0.10m, boxW - pad);
            var availH = Math.Max(0.10m, boxH - pad);

            var lineHeight = 1.15m;

            var maxFs = Math.Min(0.45m, Math.Min(availW, availH) * 0.45m);

            // ✅ baja el mínimo para que de verdad intente encajar
            var minFs = 0.06m;

            // ✅ paso más fino (se nota mucho en cajas pequeñas)
            var step = 0.01m;


            for (var fs = maxFs; fs >= minFs; fs -= step)
            {
                var lines = WrapByWidth(text, availW, fs);
                var neededH = lines.Count * (fs * lineHeight);

                if (neededH <= availH)
                    return new TextLayout(fs, lineHeight, lines);
            }

            // ✅ Fallback: devuelve TODO lo que se pueda (sin "…").
            // Si aún no entra, que el clip lo recorte, pero sin modificar el texto.
            var fallbackLines = WrapByWidth(text, availW, minFs);
            return new TextLayout(minFs, lineHeight, fallbackLines);

        }

        private List<string> WrapByWidth(string text, decimal widthM, decimal fontSizeM)
        {
            var charW = fontSizeM * 0.55m; // aproximación
            var maxChars = (int)Math.Max(1, Math.Floor(widthM / charW));

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var current = "";

            foreach (var w in words)
            {
                if (current.Length == 0) current = w;
                else if ((current.Length + 1 + w.Length) <= maxChars) current += " " + w;
                else { lines.Add(current); current = w; }

                while (current.Length > maxChars)
                {
                    lines.Add(current.Substring(0, maxChars));
                    current = current.Substring(maxChars);
                }
            }

            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        private async Task LoadDisponiblesAsync(int laboratorioId, string areaId)
        {
            _equiposDisponibles.Clear();
            _instalacionesDisponibles.Clear();
            _contenedoresDisponibles.Clear();

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sqlEquipos = @"
                SELECT e.equipo_id,
                       COALESCE(m.nombre_modelo, '') AS label
                FROM equipos e
                LEFT JOIN modelos_equipo m ON m.modelo_id = e.modelo_id
                WHERE e.laboratorio_id = @lab
                  AND (e.area_id IS NULL OR e.area_id <> @area_id)
                ORDER BY label, e.equipo_id";

            await using (var cmd = new NpgsqlCommand(sqlEquipos, conn))
            {
                cmd.Parameters.AddWithValue("lab", laboratorioId);
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    _equiposDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
            }

            const string sqlInst = @"
                SELECT instalacion_id, nombre
                FROM instalaciones
                WHERE laboratorio_id = @lab
                  AND (area_id IS NULL OR area_id <> @area_id)
                ORDER BY nombre, instalacion_id";

            await using (var cmd = new NpgsqlCommand(sqlInst, conn))
            {
                cmd.Parameters.AddWithValue("lab", laboratorioId);
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    _instalacionesDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
            }

            const string sqlCont = @"
                SELECT cont_id, cont_id
                FROM contenedores
                WHERE laboratorio_id = @lab
                  AND (area_id IS NULL OR area_id <> @area_id)
                ORDER BY cont_id";

            await using var contCmd = new NpgsqlCommand(sqlCont, conn);
            contCmd.Parameters.AddWithValue("lab", laboratorioId);
            contCmd.Parameters.AddWithValue("area_id", areaId);
            await using var contReader = await contCmd.ExecuteReaderAsync();
            while (await contReader.ReadAsync())
                _contenedoresDisponibles.Add(new SelectOption(contReader.GetString(0), contReader.GetString(1)));
        }

        private async Task AssignEquipoAsync()
        {
            if (_areaInfo is null || string.IsNullOrWhiteSpace(_selectedEquipoId)) return;
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = "UPDATE equipos SET area_id = @area_id WHERE equipo_id = @id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", _areaInfo.area_id);
            cmd.Parameters.AddWithValue("id", _selectedEquipoId);
            await cmd.ExecuteNonQueryAsync();
            _selectedEquipoId = null;
            await LoadEquiposAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);
            _assignMsg = "Equipo asignado.";
        }

        private async Task SubirEquipoAsync()
        {
            if (_areaInfo is null) return;
            if (string.IsNullOrWhiteSpace(_nuevoEquipoModeloId) && string.IsNullOrWhiteSpace(_nuevoEquipoSerie))
            {
                _nuevoEquipoMsg = "Completa al menos modelo o serie.";
                return;
            }

            var equipoId = $"eq_{Guid.NewGuid():N}".Substring(0, 12);
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                INSERT INTO equipos
                (equipo_id, modelo_id, serie, estado_id, area_id, posicion, requiere_calibracion, laboratorio_id)
                VALUES (@id, @modelo, @serie, @estado, @area, @posicion, @cal, @lab)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", equipoId);
            cmd.Parameters.AddWithValue("modelo", string.IsNullOrWhiteSpace(_nuevoEquipoModeloId) ? (object)DBNull.Value : _nuevoEquipoModeloId!);
            cmd.Parameters.AddWithValue("serie", string.IsNullOrWhiteSpace(_nuevoEquipoSerie) ? (object)DBNull.Value : _nuevoEquipoSerie!);
            cmd.Parameters.AddWithValue("estado", string.IsNullOrWhiteSpace(_nuevoEquipoEstadoId) ? (object)DBNull.Value : _nuevoEquipoEstadoId!);
            cmd.Parameters.AddWithValue("area", _areaInfo.area_id);
            cmd.Parameters.AddWithValue("posicion", string.IsNullOrWhiteSpace(_nuevoEquipoPosicion) ? (object)DBNull.Value : _nuevoEquipoPosicion!);
            cmd.Parameters.AddWithValue("cal", _nuevoEquipoRequiereCalibracion);
            cmd.Parameters.AddWithValue("lab", _areaInfo.laboratorio_id);
            await cmd.ExecuteNonQueryAsync();

            _nuevoEquipoModeloId = null;
            _nuevoEquipoSerie = null;
            _nuevoEquipoEstadoId = null;
            _nuevoEquipoPosicion = null;
            _nuevoEquipoRequiereCalibracion = false;
            _nuevoEquipoMsg = "Equipo registrado.";
            await LoadEquiposAsync(_areaInfo.area_id);
        }

        private async Task AssignInstalacionAsync()
        {
            if (_areaInfo is null || string.IsNullOrWhiteSpace(_selectedInstalacionId)) return;

            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"UPDATE instalaciones SET area_id = @area_id WHERE instalacion_id = @id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", _areaInfo.area_id);
            cmd.Parameters.AddWithValue("id", _selectedInstalacionId);
            await cmd.ExecuteNonQueryAsync();

            _selectedInstalacionId = null;

            await LoadInstalacionesAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);

            _assignMsg = "Instalación asignada.";
        }

        private async Task SubirInstalacionAsync()
        {
            if (_areaInfo is null) return;
            if (string.IsNullOrWhiteSpace(_nuevoInstalacionNombre))
            {
                _nuevoInstalacionMsg = "Ingresa el nombre de la instalación.";
                return;
            }

            var instalacionId = $"inst_{Guid.NewGuid():N}".Substring(0, 12);

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sql = @"
                INSERT INTO instalaciones
                    (instalacion_id, nombre, subcategoria_id, estado_id, laboratorio_id, area_id)
                VALUES
                    (@id, @nombre, @subcat, @estado, @lab, @area);";

            await using var cmd = new NpgsqlCommand(sql, conn);

            cmd.Parameters.AddWithValue("id", instalacionId);
            cmd.Parameters.AddWithValue("nombre", _nuevoInstalacionNombre!.Trim());
            cmd.Parameters.AddWithValue("subcat", string.IsNullOrWhiteSpace(_nuevoInstalacionSubcategoriaId) ? (object)DBNull.Value : _nuevoInstalacionSubcategoriaId!);
            cmd.Parameters.AddWithValue("estado", string.IsNullOrWhiteSpace(_nuevoInstalacionEstadoId) ? (object)DBNull.Value : _nuevoInstalacionEstadoId!);
            cmd.Parameters.AddWithValue("lab", _areaInfo.laboratorio_id);
            cmd.Parameters.AddWithValue("area", _areaInfo.area_id);

            await cmd.ExecuteNonQueryAsync();

            _nuevoInstalacionNombre = null;
            _nuevoInstalacionSubcategoriaId = null;
            _nuevoInstalacionEstadoId = null;

            _nuevoInstalacionMsg = "Instalación registrada.";
            await LoadInstalacionesAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);
        }

        private async Task AssignContenedorAsync()
        {
            if (_areaInfo is null || string.IsNullOrWhiteSpace(_selectedContenedorId)) return;
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = "UPDATE contenedores SET area_id = @area_id WHERE cont_id = @id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", _areaInfo.area_id);
            cmd.Parameters.AddWithValue("id", _selectedContenedorId);
            await cmd.ExecuteNonQueryAsync();
            _selectedContenedorId = null;
            await LoadSustanciasAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);
            _assignMsg = "Contenedor asignado.";
        }

        private async Task SubirSustanciaAsync()
        {
            if (_areaInfo is null) return;

            var sustanciaId = string.IsNullOrWhiteSpace(_nuevoSustanciaId)
                ? $"sus_{Guid.NewGuid():N}".Substring(0, 12)
                : _nuevoSustanciaId!.Trim();

            var contenedorId = string.IsNullOrWhiteSpace(_nuevoContenedorId)
                ? $"cont_{Guid.NewGuid():N}".Substring(0, 12)
                : _nuevoContenedorId!.Trim();

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sqlSust = @"
                INSERT INTO sustancias (sustancia_id, nombre_comercial, nombre_quimico, laboratorio_id)
                VALUES (@id, @comercial, @quimico, @lab)
                ON CONFLICT (sustancia_id) DO NOTHING";

            await using (var cmd = new NpgsqlCommand(sqlSust, conn))
            {
                cmd.Parameters.AddWithValue("id", sustanciaId);
                cmd.Parameters.AddWithValue("comercial", string.IsNullOrWhiteSpace(_nuevoSustanciaNombreComercial) ? (object)DBNull.Value : _nuevoSustanciaNombreComercial!);
                cmd.Parameters.AddWithValue("quimico", string.IsNullOrWhiteSpace(_nuevoSustanciaNombreQuimico) ? (object)DBNull.Value : _nuevoSustanciaNombreQuimico!);
                cmd.Parameters.AddWithValue("lab", _areaInfo.laboratorio_id);
                await cmd.ExecuteNonQueryAsync();
            }

            const string sqlCont = @"
                INSERT INTO contenedores (cont_id, sustancia_id, cantidad_reactivo_actual, area_id, laboratorio_id)
                VALUES (@cont, @sus, @cantidad, @area, @lab)";

            await using (var cmd = new NpgsqlCommand(sqlCont, conn))
            {
                cmd.Parameters.AddWithValue("cont", contenedorId);
                cmd.Parameters.AddWithValue("sus", sustanciaId);
                var cantidad = decimal.TryParse(_nuevoContenedorCantidad, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ? qty : 0m;
                cmd.Parameters.AddWithValue("cantidad", cantidad);
                cmd.Parameters.AddWithValue("area", _areaInfo.area_id);
                cmd.Parameters.AddWithValue("lab", _areaInfo.laboratorio_id);
                await cmd.ExecuteNonQueryAsync();
            }

            _nuevoSustanciaId = null;
            _nuevoSustanciaNombreComercial = null;
            _nuevoSustanciaNombreQuimico = null;
            _nuevoContenedorId = null;
            _nuevoContenedorCantidad = null;
            _nuevoSustanciaMsg = "Sustancia registrada.";
            await LoadSustanciasAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);
        }

        private async Task SubirDocumentoAsync()
        {
            if (_areaInfo is null) return;
            if (string.IsNullOrWhiteSpace(_nuevoDocumentoTitulo))
            {
                _nuevoDocumentoMsg = "Ingresa un título para el documento.";
                return;
            }

            var docId = $"doc_{Guid.NewGuid():N}".Substring(0, 12);
            await using var conn = await DataSource.OpenConnectionAsync();
            const string sql = @"
                INSERT INTO documentos (documento_id, titulo, url, archivo_local, notas, laboratorio_id)
                VALUES (@id, @titulo, @url, @archivo, @notas, @lab)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", docId);
            cmd.Parameters.AddWithValue("titulo", _nuevoDocumentoTitulo!);
            cmd.Parameters.AddWithValue("url", string.IsNullOrWhiteSpace(_nuevoDocumentoUrl) ? (object)DBNull.Value : _nuevoDocumentoUrl!);
            cmd.Parameters.AddWithValue("archivo", string.IsNullOrWhiteSpace(_nuevoDocumentoArchivoLocal) ? (object)DBNull.Value : _nuevoDocumentoArchivoLocal!);
            cmd.Parameters.AddWithValue("notas", string.IsNullOrWhiteSpace(_nuevoDocumentoNotas) ? (object)DBNull.Value : _nuevoDocumentoNotas!);
            cmd.Parameters.AddWithValue("lab", _areaInfo.laboratorio_id);
            await cmd.ExecuteNonQueryAsync();

            _nuevoDocumentoTitulo = null;
            _nuevoDocumentoUrl = null;
            _nuevoDocumentoArchivoLocal = null;
            _nuevoDocumentoNotas = null;
            _nuevoDocumentoMsg = "Documento registrado.";
        }

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

            if (vw > Wm || vh > Hm)
            {
                var scale = Math.Min(Wm / vw, Hm / vh);
                vw *= scale; vh *= scale;
                vx = Math.Max(0m, cx - vw / 2m);
                vy = Math.Max(0m, cy - vh / 2m);
                if (vx + vw > Wm) vx = Wm - vw;
                if (vy + vh > Hm) vy = Hm - vh;
            }

            VX = vx; VY = vy; VW = vw; VH = vh;
        }

        private void SetDefaultViewBox()
        {
            VX = 0m; VY = 0m; VW = Wm; VH = Hm;
            GridStartX = 0m; GridEndX = VW;
            GridStartY = 0m; GridEndY = VH;
        }

        // ====== carga vinculada al área ======
        private async Task LoadInnerItemsForArea(AreaDraw a)
        {
            _inners.Clear();
            _mesonLabelFromInner.Clear();

            var areaPolys = a.Polys.ToDictionary(p => p.poly_id, p => p, StringComparer.OrdinalIgnoreCase);

            try
            {
                Pg.UseSheet("poligonos_interiores");
                foreach (var r in await Pg.ReadAllAsync())
                {
                    var area_poly_id = Get(r, "area_poly_id");
                    if (!areaPolys.TryGetValue(area_poly_id, out var parentPoly)) continue;

                    var offset_x_m = Dec(Get(r, "offset_x_m", "0"));
                    var offset_y_m = Dec(Get(r, "offset_y_m", "0"));

                    var eje_x_rel_m = Dec(Get(r, "eje_x_rel_m", "0"));
                    var eje_y_rel_m = Dec(Get(r, "eje_y_rel_m", "0"));
                    var ancho_m = Dec(Get(r, "ancho_m", "0"));
                    var largo_m = Dec(Get(r, "largo_m", "0"));
                    var color_hex = NullIfEmpty(Get(r, "color_hex")) ?? "#4B5563";
                    var label = NullIfEmpty(Get(r, "etiqueta")) ?? "";

                    var meson_id = NullIfEmpty(Get(r, "meson_id"));
                    var instalacion_id = NullIfEmpty(Get(r, "instalacion_id"));

                    var opTxt = NullIfEmpty(Get(r, "opacidad_0_1"));
                    var op = 0.35m;
                    if (!string.IsNullOrEmpty(opTxt) && decimal.TryParse(opTxt, NumberStyles.Any, CultureInfo.InvariantCulture, out var opDec))
                        op = opDec;

                    // ✅ NO sumes offsets que el editor no usa
                    var abs_x = parentPoly.x_m + eje_x_rel_m;
                    var abs_y = parentPoly.y_m + eje_y_rel_m;


                    _inners.Add(new InnerItem
                    {
                        poly_in_id = Get(r, "poly_in_id"),
                        area_poly_id = area_poly_id,
                        eje_x_rel_m = eje_x_rel_m,
                        eje_y_rel_m = eje_y_rel_m,
                        ancho_m = ancho_m,
                        largo_m = largo_m,
                        label = label,
                        fill = color_hex,
                        opacidad = op,
                        offset_x_m = offset_x_m,
                        offset_y_m = offset_y_m,
                        meson_id = meson_id,
                        instalacion_id = instalacion_id,
                        abs_x = abs_x,
                        abs_y = abs_y
                    });

                    // Override del nombre del mesón si aplica (primer match gana)
                    if (!string.IsNullOrWhiteSpace(meson_id) && !string.IsNullOrWhiteSpace(label))
                    {
                        if (!_mesonLabelFromInner.ContainsKey(meson_id))
                            _mesonLabelFromInner[meson_id] = label;
                    }
                }
            }
            catch
            {
                _inners.Clear();
                _mesonLabelFromInner.Clear();
            }
        }

        private async Task LoadDoorsAndWindowsForArea(AreaDraw a)
        {
            _doors.Clear();
            _windows.Clear();

            static (string orient, decimal len) AxisAndLen(decimal x1, decimal y1, decimal x2, decimal y2)
            {
                if (Math.Abs((double)(x2 - x1)) >= Math.Abs((double)(y2 - y1)))
                {
                    var orient = (x2 >= x1) ? "E" : "W";
                    return (orient, Math.Abs(x2 - x1));
                }
                else
                {
                    var orient = (y2 >= y1) ? "S" : "N";
                    return (orient, Math.Abs(y2 - y1));
                }
            }

            Pg.UseSheet("puertas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas!.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (orient, len) = AxisAndLen(x1, y1, x2, y2);

                _doors.Add(new Door
                {
                    door_id = Get(r, "puerta_id"),
                    canvas_id = Get(r, "canvas_id"),
                    area_id_a = aA,
                    area_id_b = aB,
                    x_m = x1,
                    y_m = y1,
                    orientacion = orient,
                    largo_m = Math.Max(0.4m, len)
                });
            }

            Pg.UseSheet("ventanas");
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "canvas_id"), _canvas!.canvas_id, StringComparison.OrdinalIgnoreCase)) continue;

                var aA = NullIfEmpty(Get(r, "area_a"));
                var aB = NullIfEmpty(Get(r, "area_b"));
                var touches = string.Equals(aA ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(aB ?? "", a.AreaId, StringComparison.OrdinalIgnoreCase);
                if (!touches) continue;

                var x1 = Dec(Get(r, "x1_m", "0")); var y1 = Dec(Get(r, "y1_m", "0"));
                var x2 = Dec(Get(r, "x2_m", "0")); var y2 = Dec(Get(r, "y2_m", "0"));
                var (orient, len) = AxisAndLen(x1, y1, x2, y2);

                _windows.Add(new Win
                {
                    win_id = Get(r, "ventana_id"),
                    canvas_id = Get(r, "canvas_id"),
                    area_id_a = aA,
                    area_id_b = aB,
                    x_m = x1,
                    y_m = y1,
                    orientacion = orient,
                    largo_m = Math.Max(0.4m, len)
                });
            }
        }

        private async Task LoadBlocksForArea(AreaDraw a)
        {
            _blocks.Clear();
            if (_canvas is null) return;

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sql = @"
                SELECT b.bloque_id,
                       b.canvas_id,
                       b.instalacion_id,
                       b.meson_id,
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
            cmd.Parameters.AddWithValue("canvas_id", _canvas.canvas_id);
            cmd.Parameters.AddWithValue("area_id", a.AreaId);

            await using var reader = await cmd.ExecuteReaderAsync();

            var iBloqueId = reader.GetOrdinal("bloque_id");
            var iCanvasId = reader.GetOrdinal("canvas_id");
            var iInst = reader.GetOrdinal("instalacion_id");
            var iMeson = reader.GetOrdinal("meson_id");
            var iEtiqueta = reader.GetOrdinal("etiqueta");
            var iColor = reader.GetOrdinal("color_hex");
            var iZ = reader.GetOrdinal("z_order");
            var iPosX = reader.GetOrdinal("pos_x");
            var iPosY = reader.GetOrdinal("pos_y");

            var iAncho = reader.GetOrdinal("ancho");
            var ilargo = reader.GetOrdinal("largo");
            var iAltura = reader.GetOrdinal("altura");
            var iOffX = reader.GetOrdinal("offset_x");
            var iOffY = reader.GetOrdinal("offset_y");

            while (await reader.ReadAsync())
            {
                var offsetX = reader.IsDBNull(iOffX) ? 0m : reader.GetDecimal(iOffX);
                var offsetY = reader.IsDBNull(iOffY) ? 0m : reader.GetDecimal(iOffY);

                var posX = reader.IsDBNull(iPosX) ? 0m : reader.GetDecimal(iPosX);
                var posY = reader.IsDBNull(iPosY) ? 0m : reader.GetDecimal(iPosY);

                // ✅ centro del área como en el editor (bbox center, no centroid)
                var areaCenterX = (a.MinX + a.MaxX) / 2m;
                var areaCenterY = (a.MinY + a.MaxY) / 2m;

                // ✅ regla: pos_x/pos_y ya es ABS
                decimal absX = posX;
                decimal absY = posY;

                // ✅ fallback legacy: si pos viene "vacío", reconstruye con offset
                if (posX == 0m && posY == 0m && (offsetX != 0m || offsetY != 0m))
                {
                    absX = areaCenterX + offsetX;
                    absY = areaCenterY + offsetY;
                }




                var ancho = reader.IsDBNull(iAncho) ? 0.6m : reader.GetDecimal(iAncho);
                var largo = reader.IsDBNull(ilargo) ? 0.4m : reader.GetDecimal(ilargo);
                var altura = reader.IsDBNull(iAltura) ? (decimal?)null : reader.GetDecimal(iAltura);
                

                _blocks.Add(new BlockItem
                {
                    bloque_id = reader.GetString(iBloqueId),
                    canvas_id = reader.GetString(iCanvasId),
                    instalacion_id = reader.IsDBNull(iInst) ? null : reader.GetString(iInst),
                    meson_id = reader.IsDBNull(iMeson) ? null : reader.GetString(iMeson),
                    etiqueta = reader.IsDBNull(iEtiqueta) ? null : reader.GetString(iEtiqueta),
                    color_hex = reader.IsDBNull(iColor) ? "#2563eb" : reader.GetString(iColor),
                    z_order = reader.IsDBNull(iZ) ? 0 : reader.GetInt32(iZ),
                    pos_x = posX,
                    pos_y = posY,
                    ancho = ancho,
                    largo = largo,
                    altura = altura,
                    offset_x = offsetX,
                    offset_y = offsetY,
                    abs_x = absX,
                    abs_y = absY
                });
            }
        }

        // ===== outline =====
        private static void BuildAreaOutline(AreaDraw a)
        {
            if (a.Polys.Any(p => p.puntos.Count >= 3))
            {
                BuildAreaOutlineFromPoints(a);
                return;
            }

            var H = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var V = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var p in a.Polys)
            {
                var L = p.x_m; var T = p.y_m; var R = p.x_m + p.ancho_m; var B = p.y_m + p.largo_m;

                var yTop = RoundToTolerance(T);
                var yBot = RoundToTolerance(B);
                var xLft = RoundToTolerance(L);
                var xRgt = RoundToTolerance(R);

                var x1 = RoundToTolerance(Math.Min(L, R));
                var x2 = RoundToTolerance(Math.Max(L, R));
                var y1 = RoundToTolerance(Math.Min(T, B));
                var y2 = RoundToTolerance(Math.Max(T, B));

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
                foreach (var (a1, a2) in spans)
                {
                    var lo = Math.Min(a1, a2);
                    var hi = Math.Max(a1, a2);
                    xs.Add(lo); xs.Add(hi);
                }

                var xList = xs.ToList();
                for (int i = 0; i < xList.Count - 1; i++)
                {
                    var s = xList[i];
                    var e = xList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;

                    int count = 0;
                    foreach (var (a1, a2) in spans)
                    {
                        var lo = Math.Min(a1, a2);
                        var hi = Math.Max(a1, a2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }

                    if ((count % 2) == 1) a.Outline.Add((s, y, e, y));
                }
            }

            foreach (var (x, spans) in V)
            {
                if (spans.Count == 0) continue;
                var ys = new SortedSet<decimal>();
                foreach (var (b1, b2) in spans)
                {
                    var lo = Math.Min(b1, b2);
                    var hi = Math.Max(b1, b2);
                    ys.Add(lo); ys.Add(hi);
                }

                var yList = ys.ToList();
                for (int i = 0; i < yList.Count - 1; i++)
                {
                    var s = yList[i];
                    var e = yList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;

                    int count = 0;
                    foreach (var (b1, b2) in spans)
                    {
                        var lo = Math.Min(b1, b2);
                        var hi = Math.Max(b1, b2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }

                    if ((count % 2) == 1) a.Outline.Add((x, s, x, e));
                }
            }

            var merged = MergeCollinear(a.Outline);
            a.Outline.Clear();
            a.Outline.AddRange(merged);
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

        private static List<(decimal x1, decimal y1, decimal x2, decimal y2)> MergeCollinear(
            List<(decimal x1, decimal y1, decimal x2, decimal y2)> src)
        {
            var horizontals = new Dictionary<decimal, List<(decimal x1, decimal x2)>>();
            var verticals = new Dictionary<decimal, List<(decimal y1, decimal y2)>>();

            foreach (var s in src)
            {
                if (Math.Abs(s.y1 - s.y2) < Tolerance)
                {
                    var y = RoundToTolerance(s.y1);
                    var a = Math.Min(s.x1, s.x2);
                    var b = Math.Max(s.x1, s.x2);
                    if (!horizontals.ContainsKey(y)) horizontals[y] = new();
                    horizontals[y].Add((a, b));
                }
                else if (Math.Abs(s.x1 - s.x2) < Tolerance)
                {
                    var x = RoundToTolerance(s.x1);
                    var a = Math.Min(s.y1, s.y2);
                    var b = Math.Max(s.y1, s.y2);
                    if (!verticals.ContainsKey(x)) verticals[x] = new();
                    verticals[x].Add((a, b));
                }
            }

            List<(decimal x1, decimal y1, decimal x2, decimal y2)> outSegs = new();

            foreach (var kv in horizontals)
            {
                var y = kv.Key;
                var list = kv.Value.OrderBy(t => t.x1).ToList();
                if (list.Count == 0) continue;

                var curA = list[0].x1;
                var curB = list[0].x2;

                for (int i = 1; i < list.Count; i++)
                {
                    var a = list[i].x1;
                    var b = list[i].x2;
                    if (a <= curB + Tolerance) curB = Math.Max(curB, b);
                    else { outSegs.Add((curA, y, curB, y)); curA = a; curB = b; }
                }
                outSegs.Add((curA, y, curB, y));
            }

            foreach (var kv in verticals)
            {
                var x = kv.Key;
                var list = kv.Value.OrderBy(t => t.y1).ToList();
                if (list.Count == 0) continue;

                var curA = list[0].y1;
                var curB = list[0].y2;

                for (int i = 1; i < list.Count; i++)
                {
                    var a = list[i].y1;
                    var b = list[i].y2;
                    if (a <= curB + Tolerance) curB = Math.Max(curB, b);
                    else { outSegs.Add((x, curA, x, curB)); curA = a; curB = b; }
                }
                outSegs.Add((x, curA, x, curB));
            }

            return outSegs;
        }

        // ===== helpers =====
        private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        private static decimal Dec(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static int Int(string s) => int.TryParse(s, out var n) ? n : 0;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static string Get(Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;
        private static decimal Clamp(decimal min, decimal max, decimal v) => Math.Max(min, Math.Min(max, v));

        // Tamaño de fuente ajustado a largo y ancho del elemento interior
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

        private (decimal fs, decimal pillW, decimal pillH) FitLabel(AreaDraw a)
        {
            var bboxW = (a.MaxX - a.MinX);
            var bboxH = (a.MaxY - a.MinY);
            if (bboxW <= 0 || bboxH <= 0) return (0.3m, 1.5m, 0.6m);

            var targetW = Clamp(0.6m, bboxW, bboxW * 0.82m);
            var targetH = Clamp(0.35m, bboxH, bboxH * 0.26m);

            var len = Math.Max(1, a.Label.Length);
            var fsByW = (decimal)targetW / (decimal)(0.55 * len);
            var fsByH = (decimal)targetH * 0.65m;
            var fs = Clamp(0.28m, 3m, Math.Min(fsByW, fsByH));

            var pillH = Clamp(0.45m, targetH, fs * 1.9m);
            var pillW = Clamp(0.8m, targetW, (decimal)(0.55 * len) * fs + 2 * TextPad);

            return (fs, pillW, pillH);
        }

        // mismas reglas que en Areas.razor.cs (eje, no dirección)
        private static decimal DoorEndX(Door d) => d.x_m + ((d.orientacion is "E" or "W") ? d.largo_m : 0m);
        private static decimal DoorEndY(Door d) => d.y_m + ((d.orientacion is "N" or "S") ? d.largo_m : 0m);
        private static decimal WinEndX(Win w) => w.x_m + ((w.orientacion is "E" or "W") ? w.largo_m : 0m);
        private static decimal WinEndY(Win w) => w.y_m + ((w.orientacion is "N" or "S") ? w.largo_m : 0m);

        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "sin-area";
            var normalized = s.Trim().ToLowerInvariant();

            var formD = normalized.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in formD)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var cleaned = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);

            var sb2 = new System.Text.StringBuilder(cleaned.Length);
            foreach (var ch in cleaned)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-') sb2.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '_' || ch == '/' || ch == '.') sb2.Append('-');
            }
            var slug = sb2.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "sin-area" : slug;
        }

        // === RESUMEN MESONES PARA VISTA ===
        private class MesonSummary
        {
            public string meson_id { get; set; } = "";
            public string area_id { get; set; } = "";
            public string nombre_meson { get; set; } = "";
            public int reactivos_count { get; set; }
            public int equipos_count { get; set; }
        }

        private readonly List<MesonSummary> _mesones = new();

        private async Task LoadMesonesForArea(string areaId)
        {
            _mesones.Clear();
            _mesonesLookup.Clear();

            Pg.UseSheet("mesones");
            var list = new List<MesonSummary>();
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "area_id"), areaId, StringComparison.OrdinalIgnoreCase)) continue;

                var mesonId = Get(r, "meson_id");
                var nombreMeson = Get(r, "nombre_meson");

                _mesonesLookup[mesonId] = nombreMeson;

                list.Add(new MesonSummary
                {
                    meson_id = mesonId,
                    area_id = areaId,
                    nombre_meson = nombreMeson
                });
            }

            if (list.Count == 0)
            {
                _mesones.AddRange(list);
                return;
            }

            try
            {
                Pg.UseSheet("contenedores");
                var cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(Get(r, "area_id"), areaId, StringComparison.OrdinalIgnoreCase)) continue;
                    var mid = NullIfEmpty(Get(r, "meson_id"));
                    if (string.IsNullOrEmpty(mid)) continue;
                    cnt[mid] = cnt.TryGetValue(mid, out var c) ? c + 1 : 1;
                }
                foreach (var m in list)
                    if (cnt.TryGetValue(m.meson_id, out var c)) m.reactivos_count = c;
            }
            catch { }

            try
            {
                Pg.UseSheet("equipos");
                var cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in await Pg.ReadAllAsync())
                {
                    if (!string.Equals(NullIfEmpty(Get(r, "area_id")) ?? "", areaId, StringComparison.OrdinalIgnoreCase)) continue;
                    var mid = NullIfEmpty(Get(r, "meson_id"));
                    if (string.IsNullOrEmpty(mid)) continue;
                    cnt[mid] = cnt.TryGetValue(mid, out var c) ? c + 1 : 1;
                }
                foreach (var m in list)
                    if (cnt.TryGetValue(m.meson_id, out var c)) m.equipos_count = c;
            }
            catch { }

            // Override de nombre si viene desde poligonos_interiores
            foreach (var m in list)
            {
                if (_mesonLabelFromInner.TryGetValue(m.meson_id, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                    m.nombre_meson = lbl;

                if (string.IsNullOrWhiteSpace(m.nombre_meson))
                    m.nombre_meson = "MESÓN";
            }

            _mesones.AddRange(list.OrderBy(m => m.nombre_meson, StringComparer.OrdinalIgnoreCase));
        }

        private void GoToMesonById(string mesonId)
        {
            if (string.IsNullOrWhiteSpace(mesonId)) return;
            Nav.NavigateTo($"/inventario-mesones/item/{Uri.EscapeDataString(mesonId)}");
        }

        private string BlockLabel(BlockItem block)
        {
            if (!string.IsNullOrWhiteSpace(block.etiqueta))
                return block.etiqueta!;

            if (!string.IsNullOrWhiteSpace(block.meson_id)
                && _mesonesLookup.TryGetValue(block.meson_id, out var mesonName)
                && !string.IsNullOrWhiteSpace(mesonName))
                return mesonName;

            if (!string.IsNullOrWhiteSpace(block.instalacion_id)
                && _instalacionesLookup.TryGetValue(block.instalacion_id, out var instName)
                && !string.IsNullOrWhiteSpace(instName))
                return instName;

            return string.Empty;
        }

        private string MesonNameForDisplay(MesonSummary m)
        {
            if (_mesonLabelFromInner.TryGetValue(m.meson_id, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                return lbl;

            if (!string.IsNullOrWhiteSpace(m.nombre_meson))
                return m.nombre_meson;

            return "MESÓN";
        }

        private void OnBlockClick(BlockItem b)
        {
            if (!string.IsNullOrWhiteSpace(b.meson_id))
            {
                GoToMesonById(b.meson_id!);
                return;
            }

            if (!string.IsNullOrWhiteSpace(b.instalacion_id))
            {
                GoToInstalacionById(b.instalacion_id!);
                return;
            }
        }


        private void GoToInstalacion(InstalacionItem item, bool _ = false)
        {
            Nav.NavigateTo(string.Format(CultureInfo.InvariantCulture, InstalacionDetailsRouteTemplate, item.instalacion_id));
        }

        private string MesonNameFromId(string mesonId)
        {
            if (_mesonLabelFromInner.TryGetValue(mesonId, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                return lbl;

            if (_mesonesLookup.TryGetValue(mesonId, out var nm) && !string.IsNullOrWhiteSpace(nm))
                return nm;

            return mesonId;
        }

        private string MesonNameForDisplay(string mesonId)
        {
            if (string.IsNullOrWhiteSpace(mesonId)) return "MESÓN";

            if (_mesonLabelFromInner.TryGetValue(mesonId, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                return lbl;

            if (_mesonesLookup.TryGetValue(mesonId, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;

            return mesonId;
        }
    }
}


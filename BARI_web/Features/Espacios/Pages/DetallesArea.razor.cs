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

        // ====== modelos ======
        private record CanvasLab(string canvas_id, string nombre, decimal ancho_m, decimal alto_m, decimal margen_m);
        private record Poly(string poly_id, string canvas_id, string? area_id,
                            decimal x_m, decimal y_m, decimal ancho_m, decimal alto_m,
                            int z_order, string? etiqueta, string? color_hex);

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
            public decimal alto_m { get; set; }
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
        private readonly List<Door> _doors = new();
        private readonly List<Win> _windows = new();
        private readonly List<EquipoItem> _equipos = new();
        private readonly List<MaterialItem> _materialesVidrio = new();
        private readonly List<MaterialItem> _materialesMontaje = new();
        private readonly List<MaterialItem> _materialesConsumible = new();
        private readonly List<SustanciaItem> _sustancias = new();

        private readonly List<SelectOption> _equiposDisponibles = new();
        private readonly List<SelectOption> _materialesVidrioDisponibles = new();
        private readonly List<SelectOption> _materialesMontajeDisponibles = new();
        private readonly List<SelectOption> _materialesConsumibleDisponibles = new();
        private readonly List<SelectOption> _contenedoresDisponibles = new();

        private string? _selectedEquipoId;
        private string? _selectedMaterialVidrioId;
        private string? _selectedMaterialMontajeId;
        private string? _selectedMaterialConsumibleId;
        private string? _selectedContenedorId;
        private string? _assignMsg;

        // etiqueta override tomada del rectángulo interior del mesón
        private readonly Dictionary<string, string> _mesonLabelFromInner = new(StringComparer.OrdinalIgnoreCase);

        // ===== apariencia / tolerancias =====
        private const decimal OutlineStroke = 0.28m;
        private const decimal TextPad = 0.20m;
        private const decimal Tolerance = 0.004m;
        private static decimal RoundToTolerance(decimal v) => Math.Round(v / Tolerance) * Tolerance;

        // canvas dims
        private decimal Wm => _canvas?.ancho_m ?? 20m;
        private decimal Hm => _canvas?.alto_m ?? 10m;

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

        private sealed record MaterialItem(
            string material_id,
            string nombre,
            string detalle,
            string posicion
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

                // area_id desde el slug (sin GetLookupAsync, leemos "areas" 1 vez)
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

                // interiores / puertas / ventanas / mesones / instalaciones
                await LoadInnerItemsForArea(a);
                await LoadDoorsAndWindowsForArea(a);
                await LoadMesonesForArea(targetAreaId);
                await LoadInstalacionesForArea(a);

                if (_areaInfo is not null)
                {
                    await LoadEquiposAsync(targetAreaId);
                    await LoadMaterialesAsync(targetAreaId);
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
                Dec(c["ancho_m"]), Dec(c["alto_m"]), Dec(c["margen_m"])
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

                polys.Add(new Poly(
                    Get(r, "poly_id"), Get(r, "canvas_id"), areaId,
                    Dec(Get(r, "x_m", "0")), Dec(Get(r, "y_m", "0")),
                    Dec(Get(r, "ancho_m", "0")), Dec(Get(r, "alto_m", "0")),
                    Int(Get(r, "z_order", "0")),
                    NullIfEmpty(Get(r, "etiqueta")),
                    NullIfEmpty(Get(r, "color_hex"))
                ));
            }
            return polys;
        }

        private AreaDraw BuildAreaDrawFromPolys(string areaId, List<Poly> polys)
        {
            var a = new AreaDraw { AreaId = areaId };
            var ordered = polys.OrderBy(p => p.z_order).ToList();
            a.Polys.AddRange(ordered);

            a.MinX = ordered.Min(p => p.x_m);
            a.MinY = ordered.Min(p => p.y_m);
            a.MaxX = ordered.Max(p => p.x_m + p.ancho_m);
            a.MaxY = ordered.Max(p => p.y_m + p.alto_m);

            decimal sx = 0, sy = 0, sa = 0;
            foreach (var p in ordered)
            {
                var area = p.ancho_m * p.alto_m;
                if (area <= 0) continue;
                sx += (p.x_m + p.ancho_m / 2m) * area;
                sy += (p.y_m + p.alto_m / 2m) * area;
                sa += area;
            }
            if (sa > 0) { a.Cx = sx / sa; a.Cy = sy / sa; }
            else { a.Cx = (a.MinX + a.MaxX) / 2m; a.Cy = (a.MinY + a.MaxY) / 2m; }

            // Label: usa primera etiqueta de poly o, si no, fallback al area_id (evita una consulta extra)
            var etiquetaPoly = ordered.Select(p => p.etiqueta).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var nombreArea = _areaInfo?.nombre;
            a.Label = (etiquetaPoly ?? nombreArea ?? areaId).ToUpperInvariant();
            a.Fill = ordered.Select(p => p.color_hex).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "#E6E6E6";

            return a;
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

        private async Task LoadMaterialesAsync(string areaId)
        {
            _materialesVidrio.Clear();
            _materialesMontaje.Clear();
            _materialesConsumible.Clear();

            await using var conn = await DataSource.OpenConnectionAsync();

            const string sqlVidrio = @"
                SELECT material_id,
                       nombre,
                       COALESCE(capacidad_num::text, '') AS detalle,
                       COALESCE(posicion, '') AS posicion
                FROM materiales_vidrio
                WHERE area_id = @area_id
                ORDER BY nombre";
            await using (var cmd = new NpgsqlCommand(sqlVidrio, conn))
            {
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _materialesVidrio.Add(new MaterialItem(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)
                    ));
                }
            }

            const string sqlMontaje = @"
                SELECT material_id,
                       nombre,
                       COALESCE(estado_id, '') AS detalle,
                       COALESCE(posicion, '') AS posicion
                FROM materiales_montaje
                WHERE area_id = @area_id
                ORDER BY nombre";
            await using (var cmd = new NpgsqlCommand(sqlMontaje, conn))
            {
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _materialesMontaje.Add(new MaterialItem(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)
                    ));
                }
            }

            const string sqlConsumible = @"
                SELECT material_id,
                       nombre,
                       COALESCE(cantidad::text, '') AS detalle,
                       COALESCE(posicion, '') AS posicion
                FROM materiales_consumible
                WHERE area_id = @area_id
                ORDER BY nombre";
            await using var cmdCons = new NpgsqlCommand(sqlConsumible, conn);
            cmdCons.Parameters.AddWithValue("area_id", areaId);
            await using var readerCons = await cmdCons.ExecuteReaderAsync();
            while (await readerCons.ReadAsync())
            {
                _materialesConsumible.Add(new MaterialItem(
                    readerCons.GetString(0),
                    readerCons.GetString(1),
                    readerCons.GetString(2),
                    readerCons.GetString(3)
                ));
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

        private async Task LoadDisponiblesAsync(int laboratorioId, string areaId)
        {
            _equiposDisponibles.Clear();
            _materialesVidrioDisponibles.Clear();
            _materialesMontajeDisponibles.Clear();
            _materialesConsumibleDisponibles.Clear();
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
                {
                    _equiposDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
                }
            }

            const string sqlVidrio = @"
                SELECT material_id, nombre
                FROM materiales_vidrio
                WHERE laboratorio_id = @lab
                  AND (area_id IS NULL OR area_id <> @area_id)
                ORDER BY nombre";
            await using (var cmd = new NpgsqlCommand(sqlVidrio, conn))
            {
                cmd.Parameters.AddWithValue("lab", laboratorioId);
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _materialesVidrioDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
                }
            }

            const string sqlMontaje = @"
                SELECT material_id, nombre
                FROM materiales_montaje
                WHERE laboratorio_id = @lab
                  AND (area_id IS NULL OR area_id <> @area_id)
                ORDER BY nombre";
            await using (var cmd = new NpgsqlCommand(sqlMontaje, conn))
            {
                cmd.Parameters.AddWithValue("lab", laboratorioId);
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _materialesMontajeDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
                }
            }

            const string sqlConsumible = @"
                SELECT material_id, nombre
                FROM materiales_consumible
                WHERE laboratorio_id = @lab
                  AND (area_id IS NULL OR area_id <> @area_id)
                ORDER BY nombre";
            await using (var cmd = new NpgsqlCommand(sqlConsumible, conn))
            {
                cmd.Parameters.AddWithValue("lab", laboratorioId);
                cmd.Parameters.AddWithValue("area_id", areaId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _materialesConsumibleDisponibles.Add(new SelectOption(reader.GetString(0), reader.GetString(1)));
                }
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
            {
                _contenedoresDisponibles.Add(new SelectOption(contReader.GetString(0), contReader.GetString(1)));
            }
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

        private async Task AssignMaterialAsync(string table, string? materialId)
        {
            if (_areaInfo is null || string.IsNullOrWhiteSpace(materialId)) return;
            await using var conn = await DataSource.OpenConnectionAsync();
            var sql = $"UPDATE {table} SET area_id = @area_id WHERE material_id = @id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("area_id", _areaInfo.area_id);
            cmd.Parameters.AddWithValue("id", materialId);
            await cmd.ExecuteNonQueryAsync();
            _selectedMaterialVidrioId = null;
            _selectedMaterialMontajeId = null;
            _selectedMaterialConsumibleId = null;
            await LoadMaterialesAsync(_areaInfo.area_id);
            await LoadDisponiblesAsync(_areaInfo.laboratorio_id, _areaInfo.area_id);
            _assignMsg = "Material asignado.";
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

            var areaPolys = a.Polys.ToDictionary(p => p.poly_id, p => p);

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
                var alto_m = Dec(Get(r, "alto_m", "0"));
                var color_hex = NullIfEmpty(Get(r, "color_hex")) ?? "#4B5563";
                var label = NullIfEmpty(Get(r, "etiqueta")) ?? "";

                var meson_id = NullIfEmpty(Get(r, "meson_id"));
                var instalacion_id = NullIfEmpty(Get(r, "instalacion_id"));

                var opTxt = NullIfEmpty(Get(r, "opacidad_0_1"));
                var op = 0.35m;
                if (!string.IsNullOrEmpty(opTxt) && decimal.TryParse(opTxt, NumberStyles.Any, CultureInfo.InvariantCulture, out var opDec))
                    op = opDec;

                var abs_x = parentPoly.x_m + eje_x_rel_m + offset_x_m;
                var abs_y = parentPoly.y_m + eje_y_rel_m + offset_y_m;

                _inners.Add(new InnerItem
                {
                    poly_in_id = Get(r, "poly_in_id"),
                    area_poly_id = area_poly_id,
                    eje_x_rel_m = eje_x_rel_m,
                    eje_y_rel_m = eje_y_rel_m,
                    ancho_m = ancho_m,
                    alto_m = alto_m,
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

            // Puertas
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

            // Ventanas
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

        // ===== outline =====
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
                foreach (var (a1, a2) in spans) { var lo = Math.Min(a1, a2); var hi = Math.Max(a1, a2); xs.Add(lo); xs.Add(hi); }
                var xList = xs.ToList();
                for (int i = 0; i < xList.Count - 1; i++)
                {
                    var s = xList[i]; var e = xList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;
                    int count = 0;
                    foreach (var (a1, a2) in spans)
                    {
                        var lo = Math.Min(a1, a2); var hi = Math.Max(a1, a2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }
                    if ((count % 2) == 1) a.Outline.Add((s, y, e, y));
                }
            }

            foreach (var (x, spans) in V)
            {
                if (spans.Count == 0) continue;
                var ys = new SortedSet<decimal>();
                foreach (var (b1, b2) in spans) { var lo = Math.Min(b1, b2); var hi = Math.Max(b1, b2); ys.Add(lo); ys.Add(hi); }
                var yList = ys.ToList();
                for (int i = 0; i < yList.Count - 1; i++)
                {
                    var s = yList[i]; var e = yList[i + 1];
                    if (e <= s + Tolerance / 10m) continue;
                    int count = 0;
                    foreach (var (b1, b2) in spans)
                    {
                        var lo = Math.Min(b1, b2); var hi = Math.Max(b1, b2);
                        if (s >= lo - Tolerance / 2m && e <= hi + Tolerance / 2m) count++;
                    }
                    if ((count % 2) == 1) a.Outline.Add((x, s, x, e));
                }
            }

            var merged = MergeCollinear(a.Outline);
            a.Outline.Clear();
            a.Outline.AddRange(merged);
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

            // unir horizontales
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

            // unir verticales
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

        // Tamaño de fuente ajustado a alto y ancho del elemento interior
        private static decimal FitInnerText(InnerItem it)
        {
            var pad = 0.10m;
            var w = Math.Max(0.10m, it.ancho_m - 2 * pad);
            var h = Math.Max(0.10m, it.alto_m - 2 * pad);
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

        // === RESUMEN MESONES / INSTALACIONES PARA VISTA ===
        private class MesonSummary
        {
            public string meson_id { get; set; } = "";
            public string area_id { get; set; } = "";
            public string nombre_meson { get; set; } = "";
            public int reactivos_count { get; set; }  // usamos contenedores como proxy de reactivos
            public int equipos_count { get; set; }
        }

        private class InstalacionView
        {
            public string instalacion_id { get; set; } = "";
            public string nombre { get; set; } = "";
            public string? tipo_id { get; set; }
            public string? tipo_nombre { get; set; }
            public string? tipo_descripcion { get; set; } // notas del tipo
            public string? notas { get; set; }            // notas de la instalación
        }

        private readonly List<MesonSummary> _mesones = new();
        private readonly List<InstalacionView> _instalaciones = new();

        private async Task LoadMesonesForArea(string areaId)
        {
            _mesones.Clear();

            // 1) mesones del área
            Pg.UseSheet("mesones");
            var list = new List<MesonSummary>();
            foreach (var r in await Pg.ReadAllAsync())
            {
                if (!string.Equals(Get(r, "area_id"), areaId, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new MesonSummary
                {
                    meson_id = Get(r, "meson_id"),
                    area_id = Get(r, "area_id"),
                    nombre_meson = Get(r, "nombre_meson")
                });
            }

            if (list.Count == 0)
            {
                _mesones.AddRange(list);
                return;
            }

            // 2) contenedores por mesón (proxy de reactivos_count)
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
            catch { /* si no existe columna/tabla, queda en 0 */ }

            // 3) equipos por mesón
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
            catch { /* tabla no existe = ok */ }

            // Override de nombre si viene desde poligonos_interiores
            foreach (var m in list)
            {
                if (_mesonLabelFromInner.TryGetValue(m.meson_id, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                    m.nombre_meson = lbl;
                if (string.IsNullOrWhiteSpace(m.nombre_meson))
                    m.nombre_meson = "MESON";
            }

            _mesones.AddRange(list.OrderBy(m => m.nombre_meson, StringComparer.OrdinalIgnoreCase));
        }

        private string MesonNameForDisplay(MesonSummary m)
        {
            // Ya dejamos el nombre listo en LoadMesonesForArea(), pero mantenemos compatibilidad con el .razor
            if (_mesonLabelFromInner.TryGetValue(m.meson_id, out var lbl) && !string.IsNullOrWhiteSpace(lbl))
                return lbl;

            if (!string.IsNullOrWhiteSpace(m.nombre_meson))
                return m.nombre_meson;

            return "MESON";
        }

        private async Task LoadInstalacionesForArea(AreaDraw a)
        {
            _instalaciones.Clear();

            // 1) capturar instalacion_id desde los poligonos_interiores de esta área
            var areaPolyIds = a.Polys.Select(p => p.poly_id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Pg.UseSheet("poligonos_interiores");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var pid = Get(r, "area_poly_id");
                if (!areaPolyIds.Contains(pid)) continue;
                var insId = NullIfEmpty(Get(r, "instalacion_id"));
                if (!string.IsNullOrEmpty(insId)) needed.Add(insId);
            }
            if (needed.Count == 0) return;

            // 2) leer instalaciones
            var tmp = new Dictionary<string, InstalacionView>(StringComparer.OrdinalIgnoreCase);
            Pg.UseSheet("instalaciones");
            foreach (var r in await Pg.ReadAllAsync())
            {
                var id = Get(r, "instalacion_id");
                if (!needed.Contains(id)) continue;
                tmp[id] = new InstalacionView
                {
                    instalacion_id = id,
                    nombre = Get(r, "nombre"),
                    tipo_id = NullIfEmpty(Get(r, "tipo_id")),
                    notas = NullIfEmpty(Get(r, "notas"))
                };
            }

            // 3) nombres y descripción de tipos
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
                foreach (var it in tmp.Values)
                {
                    if (!string.IsNullOrEmpty(it.tipo_id) && tipoNombre.TryGetValue(it.tipo_id, out var tn))
                    {
                        it.tipo_nombre = tn;
                        it.tipo_descripcion = tipoNotas.TryGetValue(it.tipo_id, out var td) ? td : null;
                    }
                }
            }
            catch { /* si no existe tabla de tipos, quedan nulos */ }

            _instalaciones.AddRange(tmp.Values.OrderBy(v => v.nombre, StringComparer.OrdinalIgnoreCase));
        }
    }
}

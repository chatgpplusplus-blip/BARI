// /Services/PlanRepo.cs
using Dapper;
using Npgsql;

namespace BARI_web.Features.Espacios.Models;

public sealed class PlanRepo
{
    private readonly NpgsqlDataSource _ds;
    public PlanRepo(NpgsqlDataSource ds) => _ds = ds;

    public async Task<PlanDto> GetVigenteAsync(CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var canvasId = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition("SELECT canvas_id FROM canvas_lab ORDER BY canvas_id LIMIT 1", cancellationToken: ct))
            ?? throw new InvalidOperationException("No hay canvas registrado.");

        // Áreas + puntos
        var rows = await conn.QueryAsync<(string area_id, string nombre, decimal x, decimal y, int seq)>(
            new CommandDefinition(@"
SELECT a.area_id, a.nombre_areas as nombre, pp.x_m as x, pp.y_m as y, pp.orden as seq
FROM areas a
JOIN poligonos p ON p.area_id = a.area_id
JOIN poligonos_puntos pp ON pp.poly_id = p.poly_id
WHERE p.canvas_id = @c
ORDER BY a.area_id, p.poly_id, pp.orden;", new { c = canvasId }, cancellationToken: ct));

        var areas = rows
            .GroupBy(r => (r.area_id, r.nombre))
            .Select(g => new AreaDto
            {
                Id = g.Key.area_id,
                Nombre = g.Key.nombre,
                Puntos = g.OrderBy(r => r.seq).Select(r => new Pt(r.x, r.y)).ToList()
            }).ToList();

        // Puertas
        var doorRows = await conn.QueryAsync<DoorRow>(new CommandDefinition(@"
SELECT p.puerta_id, p.x1_m, p.y1_m, p.x2_m, p.y2_m, p.area_a, p.area_b
FROM puertas p
WHERE p.canvas_id=@c
ORDER BY p.puerta_id;", new { c = canvasId }, cancellationToken: ct));

        var puertas = doorRows
            .Select(d => new PuertaDto
            {
                Id = d.puerta_id,
                P1 = new Pt(d.x1_m, d.y1_m),
                P2 = new Pt(d.x2_m, d.y2_m),
                Tipo = "batiente",
                Areas = new[] { d.area_a, d.area_b }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToList()
            }).ToList();

        return new PlanDto { PlanId = canvasId, Areas = areas, Puertas = puertas };
    }

    private sealed record DoorRow(string puerta_id, decimal x1_m, decimal y1_m, decimal x2_m, decimal y2_m, string? area_a, string? area_b);
}

public sealed class PlanDto
{
    public string PlanId { get; set; } = "";
    public List<AreaDto> Areas { get; set; } = new();
    public List<PuertaDto> Puertas { get; set; } = new();
}

public sealed class AreaDto
{
    public string Id { get; set; } = "";
    public string Nombre { get; set; } = "";
    public List<Pt> Puntos { get; set; } = new();
}

public sealed class PuertaDto
{
    public string Id { get; set; } = "";
    public Pt P1 { get; set; } = new(0, 0);
    public Pt P2 { get; set; } = new(0, 0);
    public string Tipo { get; set; } = "batiente";
    public List<string> Areas { get; set; } = new();
}

public readonly record struct Pt(decimal X, decimal Y);

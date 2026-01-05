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

        var planId = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition("SELECT plan_id FROM planos WHERE vigente=TRUE ORDER BY created_at DESC LIMIT 1", cancellationToken: ct))
            ?? throw new InvalidOperationException("No hay plano vigente.");

        // Áreas + puntos
        var rows = await conn.QueryAsync<(string area_id, string nombre, decimal x, decimal y, int seq)>(
            new CommandDefinition(@"
SELECT a.area_id, a.nombre_
as nombre, av.x_m as x, av.y_m as y, av.seq
FROM areas a
JOIN area_vertices av ON av.area_id = a.area_id
WHERE a.plan_id = @p
ORDER BY a.area_id, av.seq;", new { p = planId }, cancellationToken: ct));

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
SELECT p.puerta_id, p.x1_m, p.y1_m, p.x2_m, p.y2_m, p.tipo,
       pa.area_id
FROM puertas p
LEFT JOIN puertas_areas pa ON pa.puerta_id = p.puerta_id
WHERE p.plan_id=@p
ORDER BY p.puerta_id;", new { p = planId }, cancellationToken: ct));

        var puertas = doorRows
            .GroupBy(d => (d.puerta_id, d.x1_m, d.y1_m, d.x2_m, d.y2_m, d.tipo))
            .Select(g => new PuertaDto
            {
                Id = g.Key.puerta_id,
                P1 = new Pt(g.Key.x1_m, g.Key.y1_m),
                P2 = new Pt(g.Key.x2_m, g.Key.y2_m),
                Tipo = g.Key.tipo ?? "batiente",
                Areas = g.Where(x => x.area_id != null).Select(x => x.area_id!).Distinct().ToList()
            }).ToList();

        return new PlanDto { PlanId = planId, Areas = areas, Puertas = puertas };
    }

    private sealed record DoorRow(string puerta_id, decimal x1_m, decimal y1_m, decimal x2_m, decimal y2_m, string? tipo, string? area_id);
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

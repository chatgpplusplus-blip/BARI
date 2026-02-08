using System.Globalization;
using BARI_web.General_Services;
using BARI_web.GeneralPages.HORARIOS;
using Microsoft.AspNetCore.Components;
using Npgsql;
using static BARI_web.GeneralPages.HORARIOS.ScheduleDay; // para ScheduleItemVm si lo pones en Shared

namespace BARI_web.GeneralPages.HORARIOS; // <-- CAMBIA

public partial class Horarios : ComponentBase
{
    [Inject] public NpgsqlDataSource DataSource { get; set; } = default!;
    [Inject] public LaboratorioState LaboratorioState { get; set; } = default!;

    private sealed record PersonalItem(int Id, int UsuarioId, string Nombre);

    private enum TipoPersona { Docente, Becario }

    private readonly string[] _dayHeaders = new[] { "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom" };

    private List<PersonalItem> _docentes = new();
    private List<PersonalItem> _becarios = new();

    private TipoPersona _tipoSeleccionado = TipoPersona.Docente;
    private int? _docenteIdSeleccionado;
    private int? _becarioIdSeleccionado;

    private DateTime _anchorDate = DateTime.Today;
    private DateTime? _selectedDate = DateTime.Today;

    private string? _loadError;
    private string _laboratorioNombre = "Laboratorio";

    private double _horasAcumuladasMensual;
    private string _rangoMesTexto = "";

    private List<ScheduleDay.ScheduleItemVm> _scheduleItemsDelDia = new();

    // Docente: tipos; Becario: dejamos vacío para que pinte por estado
    private string[] _workTypes => _tipoSeleccionado == TipoPersona.Docente
        ? new[] { "laboratorio", "clase", "otro" }
        : Array.Empty<string>();

    private string? _selectedPersonaNombre => _tipoSeleccionado == TipoPersona.Docente
        ? _docentes.FirstOrDefault(d => d.Id == _docenteIdSeleccionado)?.Nombre
        : _becarios.FirstOrDefault(b => b.Id == _becarioIdSeleccionado)?.Nombre;

    protected override async Task OnInitializedAsync()
    {
        await LoadPersonalAsync();
        await LoadLaboratorioNombreAsync();
        await ReloadDayAsync();
    }

    private async Task LoadPersonalAsync()
    {
        _docentes = await LoadPersonasAsync("docentes", "docente_id");
        _becarios = await LoadPersonasAsync("becarios", "becario_id");
    }

    private async Task<List<PersonalItem>> LoadPersonasAsync(string tableName, string idColumn)
    {
        var list = new List<PersonalItem>();
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {idColumn}, usuario_id, nombre FROM {tableName} ORDER BY {idColumn}", conn);
        await using var r = await cmd.ExecuteReaderAsync();

        while (await r.ReadAsync())
            list.Add(new PersonalItem(r.GetInt32(0), r.GetInt32(1), r.GetString(2)));

        return list;
    }

    private async Task LoadLaboratorioNombreAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT nombre FROM laboratorios WHERE laboratorio_id=@id", conn);
        cmd.Parameters.AddWithValue("id", LaboratorioState.LaboratorioId);
        var res = await cmd.ExecuteScalarAsync();
        _laboratorioNombre = res?.ToString() ?? $"Laboratorio #{LaboratorioState.LaboratorioId}";
    }

    private bool HasPersona() => _tipoSeleccionado == TipoPersona.Docente
        ? _docenteIdSeleccionado.HasValue
        : _becarioIdSeleccionado.HasValue;

    private async Task OnTipoPersonaChanged(ChangeEventArgs e)
    {
        _tipoSeleccionado = e.Value?.ToString() == "becario" ? TipoPersona.Becario : TipoPersona.Docente;
        _docenteIdSeleccionado = null;
        _becarioIdSeleccionado = null;
        await ReloadDayAsync();
    }

    private async Task OnDocenteChanged(ChangeEventArgs e)
    {
        _docenteIdSeleccionado = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        await ReloadDayAsync();
    }

    private async Task OnBecarioChanged(ChangeEventArgs e)
    {
        _becarioIdSeleccionado = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        await ReloadDayAsync();
    }

    private void OnDaySelected(DateTime date)
    {
        _selectedDate = date.Date;
        _ = ReloadDayAsync();
    }

    private void GoPrev()
    {
        _anchorDate = _anchorDate.AddMonths(-1);
        _ = ReloadDayAsync();
    }

    private void GoNext()
    {
        _anchorDate = _anchorDate.AddMonths(1);
        _ = ReloadDayAsync();
    }

    private void GoToday()
    {
        _anchorDate = DateTime.Today;
        _selectedDate = DateTime.Today;
        _ = ReloadDayAsync();
    }

    private IEnumerable<DateTime> BuildMonthDays(DateTime reference)
    {
        var first = new DateTime(reference.Year, reference.Month, 1);
        var start = StartOfWeek(first);
        for (int i = 0; i < 42; i++)
            yield return start.AddDays(i);
    }

    private string MesAnchorTexto =>
    CultureInfo.GetCultureInfo("es-ES").TextInfo.ToTitleCase(
        _anchorDate.ToString("MMMM yyyy", new CultureInfo("es-ES"))
    );

    private DateTime StartOfWeek(DateTime d)
    {
        var diff = (7 + (d.DayOfWeek - DayOfWeek.Monday)) % 7;
        return d.AddDays(-diff).Date;
    }

    private (DateTime Start, DateTime End) GetMonthRange(DateTime reference)
    {
        var s = new DateTime(reference.Year, reference.Month, 1);
        var e = s.AddMonths(1).AddDays(-1);
        return (s.Date, e.Date);
    }

    private double GetWorkedHours(DateTime date)
    {
        // versión rápida: calcula basado en items del mes en memoria sería mejor,
        // pero aquí lo dejamos simple: usa lo ya cargado por día
        if (_selectedDate?.Date == date.Date)
        {
            return _scheduleItemsDelDia.Where(i => i.Editable).Sum(i => (i.EndMinute - i.StartMinute) / 60.0);
        }
        return 0;
    }

    private async Task ReloadDayAsync()
    {
        _loadError = null;
        _scheduleItemsDelDia = new();

        var (ms, me) = GetMonthRange(_anchorDate);
        _rangoMesTexto = $"{ms:dd/MM} - {me:dd/MM}";

        if (!_selectedDate.HasValue || !HasPersona())
        {
            _horasAcumuladasMensual = 0;
            StateHasChanged();
            return;
        }

        try
        {
            await using var conn = await DataSource.OpenConnectionAsync();

            // Acumulado mensual
            await LoadMonthlyTotalAsync(conn, ms, me);

            // Día seleccionado
            if (_tipoSeleccionado == TipoPersona.Docente)
            {
                await LoadDocenteDayAsync(conn, _selectedDate.Value);
            }
            else
            {
                await LoadBecarioDayAsync(conn, _selectedDate.Value);
                await LoadBecarioClasesAsync(conn, _selectedDate.Value); // no editable, se pinta igual
            }
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }

        StateHasChanged();
    }

    private async Task LoadMonthlyTotalAsync(NpgsqlConnection conn, DateTime start, DateTime end)
    {
        if (_tipoSeleccionado == TipoPersona.Docente)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT COALESCE(SUM(EXTRACT(EPOCH FROM (hora_fin - hora_inicio))/3600),0)
                FROM horarios_docentes
                WHERE docente_id=@id AND fecha BETWEEN @s AND @e;", conn);

            cmd.Parameters.AddWithValue("id", _docenteIdSeleccionado!.Value);
            cmd.Parameters.AddWithValue("s", start);
            cmd.Parameters.AddWithValue("e", end);

            _horasAcumuladasMensual = Convert.ToDouble(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        else
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT COALESCE(SUM(horas_decimal),0)
                FROM horas_trabajadas_becario
                WHERE becario_id=@id AND fecha BETWEEN @s AND @e;", conn);

            cmd.Parameters.AddWithValue("id", _becarioIdSeleccionado!.Value);
            cmd.Parameters.AddWithValue("s", start);
            cmd.Parameters.AddWithValue("e", end);

            _horasAcumuladasMensual = Convert.ToDouble(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }

    private async Task LoadDocenteDayAsync(NpgsqlConnection conn, DateTime day)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT horario_docente_id, hora_inicio, hora_fin, tipo, COALESCE(observaciones,'')
            FROM horarios_docentes
            WHERE docente_id=@id AND fecha=@f
            ORDER BY hora_inicio;", conn);

        cmd.Parameters.AddWithValue("id", _docenteIdSeleccionado!.Value);
        cmd.Parameters.AddWithValue("f", day.Date);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var id = r.GetInt64(0);
            var hi = r.GetTimeSpan(1);
            var hf = r.GetTimeSpan(2);
            var tipo = r.GetString(3);
            var notes = r.GetString(4);

            var item = new ScheduleDay.ScheduleItemVm
            {
                Id = id,
                Date = day.Date,
                StartMinute = (int)hi.TotalMinutes,
                EndMinute = (int)hf.TotalMinutes,
                Type = tipo,
                Notes = notes,
                Editable = true
            };

            ApplyDocenteStyle(item);
            _scheduleItemsDelDia.Add(item);
        }
    }

    private async Task LoadBecarioDayAsync(NpgsqlConnection conn, DateTime day)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT horas_trabajadas_id, hora_inicio, hora_fin, estado, COALESCE(observaciones,'')
            FROM horas_trabajadas_becario
            WHERE becario_id=@id AND fecha=@f
            ORDER BY hora_inicio;", conn);

        cmd.Parameters.AddWithValue("id", _becarioIdSeleccionado!.Value);
        cmd.Parameters.AddWithValue("f", day.Date);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var id = r.GetInt64(0);
            var hi = r.GetTimeSpan(1);
            var hf = r.GetTimeSpan(2);
            var estado = r.GetString(3);
            var notes = r.GetString(4);

            var item = new ScheduleDay.ScheduleItemVm
            {
                Id = id,
                Date = day.Date,
                StartMinute = (int)hi.TotalMinutes,
                EndMinute = (int)hf.TotalMinutes,
                Status = estado,
                Notes = notes,
                Editable = true
            };

            ApplyBecarioStyle(item);
            _scheduleItemsDelDia.Add(item);
        }
    }

    private async Task LoadBecarioClasesAsync(NpgsqlConnection conn, DateTime day)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT horario_clase_id, hora_inicio, hora_fin, COALESCE(descripcion,'Clase')
            FROM horario_clases_becario
            WHERE becario_id=@id AND fecha=@f
            ORDER BY hora_inicio;", conn);

        cmd.Parameters.AddWithValue("id", _becarioIdSeleccionado!.Value);
        cmd.Parameters.AddWithValue("f", day.Date);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var id = r.GetInt64(0);
            var hi = r.GetTimeSpan(1);
            var hf = r.GetTimeSpan(2);
            var desc = r.GetString(3);

            _scheduleItemsDelDia.Add(new ScheduleDay.ScheduleItemVm
            {
                Id = id,
                Date = day.Date,
                StartMinute = (int)hi.TotalMinutes,
                EndMinute = (int)hf.TotalMinutes,
                Label = desc,
                CssClass = "work-class",
                Editable = false // clases NO editables aquí
            });
        }
    }

    private void ApplyDocenteStyle(ScheduleDay.ScheduleItemVm item)
    {
        item.Label = item.Type;
        item.CssClass = item.Type switch
        {
            "laboratorio" => "work-lab",
            "clase" => "work-class",
            _ => "work-other"
        };
    }

    private void ApplyBecarioStyle(ScheduleDay.ScheduleItemVm item)
    {
        item.Label = "trabajo";
        item.CssClass = item.Status switch
        {
            "validado" => "st-ok",
            "rechazado" => "st-bad",
            _ => "st-pend"
        };
    }

    // === Callbacks del ScheduleDay ===

    private async Task<ScheduleDay.ScheduleItemVm> SaveScheduleItemAsync(ScheduleDay.ScheduleItemVm item)
    {
        // validaciones: no cruzar con clases (becario) + no cruzar con otros trabajos
        if (_tipoSeleccionado == TipoPersona.Becario)
        {
            var clases = _scheduleItemsDelDia.Where(x => !x.Editable).ToList();
            foreach (var c in clases)
            {
                if (Overlap(item.StartMinute, item.EndMinute, c.StartMinute, c.EndMinute))
                    throw new InvalidOperationException("Se cruza con una clase.");
            }
        }

        // cruces con otros editables (trabajos)
        foreach (var other in _scheduleItemsDelDia.Where(x => x.Editable && x != item))
        {
            if (Overlap(item.StartMinute, item.EndMinute, other.StartMinute, other.EndMinute))
                throw new InvalidOperationException("Se cruza con otro bloque de trabajo.");
        }

        await using var conn = await DataSource.OpenConnectionAsync();

        if (_tipoSeleccionado == TipoPersona.Docente)
        {
            // INSERT o UPDATE
            if (item.Id == 0)
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO horarios_docentes (docente_id, fecha, hora_inicio, hora_fin, tipo, observaciones)
                    VALUES (@id, @f, @hi, @hf, @tipo, @obs)
                    RETURNING horario_docente_id;", conn);

                cmd.Parameters.AddWithValue("id", _docenteIdSeleccionado!.Value);
                cmd.Parameters.AddWithValue("f", _selectedDate!.Value.Date);
                cmd.Parameters.AddWithValue("hi", TimeSpan.FromMinutes(item.StartMinute));
                cmd.Parameters.AddWithValue("hf", TimeSpan.FromMinutes(item.EndMinute));
                cmd.Parameters.AddWithValue("tipo", item.Type);
                cmd.Parameters.AddWithValue("obs", (object?)item.Notes ?? "");

                item.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE horarios_docentes
                    SET hora_inicio=@hi, hora_fin=@hf, tipo=@tipo, observaciones=@obs
                    WHERE horario_docente_id=@hid;", conn);

                cmd.Parameters.AddWithValue("hi", TimeSpan.FromMinutes(item.StartMinute));
                cmd.Parameters.AddWithValue("hf", TimeSpan.FromMinutes(item.EndMinute));
                cmd.Parameters.AddWithValue("tipo", item.Type);
                cmd.Parameters.AddWithValue("obs", (object?)item.Notes ?? "");
                cmd.Parameters.AddWithValue("hid", item.Id);

                await cmd.ExecuteNonQueryAsync();
            }

            ApplyDocenteStyle(item);
            return item;
        }
        else
        {
            var horas = Math.Round((item.EndMinute - item.StartMinute) / 60.0, 2, MidpointRounding.AwayFromZero);

            if (item.Id == 0)
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO horas_trabajadas_becario
                        (becario_id, laboratorio_id, fecha, hora_inicio, hora_fin, horas_decimal, estado, observaciones)
                    VALUES
                        (@id, @lab, @f, @hi, @hf, @h, @st, @obs)
                    RETURNING horas_trabajadas_id;", conn);

                cmd.Parameters.AddWithValue("id", _becarioIdSeleccionado!.Value);
                cmd.Parameters.AddWithValue("lab", LaboratorioState.LaboratorioId);
                cmd.Parameters.AddWithValue("f", _selectedDate!.Value.Date);
                cmd.Parameters.AddWithValue("hi", TimeSpan.FromMinutes(item.StartMinute));
                cmd.Parameters.AddWithValue("hf", TimeSpan.FromMinutes(item.EndMinute));
                cmd.Parameters.AddWithValue("h", (decimal)horas);
                cmd.Parameters.AddWithValue("st", item.Status ?? "pendiente");
                cmd.Parameters.AddWithValue("obs", (object?)item.Notes ?? "");

                item.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE horas_trabajadas_becario
                    SET hora_inicio=@hi, hora_fin=@hf, horas_decimal=@h, estado=@st, observaciones=@obs
                    WHERE horas_trabajadas_id=@hid;", conn);

                cmd.Parameters.AddWithValue("hi", TimeSpan.FromMinutes(item.StartMinute));
                cmd.Parameters.AddWithValue("hf", TimeSpan.FromMinutes(item.EndMinute));
                cmd.Parameters.AddWithValue("h", (decimal)horas);
                cmd.Parameters.AddWithValue("st", item.Status ?? "pendiente");
                cmd.Parameters.AddWithValue("obs", (object?)item.Notes ?? "");
                cmd.Parameters.AddWithValue("hid", item.Id);

                await cmd.ExecuteNonQueryAsync();
            }

            ApplyBecarioStyle(item);
            return item;
        }
    }

    private async Task DeleteScheduleItemAsync(ScheduleDay.ScheduleItemVm item)
    {
        await using var conn = await DataSource.OpenConnectionAsync();

        if (_tipoSeleccionado == TipoPersona.Docente)
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM horarios_docentes WHERE horario_docente_id=@id", conn);
            cmd.Parameters.AddWithValue("id", item.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM horas_trabajadas_becario WHERE horas_trabajadas_id=@id", conn);
            cmd.Parameters.AddWithValue("id", item.Id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static bool Overlap(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;
}

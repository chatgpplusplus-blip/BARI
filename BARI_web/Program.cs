using System.Net.Http;
using Microsoft.AspNetCore.Components;
using BARI_web.Features.Seguridad_Quimica.Models;
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection;
using Npgsql;
// Se agrega el namespace de tus nuevos servicios
using BARI_web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURACIÓN DE SERVICIOS EXISTENTES ---

builder.Services.AddHttpClient();
builder.Services.AddScoped<HttpClient>(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// Blazor + Razor Pages (Se mantiene tu RootDirectory personalizado)
builder.Services.AddRazorPages(options =>
{
    options.RootDirectory = "/GeneralPages";
});
builder.Services.AddServerSideBlazor();

// Postgres (Supabase)
var pgConnStr = builder.Configuration["Database:PostgresConnectionString"]!;
builder.Services.AddSingleton(sp => new NpgsqlDataSourceBuilder(pgConnStr).Build());

// CRUD y servicios del sistema base
builder.Services.AddScoped<PgCrud>();
builder.Services.AddScoped<LaboratorioState>();

// Seeds
builder.Services.AddScoped<SeedCatalogs>();
builder.Services.AddHostedService<SeedRunner>();

// --- NUEVOS SERVICIOS: BARI BOT ---
// Los agregamos como Singletons tal como pediste
builder.Services.AddHttpClient<OllamaChatClient>();

builder.Services.AddSingleton<BariIntentRouter>();
builder.Services.AddSingleton<OllamaSqlPlanner>();
builder.Services.AddSingleton<OllamaAnswerWriter>();
builder.Services.AddSingleton<PostgresReadOnlyExecutor>();
builder.Services.AddSingleton<BariBotOrchestrator>();


var app = builder.Build();

// --- CONFIGURACIÓN DEL PIPELINE (MIDDLEWARE) ---

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Se mantiene tu endpoint de prueba de red
app.MapGet("/admin/net-test", async (IHttpClientFactory httpFactory) =>
{
    var http = httpFactory.CreateClient();
    var url = "https://mhchem.github.io/hpstatements/clp/hpstatements-es-latest.json";
    try
    {
        using var resp = await http.GetAsync(url);
        var ok = resp.IsSuccessStatusCode;
        var status = (int)resp.StatusCode;
        var content = await resp.Content.ReadAsStringAsync();
        var preview = ok ? content.AsSpan(0, Math.Min(200, content.Length)).ToString() : content;
        return Results.Ok(new { ok, status, preview });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.GetType().FullName, message = ex.Message });
    }
});

app.Run();
using System.Net.Http;
using Microsoft.AspNetCore.Components;
using BARI_web.Features.Seguridad_Quimica.Models;
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection;
using Npgsql;
using BARI_web.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------
// SERVICIOS BASE
// ------------------------------

builder.Services.AddHttpClient();

builder.Services.AddScoped<HttpClient>(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// Blazor + Razor Pages (RootDirectory personalizado)
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

// ------------------------------
// BARI BOT (DeepSeek + acceso total a BD en lectura)
// ------------------------------

// Bind opciones desde appsettings / user-secrets / env
builder.Services.Configure<DeepSeekOptions>(builder.Configuration.GetSection("DeepSeek"));

// HttpClient tipado para DeepSeek
builder.Services.AddHttpClient<DeepSeekChatClient>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<DeepSeekOptions>>().Value;

    http.BaseAddress = new Uri(opt.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);

    if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opt.ApiKey);

    http.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

// Catálogo de esquema (introspección de TODA la BD, cacheado)
builder.Services.AddSingleton<SchemaCatalog>();

// Firewall SQL (solo SELECT/WITH, fuerza LIMIT y bloquea DDL/DML)
builder.Services.AddSingleton<SafeSqlValidator>(sp => new SafeSqlValidator
{
    MaxRows = 100
});

// Servicios del bot (sin Ollama) - usando planner/executor genéricos
builder.Services.AddSingleton<BariIntentRouter>();
builder.Services.AddSingleton<DeepSeekSqlPlanner>();
builder.Services.AddSingleton<PostgresReadOnlyExecutor>();
builder.Services.AddSingleton<DeepSeekAnswerWriter>();
builder.Services.AddSingleton<BariBotOrchestrator>();

// ------------------------------
// APP
// ------------------------------

var app = builder.Build();

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

// Endpoint de prueba de red (se mantiene)
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

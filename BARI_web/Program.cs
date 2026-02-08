using System.Net.Http;
using Microsoft.AspNetCore.Components;
using BARI_web.Features.Seguridad_Quimica.Models;
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection;
using Npgsql;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using BARI_web.Features.Services;
using Microsoft.AspNetCore.SignalR;
using BARI_web.Features.Descarga;

var builder = WebApplication.CreateBuilder(args);

// Render: escucha en el puerto asignado por la plataforma
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

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

// ✅ Forwarded headers (importante en Render / reverse proxy)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Importante en hosting tipo Render (proxy/reverse-proxy):
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ✅ Blazor Server: tolerancia a “background” en móviles
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        // Mantén la conexión "viva" y da más margen antes de declararla muerta
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        // En móvil, al ir a WhatsApp, la pestaña puede congelarse:
        // si este timeout es corto, el server corta rápido y pierdes el circuito.
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);

        // Handshake un poco más tolerante (red móvil)
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);

        // Payloads grandes por SignalR (si aplicara)
        options.MaximumReceiveMessageSize = 20 * 1024 * 1024; // 20 MB
    })
    .AddCircuitOptions(options =>
    {
        // 🔥 CLAVE: retener el circuito desconectado para que al volver NO haga reload
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);

        // Cuántos circuitos desconectados se guardan (sube si tienes pocos usuarios simultáneos)
        options.DisconnectedCircuitMaxRetained = 200;

        // Opcional: evita que se caiga por renders pendientes si la red es mala
        options.MaxBufferedUnacknowledgedRenderBatches = 20;
    });

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
builder.Services.AddScoped<CascadeDeleteService>();

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

// 🔥 Importante: antes de HttpsRedirection para que detecte bien el esquema detrás del proxy
app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Map Blazor
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Tus endpoints
app.MapInventoryDownloads();
app.MapHorasDownloads();

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

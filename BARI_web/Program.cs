// Program.cs

using System.Net.Http;
using Microsoft.AspNetCore.Components;
using BARI_web.Features.Seguridad_Quimica.Models; // SeedRunner, PlanRepo DTOs
using BARI_web.General_Services;
using BARI_web.General_Services.DataBaseConnection; // PgCrud
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// HttpClient: factory + scoped client with BaseAddress for same-site API calls
builder.Services.AddHttpClient();
builder.Services.AddScoped<HttpClient>(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// Blazor + Razor Pages (custom root)
builder.Services.AddRazorPages(options =>
{
    options.RootDirectory = "/GeneralPages"; // _Host.cshtml, _Layout.cshtml, Error.cshtml
});
builder.Services.AddServerSideBlazor();


// Postgres (Supabase)
var pgConnStr = builder.Configuration["Database:PostgresConnectionString"]!;
builder.Services.AddSingleton(sp => new NpgsqlDataSourceBuilder(pgConnStr).Build());

// CRUD and services
builder.Services.AddScoped<PgCrud>();
builder.Services.AddScoped<LaboratorioState>();

// Seeds
builder.Services.AddScoped<SeedCatalogs>();
builder.Services.AddHostedService<SeedRunner>();

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

// Simple net test endpoint using IHttpClientFactory
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

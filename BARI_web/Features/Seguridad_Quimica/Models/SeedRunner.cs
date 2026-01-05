using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BARI_web.Features.Seguridad_Quimica.Models;

public sealed class SeedRunner : IHostedService
{
    private readonly ILogger<SeedRunner> _log;
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;

    public SeedRunner(ILogger<SeedRunner> log, IServiceProvider sp, IConfiguration cfg)
    {
        _log = log; _sp = sp; _cfg = cfg;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var run = _cfg.GetValue<bool>("Seeding:RunOnStartup");
        var force = _cfg.GetValue<bool>("Seeding:ForceAll");

        if (!run)
        {
            _log.LogInformation("Seeding deshabilitado (Seeding:RunOnStartup=false).");
            return;
        }

        using var scope = _sp.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<SeedCatalogs>();
        await seeder.RunAsync(force, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

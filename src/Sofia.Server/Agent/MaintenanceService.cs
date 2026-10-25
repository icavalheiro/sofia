using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Providers;

namespace Sofia.Server.Agent;

/// <summary>Recovers state left by a previous process and keeps the model catalog fresh.</summary>
public sealed class MaintenanceService(SofiaDb db, ProviderService providers, ILogger<MaintenanceService> log) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Recover();
        providers.SeedFromEnvironment();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await providers.RefreshAsync(discover: db.GetConfig().Discovery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Provider refresh failed");
            }
            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs never survive a restart: settle their sessions and any card still waiting for an answer.</summary>
    private void Recover()
    {
        var recovered = 0;
        foreach (var session in db.Sessions.FindAll().ToList())
        {
            var stale = session.Status != SessionStatus.Idle && session.Status != SessionStatus.Error;
            if (stale)
            {
                session.Status = SessionStatus.Idle;
                db.Sessions.Update(session);
                recovered++;
            }
            foreach (var m in db.SessionMessages(session.Id))
            {
                var changed = SessionRun.SettleCard(m);
                if (stale)
                {
                    foreach (var call in m.ToolCalls ?? [])
                    {
                        if (call.Status != "running") continue;
                        call.Status = "error";
                        call.Error = "Interrupted: the server restarted.";
                        call.Duration = "—";
                        call.Completed = true;
                        changed = true;
                    }
                }
                if (changed) db.Messages.Update(m);
            }
        }
        if (recovered > 0) log.LogInformation("Recovered {Count} sessions interrupted by the last shutdown", recovered);
    }
}

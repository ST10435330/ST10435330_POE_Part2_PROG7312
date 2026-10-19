namespace SmartX;

public sealed class TelemetryWorker(RealtimeEngine engine, ILogger<TelemetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var nextHealth = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    engine.Process();
                    if (DateTimeOffset.UtcNow >= nextHealth)
                    {
                        engine.RefreshAlerts(); nextHealth = DateTimeOffset.UtcNow.AddSeconds(1);
                    }
                }
                catch (Exception error) { logger.LogError(error, "Telemetry worker failed; it will retry on the next tick."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

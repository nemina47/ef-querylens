namespace EFQueryLens.Daemon;

internal static class IdleShutdownTimer
{
    internal static IDisposable Start(WebApplication app, DaemonRuntime runtime, int idleMinutes)
    {
        var timer = new System.Timers.Timer(TimeSpan.FromSeconds(60).TotalMilliseconds)
        {
            AutoReset = true,
        };

        timer.Elapsed += (_, _) =>
        {
            if (DateTime.UtcNow - runtime.LastActivity > TimeSpan.FromMinutes(idleMinutes))
            {
                Console.Error.WriteLine($"[QL-Engine] idle-shutdown after {idleMinutes}m of inactivity");
                _ = app.StopAsync();
            }
        };

        timer.Start();
        return timer;
    }
}

using M365Graph;
using PyCompat;
using WinBackground;

namespace MeetingPreps;

/// <summary>Watch mode: a run every every_minutes within working hours, so preps are written when they're due.
///
/// A long-running loop rather than scheduled triggers: when the laptop wakes, the loop simply carries on and catches up
/// anything that came due while it slept. One watcher per settings file (a second exits at once, code 3); the
/// settings are re-read each run; it never opens a browser; a failure is notified only after three in a row (a
/// sign-in problem at once); it logs `alive` hourly. **A Microsoft 365 outage isn't a failure:** runs carry on from
/// the cached calendar, preps say what they couldn't read, and the engine notifies once per outage. A run fails only
/// when there's no calendar and no recent copy of it either, and that's the "couldn't run" notice.</summary>
public static class Watch
{
    public const int AlreadyRunning = 3;

    public static bool Due(Config config, DateTime now)
    {
        if (config.WatchWeekdays && PyTime.Weekday(now) >= 5)
            return false;
        var hhmm = PyTime.Strftime(now, "%H:%M");
        return string.CompareOrdinal(config.WatchHours.Start, hhmm) <= 0 && string.CompareOrdinal(hhmm, config.WatchHours.End) < 0;
    }

    public static string LockName(Config config, PrepsHost host) => $"{host.LockPrefix}meeting-preps-watch-{Locks.Key(config.Path)}";

    /// <summary>Runs until the process ends (or keepGoing says stop, for tests).</summary>
    public static int Run(Config config, PrepsHost host, Func<bool>? keepGoing = null)
    {
        var log = Logger.Create(config.LogFile, host.LogEcho, clock: host.Clock.Now);
        using var held = Locks.SingleInstance(LockName(config, host));   // held until the process ends
        if (held is null)
        {
            log("another watcher is already running for these settings; not starting a second");
            return AlreadyRunning;
        }
        log($"watching: a run every {config.WatchEveryMinutes} min, {config.WatchHours.Start}-{config.WatchHours.End}"
            + (config.WatchWeekdays ? " Mon-Fri" : ""));
        double Seconds() => host.Clock.UtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var nextRun = 0.0;
        var beat = Seconds();
        string? failing = null;
        var streak = 0;   // failures in a row
        while (keepGoing?.Invoke() ?? true)
        {
            if (Seconds() >= nextRun)
            {
                nextRun = Seconds() + config.WatchEveryMinutes * 60;
                if (Due(config, host.Clock.Now()))
                {
                    try
                    {
                        config = Config.Load(config.Path, host.Env);
                        new Engine(config, host.Graph(config, false), log, host).Run();
                        failing = null;
                        streak = 0;
                    }
                    catch (SignInNeededException error)
                    {
                        log($"sign-in needed: {error.Message}");
                        if (failing != "sign-in")
                            host.Notify("meeting-preps needs a sign-in", [Head(error.Message, 300)]);
                        failing = "sign-in";
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        var name = PyMore.PyName(error);
                        log($"run failed: {name}: {error.Message}");
                        streak++;
                        if (streak == 3)
                            host.Notify("meeting-preps couldn't run", [Head($"{name}: {error.Message}", 300), $"Log: {config.LogFile}"]);
                        failing = name;
                    }
                }
            }
            if (Seconds() - beat >= 3600)
            {
                beat = Seconds();
                log("alive");
            }
            host.Sleep(TimeSpan.FromSeconds(30));
        }
        return 0;
    }

    private static string Head(string text, int count) => PyCompat.PyStr.Head(text, count);
}

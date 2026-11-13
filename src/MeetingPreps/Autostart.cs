namespace MeetingPreps;

/// <summary>The watcher at logon, through Windows Task Scheduler (WinBackground). It runs meeting-preps-background.exe
/// directly, so there's no window, and stopping the task stops the watcher itself. The same task name as the Python
/// version, so turning either on replaces the other.</summary>
public static class Autostart
{
    public const string TaskName = "meeting-preps watch";
    public const string Description = "Writes meeting preps when they're due (meeting-preps watch).";
    public static readonly IReadOnlyDictionary<string, string> Results = new Dictionary<string, string> { ["3"] = "another watcher was already running" };

    public static int Command(string action, Config config, PrepsHost host)
    {
        void Print(string text) => host.Out.Write(text.Replace("\n", Environment.NewLine) + Environment.NewLine);
        if (action == "on")
        {
            var line = host.Task.Enable(host.BackgroundExe, ["--config", config.Path, "watch"], Path.GetDirectoryName(config.Path));
            Print($"Autostart is on: '{TaskName}' runs at every logon, and has started now.\n  {line}");
        }
        else if (action == "off")
            Print(host.Task.Disable() ? "Autostart is off." : "Autostart was already off.");
        else
        {
            var info = host.Task.Status();
            if (info is null)
                Print("Autostart is off. `meeting-preps autostart on` turns it on.");
            else
            {
                Print($"Autostart is on ('{TaskName}').");
                foreach (var name in new[] { "state", "last_run", "last_result", "command" })
                    Print($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
            }
        }
        host.Out.Flush();
        return 0;
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using WinBackground;

namespace MeetingPreps;

/// <summary>The command line, as the Python's cli.py: the same commands, options and exit codes (0; 2 for settings,
/// sign-in, no such meeting and usage errors; 1 when a run had failures or something unexpected went wrong; 3 from
/// watch when another watcher is running). argparse's wording is approximated.</summary>
public static class Cli
{
    public const string Usage = """
        meeting-preps: a prep for each meeting worth one, written before it from everything since last time.

            meeting-preps config                     create the settings file (if missing) and say where it is
            meeting-preps sign-in                    sign in to Microsoft (once; opens a browser)
            meeting-preps plan [DATE] [DAYS]         which meetings get a prep, and when (default: the next 5 days)
            meeting-preps run [--dry-run]            write whatever is due, then refresh preps for meetings in the next hour
            meeting-preps write DATE "<name>"        one prep now: the meeting that day with a person, or a series by subject
            meeting-preps update <transcript>...     add "Update HH:MM" blocks to upcoming preps these transcripts touch
            meeting-preps hook                       the same, for teams-transcripts' after_run (its JSON on stdin)
            meeting-preps context "<name>"           what a prep would see for this person: email, Teams, diary
            meeting-preps prompt DATE "<name>"       the prompt a prep would be written from; nothing written, Claude not run
            meeting-preps watch                      a run every few minutes in working hours; what autostart runs
            meeting-preps autostart on               the watcher at every logon, and now (off, status)

        options: --config FILE (default %APPDATA%\meeting-preps\config.toml), --version
        """;

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0-dev";

    private sealed class UsageException(string message) : Exception(message);

    private static void Print(TextWriter writer, string text)
    {
        writer.Write(text.Replace("\n", Environment.NewLine) + Environment.NewLine);
        writer.Flush();
    }

    public static int Run(string[] argv, PrepsHost host)
    {
        try
        {
            return Dispatch(argv, host);
        }
        catch (UsageException error)
        {
            Print(host.Err, $"usage: meeting-preps [-h] [--version] [--config CONFIG] COMMAND ...\nmeeting-preps: error: {error.Message}");
            return 2;
        }
    }

    private static int Dispatch(string[] argv, PrepsHost host)
    {
        string? configPath = null;
        var rest = new List<string>();
        var i = 0;
        for (; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a is "-h" or "--help")
            {
                Print(host.Out, Usage.ReplaceLineEndings("\n"));
                return 0;
            }
            if (a == "--version")
            {
                Print(host.Out, Version);
                return 0;
            }
            if (a == "--config")
            {
                if (++i >= argv.Length)
                    throw new UsageException("argument --config: expected one argument");
                configPath = argv[i];
                continue;
            }
            if (a.StartsWith("--config=", StringComparison.Ordinal))
            {
                configPath = a["--config=".Length..];
                continue;
            }
            break;
        }
        if (i >= argv.Length)
            throw new UsageException("the following arguments are required: command");
        var command = argv[i];
        rest.AddRange(argv.Skip(i + 1));
        string[] commands = ["config", "sign-in", "plan", "run", "write", "update", "hook", "context", "prompt", "watch", "autostart"];
        if (!commands.Contains(command))
            throw new UsageException($"argument command: invalid choice: {Py.ReprString(command)}");
        var flags = rest.Where(r => r.StartsWith("--", StringComparison.Ordinal)).ToList();
        var positional = rest.Where(r => !r.StartsWith("--", StringComparison.Ordinal)).ToList();
        string[] allowed = command switch { "plan" => ["--json"], "run" => ["--dry-run"], _ => [] };
        foreach (var flag in flags)
            if (!allowed.Contains(flag))
                throw new UsageException($"unrecognized arguments: {flag}");
        void Count(int min, int max, string names)
        {
            if (positional.Count < min)
                throw new UsageException($"the following arguments are required: {names}");
            if (positional.Count > max)
                throw new UsageException($"unrecognized arguments: {string.Join(' ', positional.Skip(max))}");
        }
        switch (command)
        {
            case "config" or "sign-in" or "run" or "hook" or "watch": Count(0, 0, ""); break;
            case "plan": Count(0, 2, ""); break;
            case "write" or "prompt": Count(2, 2, "date, who"); break;
            case "update": Count(1, int.MaxValue, "transcripts"); break;
            case "context": Count(1, 1, "name"); break;
            case "autostart":
                Count(1, 1, "action");
                if (positional[0] is not ("on" or "off" or "status"))
                    throw new UsageException($"argument action: invalid choice: {Py.ReprString(positional[0])} (choose from on, off, status)");
                break;
        }
        var planDays = 5;
        if (command == "plan" && positional.Count == 2 && !int.TryParse(positional[1], out planDays))
            throw new UsageException($"argument days: invalid int value: {Py.ReprString(positional[1])}");

        if (command == "config")
        {
            Print(host.Out, configPath is { Length: > 0 } ? configPath : Config.CreateDefault(host.Env));
            return 0;
        }
        Config config;
        try
        {
            config = Config.Load(configPath, host.Env);
        }
        catch (Exception error) when (error is FileNotFoundException or SettingsException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        if (command == "autostart")
            return Autostart.Command(positional[0], config, host);
        if (command == "watch")
            return Watch.Run(config, host);

        var log = Logger.Create(config.LogFile, host.LogEcho, stampEcho: false, clock: host.Clock.Now);
        try
        {
            if (command == "sign-in")
            {
                GraphReader.ClientFor(config).TokenAsync(config.Scopes, interactive: true).GetAwaiter().GetResult();
                Print(host.Out, "Signed in.");
                return 0;
            }
            var graph = host.Graph(config, null);
            if (command == "plan")
            {
                var start = positional.Count > 0 ? DateOnly.FromDateTime(PyTime.FromIso(positional[0])) : DateOnly.FromDateTime(host.Clock.Now());
                var plan = Planner.PlanAll(config, Planner.Events(graph, config, start, planDays), start, planDays);
                Print(host.Out, flags.Contains("--json") ? Planner.Json(plan) : Planner.Show(plan, start, planDays));
                return 0;
            }
            if (command == "context")
            {
                var name = positional[0];
                Print(host.Out, $"## Email (14 days)\n{Context.Mail(graph, config, name, host.Clock, log: log)}\n\n"
                                + $"## Teams (14 days)\n{Context.Teams(graph, name, host.Clock, log: log)}\n\n"
                                + $"## Diary (7 days)\n{Context.Diary(graph, config, DateOnly.FromDateTime(host.Clock.Now()))}");
                return 0;
            }
            var engine = new Engine(config, graph, log, host);
            switch (command)
            {
                case "run":
                    return engine.Run(flags.Contains("--dry-run"));
                case "write":
                    engine.WriteOne(DateOnly.FromDateTime(PyTime.FromIso(positional[0])), positional[1]);
                    return 0;
                case "prompt":
                    Print(host.Out, engine.PromptFor(DateOnly.FromDateTime(PyTime.FromIso(positional[0])), positional[1]));
                    return 0;
                case "update":
                    return engine.Update(positional);
                case "hook":
                    return engine.Update(HookPaths(host.Stdin().ReadToEnd()));
            }
        }
        catch (SignInNeededException error)
        {
            log($"sign-in needed: {error.Message}");
            return 2;
        }
        catch (Exception error) when (error is NoSuchMeetingException or MissingFieldException)
        {
            log(error.Message);
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Print(host.Err, $"{error.GetType().Name}: {error.Message}");
            return 1;
        }
        return 0;
    }

    /// <summary>teams-transcripts' after_run report (results with "path"), doc-filer's ({"written": [...]}), or paths,
    /// one a line.</summary>
    public static List<string> HookPaths(string raw)
    {
        JsonNode? data;
        try
        {
            data = PyStr.Strip(raw).Length > 0 ? PyJson.Parse(raw) : new JsonArray();
        }
        catch (JsonException)
        {
            return PyStr.SplitLines(raw).Select(PyStr.Strip).Where(l => l.Length > 0).ToList();
        }
        var items = data is JsonObject o ? o["written"] as JsonArray ?? [] : data as JsonArray ?? [];
        var paths = new List<string>();
        foreach (var item in items)
        {
            string? path = item switch
            {
                JsonObject d => Py.Truthy(d["path"]) ? Py.Str(d["path"]) : d["file"] is JsonValue f && Py.Truthy(f) ? Py.Str(f) : null,
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
                _ => null,
            };
            if (path is { Length: > 0 } && Suffix(path) is ".md" or ".txt")
                paths.Add(path);
        }
        return paths;
    }

    /// <summary>pathlib's suffix, lower-cased: the last ".x" of the name, if the name isn't just that.</summary>
    internal static string Suffix(string path)
    {
        var name = Planner.BaseName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : "";
    }
}

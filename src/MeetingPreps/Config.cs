using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace MeetingPreps;

/// <summary>Settings that can't be used: Python's ValueError, shown as it is and exit code 2.</summary>
public sealed class SettingsException(string message) : Exception(message);

/// <summary>A kind of meeting: how its prep is described and written.</summary>
public sealed class Kind
{
    public required string Name { get; init; }
    public required string Describe { get; init; }
    public string Rules { get; init; } = "";
    public string? Companion { get; init; }
    public string CompanionRules { get; init; } = "";
    public bool EveryTranscript { get; init; }
    public string UpdateRules { get; init; } = "";
    public List<List<string>> ContextCommands { get; init; } = [];

    /// <summary>A document written in a block of your own (a weekly update), not a meeting prep: its prompt is its
    /// rules plus the week's diary, email and (if the series names a chat) Teams, without the prep's scaffolding.</summary>
    public bool Document { get; init; }
}

/// <summary>A series' usual weekly slot.</summary>
public sealed record Slot(string Day, string Start, string End);

/// <summary>A [person."Name"] or [series."Subject"] table, as the planner reads it (Python kept the raw dict).</summary>
public sealed class Entry
{
    public required string Kind { get; init; }
    public required string File { get; init; }
    public string Slug { get; init; } = "";
    public string? AlwaysDayBeforeAt { get; init; }
    public string? DuePreviousWorkingDayAt { get; init; }
    public string? DueSameDayAt { get; init; }
    public List<string> Members { get; init; } = [];
    public string? Chat { get; init; }
    public List<Slot> Weekly { get; init; } = [];
    public string? Absorbs { get; init; }

    /// <summary>A series that is one of your own blocks (yours, no one else invited): only such an event matches, it's
    /// never put back from a weekly slot (no block, as on leave, means nothing is written), and it stays worth writing
    /// for the rest of its day after the block has started.</summary>
    public bool OwnBlock { get; init; }

    /// <summary>A document written around your own leave rather than for a meeting: "return" on the first working day
    /// back, "before" on the last working day before it, for leave of at least [leave] min_working_days. Null for any
    /// other series.</summary>
    public string? Leave { get; init; }

    /// <summary>Where this series' files go, overriding output (absolute; given relative to the workspace).</summary>
    public string? Folder { get; init; }
}

/// <summary>The settings file: who you are, which meetings get a prep, how each kind is written, and where it goes.
/// Every option is in the README. Read as the Python version read it, so paths come out as the same text and the
/// state, log, calendar copy and lock names are shared.</summary>
public sealed class Config
{
    public const string EnvConfig = "MEETING_PREPS_CONFIG";
    public const string DefaultLeavePattern = @"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b";

    /// <summary>Every top-level setting this file accepts (tables included): the ones a base file can supply.
    /// time_zone is the shared name for timezone.</summary>
    public static readonly string[] KnownKeys =
    [
        "me", "email", "workspace", "output", "history", "kind", "person", "series", "aliases", "org_domain", "login_hint",
        "tenant", "msal_cache", "model", "owner", "house_rules", "house_rules_file", "after_write", "git", "review",
        "first_thing_before", "day_before_at", "on_the_day_at", "refresh_within_minutes", "leave_pattern", "mail_skip_folders",
        "noise_senders", "noise_rules", "timezone", "time_zone", "scopes", "watch", "schedule", "log_file", "state_file", "calendar_cache",
        "calendar_cache_days", "hook_include", "hook_exclude", "hook_queue", "originals", "leave", "plan_days",
    ];

    public const string Template = """
        # meeting-preps settings. See the README.
        # base = "base-config.toml"   # optional: settings shared with other tools
        me = "Your Name"
        email = "you@example.com"
        workspace = "~/Documents/Meetings"
        output = "preps"

        [kind.one-to-one]
        describe = "1:1 with {name}"
        rules = "Read your notes and summaries of past meetings with {name}, and pick up open actions."

        # [person."Their Name"]
        # kind = "one-to-one"
        # slug = "their-name"
        # file = "{date}-their-name-prep.md"

        """;

    public required string Path { get; init; }

    /// <summary>1.5.0: which files `hook` takes as transcripts (globs relative to workspace, `*` across folders): any
    /// .md or .txt by default; hook_exclude leaves some out (performance reviews, say).</summary>
    public IReadOnlyList<string> HookInclude { get; init; } = ["*"];
    public IReadOnlyList<string> HookExclude { get; init; } = [];

    /// <summary>1.5.0: true, and `hook` puts the update on the claude-headless queue, at the front (a meeting to beat),
    /// and starts the queue runner, rather than running it there and then (holding up whatever ran the hook).</summary>
    public bool HookQueue { get; init; }

    /// <summary>Whether `hook` takes this file: inside the workspace, matching hook_include and not hook_exclude.</summary>
    public bool HookWants(string file)
    {
        if (RelativeTo(PyPath.Resolve(file), Workspace) is not { } rel)
            return false;
        static bool Match(string path, string glob) => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, path, ignoreCase: true);
        return HookInclude.Any(g => Match(rel, g)) && !HookExclude.Any(g => Match(rel, g));
    }

    public required string Me { get; init; }
    public required string Email { get; init; }
    public required string Workspace { get; init; }
    public required string Output { get; init; }
    public required string History { get; init; }
    public Dictionary<string, Kind> Kinds { get; init; } = [];
    /// <summary>In file order, as Python's dict.</summary>
    public List<KeyValuePair<string, Entry>> PeopleInOrder { get; init; } = [];
    public List<KeyValuePair<string, Entry>> SeriesInOrder { get; init; } = [];
    public Dictionary<string, string> Aliases { get; init; } = [];
    public List<KeyValuePair<string, string>> AliasesInOrder { get; init; } = [];
    public string? OrgDomain { get; init; }
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public string Model { get; init; } = "opus";
    public string Owner { get; init; } = "you";
    public string HouseRules { get; init; } = "";
    public List<string> AfterWrite { get; init; } = [];

    /// <summary>[git], or null: nothing committed. Commits each file written into the repository.</summary>
    public RepoCommit.GitSettings? Git { get; init; }

    /// <summary>originals, or null: a folder (outside the workspace) where each file written is also kept as written, at
    /// the same relative path, overwritten by each later write. For a workspace without git, so what the person changed
    /// in a draft can still be seen (git would have the draft's commit).</summary>
    public string? Originals { get; init; }

    /// <summary>[review], or null: nothing queued. An item for each file written into the repository.</summary>
    public ReviewQueue.ReviewSettings? Review { get; init; }
    public string FirstThingBefore { get; init; } = "10:00";
    public string DayBeforeAt { get; init; } = "16:00";
    public string OnTheDayAt { get; init; } = "07:00";
    public long RefreshWithinMinutes { get; init; } = 60;
    /// <summary>How many days `plan` shows when it isn't given a number (plan_days; 5). Only the view: what gets a prep is
    /// decided by the timings above, to the end of the next working day.</summary>
    public int PlanDays { get; init; } = 5;
    public string LeavePattern { get; init; } = DefaultLeavePattern;

    /// <summary>[leave]: the rules for the leave documents (a series with `leave`).</summary>
    public LeaveSettings LeaveSettings { get; init; } = new();
    public List<string> MailSkipFolders { get; init; } = ["junk email", "drafts", "outbox"];
    public List<string> NoiseSenders { get; init; } = [];
    public string? NoiseRules { get; init; }
    public string TimeZone { get; init; } = "GMT Standard Time";
    public List<string> Scopes { get; init; } = ["Calendars.Read", "Mail.Read", "Chat.Read"];
    /// <summary>When the watcher runs: [schedule] (background-tools-lib's shared scheduling settings, 1.9.0), a
    /// watcher's every_minutes being how often it runs. Until 1.9.0 these were [watch] every_minutes, hours and
    /// weekdays, still read (the old form; the watcher logs it once at start).</summary>
    public WinBackground.ScheduleSettings Schedule { get; init; } = DefaultSchedule;

    /// <summary>True when the schedule came from the old [watch] table.</summary>
    public bool ScheduleFromOldKeys { get; init; }

    public static readonly WinBackground.ScheduleSettings DefaultSchedule = new()
    {
        EveryMinutes = 5,
        Hours = (new TimeSpan(7, 0, 0), new TimeSpan(18, 0, 0)),
        Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
    };

    public long WatchEveryMinutes => Schedule.EveryMinutes ?? 5;
    public (string Start, string End) WatchHours => Schedule.Hours is var (from, to) ? (Clock(from), Clock(to)) : ("00:00", "24:00");
    public bool WatchWeekdays => Schedule.Days is { Count: 5 } d && !d.Contains(DayOfWeek.Saturday) && !d.Contains(DayOfWeek.Sunday);

    /// <summary>"a run every 5 min, 07:00-18:00 Mon-Fri", for the log and messages.</summary>
    public string ScheduleText()
    {
        var days = Schedule.Days is null || Schedule.Days.Count == 7 ? ""
            : WatchWeekdays ? " Mon-Fri"
            : " " + string.Join(",", Schedule.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]));
        return $"a run every {WatchEveryMinutes} min, {WatchHours.Start}-{WatchHours.End}{days}";
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 24 ? "24:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    private static readonly string[] OldScheduleKeys = ["every_minutes", "hours", "weekdays"];

    /// <summary>The watcher's schedule: [schedule] (1.9.0), or the old [watch] every_minutes, hours and weekdays (still
    /// read, so a settings file from before keeps working). Both at once is refused.</summary>
    private static (WinBackground.ScheduleSettings Schedule, bool OldKeys) ReadSchedule(TomlTable raw, string full)
    {
        var watch = raw.TryGetValue("watch", out var w) && w is TomlTable watchTable ? watchTable : [];
        var old = OldScheduleKeys.Where(watch.ContainsKey).ToList();
        if (raw.TryGetValue("schedule", out var table))
        {
            if (old.Count > 0)
                throw new SettingsException($"{full} sets {string.Join(", ", old)} in [watch] and has a [schedule]: keep them in "
                                            + "[schedule] only ([watch] is the old form).");
            if (table is not TomlTable schedule)
                throw new SettingsException($"schedule in {full} should be a [schedule] table.");
            WinBackground.ScheduleSettings parsed;
            try
            {
                parsed = WinBackground.ScheduleSettings.Parse(schedule, DefaultSchedule, WinBackground.TaskKind.Watcher, $"[schedule] in {full}");
            }
            catch (FormatException error)
            {
                throw new SettingsException(error.Message);
            }
            if (parsed.EveryMinutes is not > 0)
                throw new SettingsException($"[schedule] in {full}: every_minutes (how often the watcher runs) should be at least 1.");
            return (parsed, false);
        }
        if (old.Count == 0)
            return (DefaultSchedule, false);
        var hours = watch.TryGetValue("hours", out var h) ? Str(h) : "07:00-18:00";
        if (!Regex.IsMatch(hours, @"\A\d\d:\d\d-\d\d:\d\d\z", RegexOptions.CultureInvariant))
            throw new SettingsException($"[watch] hours in {full} should look like \"07:00-18:00\".");
        return (DefaultSchedule with
        {
            EveryMinutes = watch.TryGetValue("every_minutes", out var every) ? (int)Int(every) : 5,
            Hours = WinBackground.ScheduleSettings.ParseHours(hours, $"[watch] in {full}"),
            Days = !watch.ContainsKey("weekdays") || Truthy(watch, "weekdays") ? DefaultSchedule.Days : null,
        }, true);
    }
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }

    /// <summary>The copy of the last good calendar read, planned from when Graph is down (default: beside the state
    /// file, as &lt;state&gt;.calendar.json).</summary>
    public required string CalendarCache { get; init; }

    /// <summary>How old that copy may be, in days, and still be planned from.</summary>
    public double CalendarCacheDays { get; init; } = 3;

    public Entry? Person(string name) => PeopleInOrder.FirstOrDefault(p => p.Key == name).Value;
    public Entry? Series(string subject) => SeriesInOrder.FirstOrDefault(p => p.Key == subject).Value;

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\meeting-preps\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "meeting-preps", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\meeting-preps: the default state and log.</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "meeting-preps"));
    }

    /// <summary>Path(path).resolve().relative_to(workspace).as_posix(), or str(path) if it isn't inside.</summary>
    public string Relative(string path)
    {
        var resolved = PyPath.Resolve(path);
        return RelativeTo(resolved, Workspace) is { } inside ? inside : path;
    }

    /// <summary>pathlib's relative_to on Windows: whole parts, ignoring case; null if not inside.</summary>
    internal static string? RelativeTo(string path, string folder)
    {
        var a = Parts(path);
        var b = Parts(folder);
        if (b.Count > a.Count)
            return null;
        for (var i = 0; i < b.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return null;
        return a.Count == b.Count ? "." : string.Join('/', a.Skip(b.Count));
    }

    internal static bool IsRelativeTo(string path, string folder) => RelativeTo(path, folder) is not null;

    private static List<string> Parts(string path)
    {
        var p = PyPath.Pure(path);
        var parts = new List<string>();
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var bits = p[2..].Split('\\');
            parts.Add(@"\\" + string.Join('\\', bits.Take(2)) + "\\");
            parts.AddRange(bits.Skip(2).Where(x => x.Length > 0));
            return parts;
        }
        if (p.Length >= 2 && p[1] == ':')
        {
            parts.Add(p.StartsWith(p[..2] + "\\", StringComparison.Ordinal) ? p[..2] + "\\" : p[..2]);
            p = p[2..];
        }
        parts.AddRange(p.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return parts;
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
        return PyPath.Resolve(PyPath.IsAbsolute(expanded) ? expanded : System.IO.Path.Combine(folder, expanded));
    }

    private static readonly Regex Time = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.CultureInvariant);

    /// <summary>TIME.match: Python's $ also matches before a final newline, and \d is any Unicode digit.</summary>
    internal static bool IsTime(string text) => Time.IsMatch(text);

    /// <summary>load(path): the given path, else MEETING_PREPS_CONFIG, else the default. Throws FileNotFoundException
    /// if there's no file, and SettingsException for settings that can't be used.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `meeting-preps config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('\ufeff'))
                throw new SettingsException("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            // With the base file it names, if any: its [paths] are variables for the paths here (so env is replaced),
            // and its settings this file knows and doesn't set are added.
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (FormatException error)
        {
            throw new SettingsException(error.Message);   // the base: missing, unreadable, or naming a base itself
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or SettingsException))
        {
            throw new SettingsException($"{full}: {error.Message}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        foreach (var key in new[] { "me", "email" })
            if (!Truthy(raw, key))
                throw new SettingsException($"{full} needs {key}.");
        var workspace = PathSetting(raw.TryGetValue("workspace", out var ws) ? Str(ws) : ".", folder, env);
        if (!Directory.Exists(workspace))
            throw new SettingsException($"workspace in {full} isn't a folder: {workspace}");
        var output = PathSetting(raw.TryGetValue("output", out var o) ? Str(o) : "preps", workspace, env);
        foreach (var key in new[] { "first_thing_before", "day_before_at", "on_the_day_at" })
            if (raw.TryGetValue(key, out var value) && !IsTime(Str(value)))
                throw new SettingsException($"{key} in {full} should be a time such as \"07:00\".");

        var kinds = new Dictionary<string, Kind>();
        if (raw.TryGetValue("kind", out var kindTables) && kindTables is TomlTable kindTable)
        {
            foreach (var (name, value) in kindTable)
            {
                var section = value as TomlTable ?? [];
                var where = $"[kind.{name}] in {full}";
                if (!Truthy(section, "describe"))
                    throw new SettingsException($"{where} needs describe.");
                kinds[name] = new Kind
                {
                    Name = name,
                    Describe = Str(section["describe"]),
                    Rules = TextSetting(section, "rules", folder, where, env),
                    Companion = section.TryGetValue("companion", out var c) && c is not null ? Str(c) : null,
                    CompanionRules = TextSetting(section, "companion_rules", folder, where, env),
                    EveryTranscript = Truthy(section, "every_transcript"),
                    UpdateRules = TextSetting(section, "update_rules", folder, where, env),
                    ContextCommands = section.TryGetValue("context_commands", out var cc) && cc is TomlArray commands
                        ? commands.Select(command => command is TomlArray parts ? parts.Select(Str).ToList() : [Str(command)]).ToList()
                        : [],
                    Document = Truthy(section, "document"),
                };
            }
        }

        var people = Entries(raw, "person", "person", kinds, full, workspace, env);
        var series = Entries(raw, "series", "series", kinds, full, workspace, env);
        var (schedule, oldKeys) = ReadSchedule(raw, full);
        var leavePattern = raw.TryGetValue("leave_pattern", out var lp) ? Str(lp) : DefaultLeavePattern;
        if (PyRegex.Problem(leavePattern) is { } problem)
            throw new SettingsException($"leave_pattern in {full} isn't a valid regular expression ({problem}).");

        RepoCommit.GitSettings? git = null;
        ReviewQueue.ReviewSettings? review = null;
        try
        {
            if (raw.TryGetValue("git", out var gitSection))
                git = RepoCommit.GitSettings.Read(gitSection, full, value => PathSetting(value, folder, env));
            if (raw.TryGetValue("review", out var reviewSection))
                review = ReviewQueue.ReviewSettings.Read(reviewSection, full, value => PathSetting(value, folder, env));
        }
        catch (FormatException error)
        {
            throw new SettingsException(error.Message);
        }
        if (git is not null && !Directory.Exists(git.Repo))
            throw new SettingsException($"repo under [git] in {full} isn't a folder: {git.Repo}");

        var aliases = raw.TryGetValue("aliases", out var al) && al is TomlTable aliasTable
            ? aliasTable.Select(pair => new KeyValuePair<string, string>(pair.Key, Str(pair.Value))).ToList()
            : [];
        string? Optional(string key) => raw.TryGetValue(key, out var value) && Truthy(raw, key) ? Str(value) : null;
        var local = LocalFolder(env);
        var stateFile = Optional("state_file") is { } state ? PathSetting(state, folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "state.json"));
        var houseRules = Truthy(raw, "house_rules_file")
            ? TextSetting(raw, "house_rules", folder, full, env)
            : HouseRulesFile(raw, folder, full, env);
        List<string> Globs(string key, List<string> fallback) =>
            !raw.TryGetValue(key, out var value) ? fallback
                : value is TomlArray a && a.All(x => x is string) ? a.Cast<string>().ToList()
                : throw new SettingsException($"{key} in {full} should be a list of globs");
        string OutsideWorkspace(string path) => RelativeTo(PyPath.Resolve(path), workspace) is null ? path
            : throw new SettingsException($"originals in {full} should be a folder outside the workspace ({workspace}), not {path}");
        return new Config
        {
            Path = full,
            HookInclude = Globs("hook_include", ["*"]),
            HookExclude = Globs("hook_exclude", []),
            HookQueue = raw.TryGetValue("hook_queue", out var hq) && (hq is bool hb ? hb : throw new SettingsException($"hook_queue in {full} should be true or false")),
            Me = Str(raw["me"]),
            Email = Str(raw["email"]).ToLowerInvariant(),
            Workspace = workspace,
            Output = output,
            History = Optional("history") is { } history ? PathSetting(history, workspace, env) : output,
            Kinds = kinds,
            PeopleInOrder = people,
            SeriesInOrder = series,
            AliasesInOrder = aliases,
            Aliases = aliases.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Last().Value),
            OrgDomain = Optional("org_domain")?.ToLowerInvariant() is { Length: > 0 } domain ? domain : null,
            LoginHint = raw.TryGetValue("login_hint", out var hint) && hint is not null ? Str(hint) : null,
            Tenant = raw.TryGetValue("tenant", out var tenant) ? Str(tenant) : "organizations",
            MsalCache = Optional("msal_cache") is { } cache ? PathSetting(cache, folder, env) : null,
            Model = raw.TryGetValue("model", out var model) ? Str(model) : "opus",
            Owner = raw.TryGetValue("owner", out var owner) ? Str(owner) : "you",
            HouseRules = houseRules,
            AfterWrite = raw.TryGetValue("after_write", out var aw) && aw is TomlArray after ? after.Select(Str).ToList() : [],
            Git = git,
            Review = review,
            Originals = Optional("originals") is { } originals ? OutsideWorkspace(PathSetting(originals, folder, env)) : null,
            FirstThingBefore = raw.TryGetValue("first_thing_before", out var ftb) ? Str(ftb) : "10:00",
            DayBeforeAt = raw.TryGetValue("day_before_at", out var dba) ? Str(dba) : "16:00",
            OnTheDayAt = raw.TryGetValue("on_the_day_at", out var otd) ? Str(otd) : "07:00",
            RefreshWithinMinutes = raw.TryGetValue("refresh_within_minutes", out var rw) ? Int(rw) : 60,
            PlanDays = raw.TryGetValue("plan_days", out var pd) ? PlanDaysSetting(pd, full) : 5,
            LeavePattern = leavePattern,
            LeaveSettings = ReadLeave(raw, full),
            MailSkipFolders = raw.TryGetValue("mail_skip_folders", out var msf) && msf is TomlArray skip
                ? skip.Select(f => Str(f).ToLowerInvariant()).ToList()
                : ["junk email", "drafts", "outbox"],
            NoiseRules = Optional("noise_rules") is { } rules ? PathSetting(rules, folder, env) : null,
            NoiseSenders = raw.TryGetValue("noise_senders", out var ns) && ns is TomlArray senders ? senders.Select(Str).ToList() : [],
            // timezone, or the shared time_zone (a base file's name for it); timezone wins.
            TimeZone = raw.TryGetValue("timezone", out var tz) || raw.TryGetValue("time_zone", out tz) ? Str(tz) : "GMT Standard Time",
            Scopes = raw.TryGetValue("scopes", out var sc) && sc is TomlArray scopes ? scopes.Select(Str).ToList() : ["Calendars.Read", "Mail.Read", "Chat.Read"],
            Schedule = schedule,
            ScheduleFromOldKeys = oldKeys,
            LogFile = Optional("log_file") is { } log ? PathSetting(log, folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "meeting-preps.log")),
            StateFile = stateFile,
            CalendarCache = Optional("calendar_cache") is { } calendarCache ? PathSetting(calendarCache, folder, env) : WithSuffix(stateFile, ".calendar.json"),
            CalendarCacheDays = raw.TryGetValue("calendar_cache_days", out var ccd) ? Float(ccd) : 3,
        };
    }

    /// <summary>[leave]: background-tools-lib's shared leave settings (subjects, categories, min_hours, half_day,
    /// bank_holiday_subjects, bank_holiday_categories; a base file's [leave] can supply them), plus this tool's own
    /// min_working_days and window_days, each optional.</summary>
    private static LeaveSettings ReadLeave(TomlTable raw, string full)
    {
        if (!raw.TryGetValue("leave", out var value))
            return new LeaveSettings();
        if (value is not TomlTable table)
            throw new SettingsException($"leave in {full} should be a [leave] table.");
        CalendarDays.LeaveSettings shared;
        try
        {
            shared = CalendarDays.LeaveSettings.Parse(table, full, ["min_working_days", "window_days"]);
        }
        catch (FormatException error)
        {
            throw new SettingsException(error.Message);
        }
        double Number(string key, double fallback, double min, double max)
        {
            if (!table.TryGetValue(key, out var v))
                return fallback;
            var n = v switch { long l => l, double d => d, _ => double.NaN };
            return n >= min && n <= max ? n : throw new SettingsException($"[leave] {key} in {full} should be a number from {min} to {max}.");
        }
        return new LeaveSettings
        {
            MinWorkingDays = Number("min_working_days", 5, 0.5, 366),
            Rules = shared.Rules,
            HalfDay = shared.HalfDay,
            BankHolidaySubjects = shared.BankHolidaySubjects,
            BankHolidayCategories = shared.BankHolidayCategories,
            WindowDays = (int)Number("window_days", 42, 7, 366),
        };
    }

    /// <summary>pathlib's with_suffix: the name's last ".x" (if it has one) replaced.</summary>
    internal static string WithSuffix(string path, string suffix)
    {
        var name = System.IO.Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
        return PyPath.Pure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", stem + suffix));
    }

    private static List<KeyValuePair<string, Entry>> Entries(TomlTable raw, string key, string label, Dictionary<string, Kind> kinds, string full,
        string workspace, Func<string, string?> env)
    {
        var list = new List<KeyValuePair<string, Entry>>();
        if (!raw.TryGetValue(key, out var value) || value is not TomlTable table)
            return list;
        foreach (var (name, item) in table)
        {
            var entry = item as TomlTable ?? [];
            var where = $"[{label}.\"{name}\"] in {full}";
            var kind = entry.TryGetValue("kind", out var k) && k is not null ? Str(k) : null;
            if (kind is null || !kinds.ContainsKey(kind))
            {
                var repr = kind is null ? "None" : Py.ReprString(kind);
                throw new SettingsException($"{where} has kind {repr}, which has no [kind.{kind ?? "None"}].");
            }
            var file = entry.TryGetValue("file", out var f) ? Str(f) : "";
            var leave = entry.TryGetValue("leave", out var lv) ? Str(lv) : null;
            if (leave is not null && label != "series")
                throw new SettingsException($"{where}: leave is for a series (a document written around your leave), not a person.");
            if (leave is not (null or "return" or "before"))
                throw new SettingsException($"{where}: leave should be \"return\" (the first working day back) or \"before\" (the last working day before), not {Py.ReprString(leave)}.");
            string[] dated = leave is null ? ["{date}", "{week}", "{monday}"] : ["{date}", "{week}", "{monday}", "{first_day}", "{last_day}", "{return_day}"];
            if (!dated.Any(d => file.Contains(d, StringComparison.Ordinal)))
                throw new SettingsException($"{where} needs a file name with {{date}} in it.");
            if (leave is not null && (Truthy(entry, "weekly") || Truthy(entry, "own_block") || Truthy(entry, "absorbs") || Truthy(entry, "always_day_before_at")
                                      || Truthy(entry, "due_previous_working_day_at")))
                throw new SettingsException($"{where}: a leave series is written on a day worked out from your leave, so weekly, own_block, absorbs and the timings other than due_same_day_at don't apply.");
            foreach (var timing in new[] { "always_day_before_at", "due_previous_working_day_at", "due_same_day_at" })
                if (entry.TryGetValue(timing, out var t) && !IsTime(Str(t)))
                    throw new SettingsException($"{where}: {timing} should be a time such as \"14:00\".");
            string? Timing(string name) => entry.TryGetValue(name, out var t) && Truthy(entry, name) ? Str(t) : null;
            var ownBlock = Truthy(entry, "own_block");
            if (ownBlock && Truthy(entry, "weekly"))
                throw new SettingsException($"{where}: own_block and weekly don't go together (no block means nothing is written).");
            list.Add(new(name, new Entry
            {
                Kind = kind,
                File = file,
                Slug = entry.TryGetValue("slug", out var slug) ? Str(slug) : "",
                AlwaysDayBeforeAt = Timing("always_day_before_at"),
                DuePreviousWorkingDayAt = Timing("due_previous_working_day_at"),
                DueSameDayAt = Timing("due_same_day_at"),
                Members = entry.TryGetValue("members", out var m) && m is TomlArray members ? members.Select(Str).ToList() : [],
                Chat = entry.TryGetValue("chat", out var chat) && Truthy(entry, "chat") ? Str(chat) : null,
                Weekly = entry.TryGetValue("weekly", out var wk) && wk is TomlTableArray slots
                    ? slots.Select(s => new Slot(Str(s["day"]), Str(s["start"]), Str(s["end"]))).ToList()
                    : entry.TryGetValue("weekly", out var wk2) && wk2 is TomlArray inline
                        ? inline.OfType<TomlTable>().Select(s => new Slot(Str(s["day"]), Str(s["start"]), Str(s["end"]))).ToList()
                        : [],
                Absorbs = entry.TryGetValue("absorbs", out var ab) && Truthy(entry, "absorbs") ? Str(ab) : null,
                OwnBlock = ownBlock,
                Folder = Truthy(entry, "folder") ? PathSetting(Str(entry["folder"]), workspace, env) : null,
                Leave = leave,
            }));
        }
        return list;
    }

    /// <summary>_text: a rules text given inline (key) or as a file (key_file, relative to the settings file).</summary>
    private static string TextSetting(TomlTable section, string key, string folder, string where, Func<string, string?> env)
    {
        if (Truthy(section, key + "_file"))
        {
            var path = PathSetting(Str(section[key + "_file"]), folder, env);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new SettingsException($"{where}: {key}_file names a file that isn't there: {path}");
            return PyStr.Strip(PyStr.ReadText(path));
        }
        return PyStr.Strip(section.TryGetValue(key, out var value) ? Str(value) : "");
    }

    /// <summary>_house_rules: house_rules is a file name (relative to the settings file), or empty.</summary>
    private static string HouseRulesFile(TomlTable raw, string folder, string full, Func<string, string?> env)
    {
        if (!Truthy(raw, "house_rules"))
            return "";
        var rules = PathSetting(Str(raw["house_rules"]), folder, env);
        if (!File.Exists(rules) && !Directory.Exists(rules))
            throw new SettingsException($"house_rules in {full} names a file that isn't there: {rules}");
        return PyStr.Strip(PyStr.ReadText(rules));
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    internal static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTableArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    /// <summary>Python's str() of a setting.</summary>
    internal static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        double d => Py.Repr(d),
        TomlDateTime t => t.ToString(),
        _ => value.ToString() ?? "",
    };

    /// <summary>Python's float() of a setting: a number, a bool, or text such as "2.5" (a ValueError otherwise).</summary>
    private static double Float(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s => PyMore.ParseFloat(s),
        _ => throw new InvalidOperationException(
            $"float() argument must be a string or a real number, not '{(value is TomlArray ? "list" : value is TomlTable ? "dict" : value?.GetType().Name)}'"),
    };

    /// <summary>Python's int() of a setting.</summary>
    private static long Int(object? value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => PyStr.Int(s),
        _ => throw new SettingsException($"{value} isn't a whole number."),
    };

    /// <summary>plan_days: a whole number of days, 1 to 366.</summary>
    private static int PlanDaysSetting(object? value, string file) =>
        value is long n and >= 1 and <= 366 ? (int)n
            : throw new SettingsException($"plan_days in {file} should be a whole number of days from 1 to 366, not {value}.");
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;

using System.Globalization;
using System.Text.RegularExpressions;
using MeetingPreps.Interim;
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
}

/// <summary>The settings file: who you are, which meetings get a prep, how each kind is written, and where it goes.
/// The same file the Python version read (see its config.py docstring for every option), read the same way, so
/// paths come out as the same text and the state, log and lock names are shared.</summary>
public sealed class Config
{
    public const string EnvConfig = "MEETING_PREPS_CONFIG";
    public const string DefaultLeavePattern = @"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b";

    public const string Template = """
        # meeting-preps settings. See the README.
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
    public string FirstThingBefore { get; init; } = "10:00";
    public string DayBeforeAt { get; init; } = "16:00";
    public string OnTheDayAt { get; init; } = "07:00";
    public long RefreshWithinMinutes { get; init; } = 60;
    public string LeavePattern { get; init; } = DefaultLeavePattern;
    public List<string> MailSkipFolders { get; init; } = ["junk email", "drafts", "outbox"];
    public List<string> NoiseSenders { get; init; } = [];
    public string? NoiseRules { get; init; }
    public string TimeZone { get; init; } = "GMT Standard Time";
    public List<string> Scopes { get; init; } = ["Calendars.Read", "Mail.Read", "Chat.Read"];
    public long WatchEveryMinutes { get; init; } = 5;
    public (string Start, string End) WatchHours { get; init; } = ("07:00", "18:00");
    public bool WatchWeekdays { get; init; } = true;
    public required string LogFile { get; init; }
    public required string StateFile { get; init; }

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
        var resolved = RealPath.Resolve(path);
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
        return RealPath.Resolve(PyPath.IsAbsolute(expanded) ? expanded : System.IO.Path.Combine(folder, expanded));
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
        var full = RealPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `meeting-preps config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('\ufeff'))
                throw new SettingsException("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
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
                };
            }
        }

        var people = Entries(raw, "person", "person", kinds, full);
        var series = Entries(raw, "series", "series", kinds, full);
        var watch = raw.TryGetValue("watch", out var w) && w is TomlTable watchTable ? watchTable : [];
        var hours = watch.TryGetValue("hours", out var h) ? Str(h) : "07:00-18:00";
        if (!Regex.IsMatch(hours, @"\A\d\d:\d\d-\d\d:\d\d\z", RegexOptions.CultureInvariant))
            throw new SettingsException($"[watch] hours in {full} should look like \"07:00-18:00\".");
        var leavePattern = raw.TryGetValue("leave_pattern", out var lp) ? Str(lp) : DefaultLeavePattern;
        if (PyRegex.Problem(leavePattern) is { } problem)
            throw new SettingsException($"leave_pattern in {full} isn't a valid regular expression ({problem}).");

        var aliases = raw.TryGetValue("aliases", out var al) && al is TomlTable aliasTable
            ? aliasTable.Select(pair => new KeyValuePair<string, string>(pair.Key, Str(pair.Value))).ToList()
            : [];
        string? Optional(string key) => raw.TryGetValue(key, out var value) && Truthy(raw, key) ? Str(value) : null;
        var local = LocalFolder(env);
        var houseRules = Truthy(raw, "house_rules_file")
            ? TextSetting(raw, "house_rules", folder, full, env)
            : HouseRulesFile(raw, folder, full, env);
        return new Config
        {
            Path = full,
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
            FirstThingBefore = raw.TryGetValue("first_thing_before", out var ftb) ? Str(ftb) : "10:00",
            DayBeforeAt = raw.TryGetValue("day_before_at", out var dba) ? Str(dba) : "16:00",
            OnTheDayAt = raw.TryGetValue("on_the_day_at", out var otd) ? Str(otd) : "07:00",
            RefreshWithinMinutes = raw.TryGetValue("refresh_within_minutes", out var rw) ? Int(rw) : 60,
            LeavePattern = leavePattern,
            MailSkipFolders = raw.TryGetValue("mail_skip_folders", out var msf) && msf is TomlArray skip
                ? skip.Select(f => Str(f).ToLowerInvariant()).ToList()
                : ["junk email", "drafts", "outbox"],
            NoiseRules = Optional("noise_rules") is { } rules ? PathSetting(rules, folder, env) : null,
            NoiseSenders = raw.TryGetValue("noise_senders", out var ns) && ns is TomlArray senders ? senders.Select(Str).ToList() : [],
            TimeZone = raw.TryGetValue("timezone", out var tz) ? Str(tz) : "GMT Standard Time",
            Scopes = raw.TryGetValue("scopes", out var sc) && sc is TomlArray scopes ? scopes.Select(Str).ToList() : ["Calendars.Read", "Mail.Read", "Chat.Read"],
            WatchEveryMinutes = watch.TryGetValue("every_minutes", out var every) ? Int(every) : 5,
            WatchHours = (hours.Split('-')[0], hours.Split('-')[1]),
            WatchWeekdays = !watch.ContainsKey("weekdays") || Truthy(watch, "weekdays"),
            LogFile = Optional("log_file") is { } log ? PathSetting(log, folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "meeting-preps.log")),
            StateFile = Optional("state_file") is { } state ? PathSetting(state, folder, env) : PyPath.Pure(System.IO.Path.Combine(local, "state.json")),
        };
    }

    private static List<KeyValuePair<string, Entry>> Entries(TomlTable raw, string key, string label, Dictionary<string, Kind> kinds, string full)
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
            if (!file.Contains("{date}", StringComparison.Ordinal) && !file.Contains("{week}", StringComparison.Ordinal))
                throw new SettingsException($"{where} needs a file name with {{date}} in it.");
            foreach (var timing in new[] { "always_day_before_at", "due_previous_working_day_at", "due_same_day_at" })
                if (entry.TryGetValue(timing, out var t) && !IsTime(Str(t)))
                    throw new SettingsException($"{where}: {timing} should be a time such as \"14:00\".");
            string? Timing(string name) => entry.TryGetValue(name, out var t) && Truthy(entry, name) ? Str(t) : null;
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

    /// <summary>Python's int() of a setting.</summary>
    private static long Int(object? value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => PyText.Int(s),
        _ => throw new SettingsException($"{value} isn't a whole number."),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;

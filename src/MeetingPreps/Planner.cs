using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingPreps;

/// <summary>Which meetings get a prep, and when it's written.
///
/// Every meeting in the calendar is sorted into prep (a 1:1 with someone in [person], or a [series] meeting by its
/// exact subject), unsure (a 1:1 with someone the settings don't name) and skip (group meetings, your own blocks,
/// all-day markers, free, cancelled or declined). **A 1:1 is a meeting with exactly one other person**; meeting
/// rooms don't count. Timing, series prepped when declined, leave days and absorbed meetings are as the Python's
/// planner.py describes them.
///
/// Times are wall-clock times as Graph gives them in the settings' time zone, compared as they are, with no zone
/// arithmetic (as the Python's naive datetimes).</summary>
public static class Planner
{
    public static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The calendar for days from start, in the settings' time zone.</summary>
    public static List<JsonObject> Events(IGraph graph, Config config, DateOnly start, int days)
    {
        var query = PyMore.UrlEncode([
            ("startDateTime", $"{PyTime.Iso(start)}T00:00:00"),
            ("endDateTime", $"{PyTime.Iso(start.AddDays(days))}T00:00:00"),
            ("$top", "200"), ("$orderby", "start/dateTime"),
            ("$select", "subject,start,end,attendees,organizer,isCancelled,showAs,responseStatus,isAllDay,webLink"),
        ]);
        var headers = new Dictionary<string, string> { ["Prefer"] = $"outlook.timezone=\"{config.TimeZone}\"" };
        return graph.Pages($"/me/calendarView?{query}", headers).Select(e => (JsonObject)e!).ToList();
    }

    public static DateOnly PreviousWorkingDay(DateOnly day)
    {
        day = day.AddDays(-1);
        while (PyTime.Weekday(day) >= 5)
            day = day.AddDays(-1);
        return day;
    }

    public static DateOnly NextWorkingDay(DateOnly day)
    {
        day = day.AddDays(1);
        while (PyTime.Weekday(day) >= 5)
            day = day.AddDays(1);
        return day;
    }

    public static DateTime At(DateOnly day, string hhmm) => day.ToDateTime(TimeOnly.FromTimeSpan(PyTime.TimeFromIso(hhmm)));

    /// <summary>The Monday a weekly document is filed under: that day if it's a Monday, else the coming Monday.</summary>
    public static DateOnly WeekOf(DateOnly day) => day.AddDays((7 - PyTime.Weekday(day)) % 7);

    /// <summary>The Monday of the day's own week: that day if it's a Monday, else the Monday before.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-PyTime.Weekday(day));

    /// <summary>The most recent prep of the same kind for an EARLIER meeting, searched recursively (archives count).
    /// Not one for this meeting: during a side-by-side trial another tool's prep for the same meeting exists, and
    /// modelling on it copies it (10 Nov 2026). A series with its own folder is searched there, and only there (not in
    /// its subfolders, which may hold other people's documents named the same way).</summary>
    public static string? LastPrep(Config config, string pattern, DateOnly before, string? folder = null)
    {
        var glob = pattern.Replace("{date}", "*").Replace("{week}", "*").Replace("{monday}", "*").Replace("{slug}", "*");
        if (folder is not null)
        {
            if (!Directory.Exists(folder))
                return null;
            var name = glob.Split('/', '\\')[^1];
            var mine = Directory.EnumerateFiles(folder)
                .Where(p => Glob(Path.GetFileName(p), name) && DatePrefix(Path.GetFileName(p))
                            && string.CompareOrdinal(PyStr.Head(Path.GetFileName(p), 10), PyTime.Iso(before)) < 0)
                .OrderBy(p => Path.GetFileName(p), PyStr.Order)
                .ToList();
            return mine.Count > 0 ? config.Relative(mine[^1]) : null;
        }
        if (!Directory.Exists(config.History))
            return null;
        var cutoff = PyTime.Iso(before);
        // rglob("a/b*") is glob("**/a/b*"): the pattern's parts against the last parts of the path, one by one. A file
        // name in a subfolder ("peers/{date}-x.md") has more than one part (found by the 0.1.1 fuzz, 13 Nov 2026).
        var parts = glob.Split('/', '\\').Where(p => p.Length > 0 && p != ".").ToArray();
        var root = config.History.TrimEnd('\\', '/').Length + 1;
        var found = Walk(config.History)
            .Where(p =>
            {
                var path = p[root..].Split('\\', '/');
                return path.Length >= parts.Length && parts.Select((g, i) => Glob(path[path.Length - parts.Length + i], g)).All(m => m);
            })
            .Where(p => DatePrefix(Path.GetFileName(p)) && string.CompareOrdinal(PyStr.Head(Path.GetFileName(p), 10), cutoff) < 0)
            .OrderBy(p => Path.GetFileName(p), PyStr.Order)
            .ToList();
        return found.Count > 0 ? config.Relative(found[^1]) : null;
    }

    private static bool DatePrefix(string name) =>
        PyRegex.Get(@"\d{4}-\d{2}-\d{2}").MatchStart(name) is not null;

    /// <summary>pathlib's rglob: everything under the folder, files and folders, without following links.</summary>
    private static IEnumerable<string> Walk(string folder)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var entry in entries)
        {
            yield return entry;
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                foreach (var inner in Walk(entry))
                    yield return inner;
        }
    }

    /// <summary>A file name against a glob of literal text and "*", ignoring case (Windows).</summary>
    internal static bool Glob(string name, string glob)
    {
        var regex = "\\A" + string.Join(".*", glob.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "\\z";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    private static DateTime Due(Config config, Entry entry, DateTime begins)
    {
        var day = DateOnly.FromDateTime(begins);
        if (entry.AlwaysDayBeforeAt is { } always)
            return At(PreviousWorkingDay(day), always);
        if (entry.DuePreviousWorkingDayAt is { } previous)
            return At(PreviousWorkingDay(day), previous);
        if (entry.DueSameDayAt is { } same)   // and at least an hour before it starts
        {
            var a = At(day, same);
            var b = begins.AddHours(-1);
            return a <= b ? a : b;
        }
        if (begins.TimeOfDay < PyTime.TimeFromIso(config.FirstThingBefore))
            return At(PreviousWorkingDay(day), config.DayBeforeAt);
        return At(day, config.OnTheDayAt);
    }

    private static string Field(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v ? Py.Str(v) : "";

    private static string? Optional(JsonObject o, string key) => o[key] is JsonValue v ? Py.Str(v) : null;

    private static DateTime Time19(JsonObject e, string key) => PyTime.FromIso(PyStr.Head(Py.Str(e[key]!["dateTime"]), 19));

    internal static bool IsRoom(string name) => PyRegex.Get("(?i)mtg room|room ").MatchStart(name) is not null;

    public static PlanRow Classify(Config config, JsonObject e)
    {
        var begins = Time19(e, "start");
        var ends = Time19(e, "end");
        var subject = Py.Truthy(e["subject"]) ? Py.Str(e["subject"]) : "(no subject)";
        var row = new PlanRow(PyTime.Iso(begins, "minutes"), PyTime.Strftime(ends, "%H:%M"), subject);
        var others = new List<string>();
        foreach (var a in e["attendees"] as JsonArray ?? [])
        {
            var address = a?["emailAddress"];
            // An attendee with no name counts as an empty name (the Python stopped the whole plan on one).
            var name = Field(address, "name");
            if (Optional((JsonObject)a!, "type") == "resource" || name == config.Me
                || Field(address, "address").ToLowerInvariant() == config.Email || IsRoom(name))
                continue;
            others.Add(name);
        }
        if (Py.Truthy(e["isCancelled"]))
            return Skip(row, "cancelled");
        if (Py.Truthy(e["isAllDay"]) || begins == ends)
            return Skip(row, "all-day or marker");
        var series = config.Series(subject);
        if (series is { OwnBlock: true } && !OwnBlock(config, e, others))
            series = null;   // the same subject with other people in it is a meeting, classified as any other
        var showAs = Optional(e, "showAs");
        var declined = Field(e["responseStatus"], "response") == "declined" || showAs is "free" or "oof";
        if (declined && series is null)
            return Skip(row, $"shown as {showAs ?? "None"}");
        var day = DateOnly.FromDateTime(begins);
        if (series is not null)
        {
            if (declined && !series.OwnBlock)   // a block of your own shown as free is still yours to write in
                row.NotAttending = "declined";
            var file = PyMore.Format(series.File, new Dictionary<string, string>
            {
                ["date"] = PyTime.Iso(day), ["week"] = PyTime.Iso(WeekOf(day)), ["monday"] = PyTime.Iso(WeekStart(day)),
            });
            var plan = row.Copy();
            plan.Decision = "prep";
            plan.Kind = series.Kind;
            plan.With = null;
            plan.Series = subject;
            plan.Due = PyTime.Iso(Due(config, series, begins), "minutes");
            plan.File = file;
            if (series.Folder is not null)
                plan.Folder = series.Folder;
            plan.Exists = Exists(Path.Combine(series.Folder ?? config.Output, file));
            plan.Previous = LastPrep(config, series.File, day, series.Folder);
            return plan;
        }
        if (others.Count == 0)
            return Skip(row, "your own block");
        if (others.Count > 1)
            return Skip(row, $"group, {others.Count + 1} people");
        var with = config.Aliases.GetValueOrDefault(others[0], others[0]);
        var person = config.Person(with);
        if (person is null)
        {
            var unsure = row.Copy();
            unsure.Decision = "unsure";
            unsure.Why = $"1:1 with {others[0]}, who isn't in the settings";
            unsure.With = others[0];
            return unsure;
        }
        var values = new Dictionary<string, string> { ["date"] = PyTime.Iso(day), ["slug"] = person.Slug };
        var prep = row.Copy();
        prep.Decision = "prep";
        prep.Kind = person.Kind;
        prep.With = with;
        prep.Due = PyTime.Iso(Due(config, person, begins), "minutes");
        prep.File = PyMore.Format(person.File, values);
        prep.Exists = Exists(Path.Combine(config.Output, prep.File));
        prep.Previous = LastPrep(config, person.File, day);
        if (config.Kinds[person.Kind].Companion is { Length: > 0 } companion)
            prep.Companion = PyMore.Format(companion, values);
        return prep;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>One of your own blocks: nobody else in it, and (when the calendar says who organised it) organised by you.</summary>
    private static bool OwnBlock(Config config, JsonObject e, List<string> others)
    {
        if (others.Count > 0)
            return false;
        if (e["organizer"] is not JsonObject org || org["emailAddress"] is not JsonObject address)
            return true;
        return Optional(address, "name") == config.Me || Field(address, "address").ToLowerInvariant() == config.Email;
    }

    private static PlanRow Skip(PlanRow row, string why)
    {
        var skip = row.Copy();
        skip.Decision = "skip";
        skip.Why = why;
        return skip;
    }

    /// <summary>Days you're on leave: your own events (you organised them) whose subject matches leave_pattern. Not
    /// someone else's ("Kurt Holiday"), and not reminders ("Check team's remaining leave").</summary>
    public static HashSet<DateOnly> LeaveDays(Config config, IEnumerable<JsonObject> items)
    {
        var pattern = PyRegex.Get(config.LeavePattern, ignoreCase: true);
        var days = new HashSet<DateOnly>();
        foreach (var e in items)
        {
            var organiser = e["organizer"] is JsonObject org && org["emailAddress"] is JsonObject address ? address : new JsonObject();
            if (Optional(organiser, "name") != config.Me && Field(organiser, "address").ToLowerInvariant() != config.Email)
                continue;
            var subject = Py.Truthy(e["subject"]) ? Py.Str(e["subject"]) : "";
            if (Py.Truthy(e["isCancelled"]) || pattern.MatchStart(subject) is null)
                continue;
            var day = DateOnly.ParseExact(PyStr.Head(Py.Str(e["start"]!["dateTime"]), 10), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var last = DateOnly.FromDateTime(Time19(e, "end").AddMinutes(-1));
            while (day <= last)
            {
                days.Add(day);
                day = day.AddDays(1);
            }
        }
        return days;
    }

    /// <summary>A series' usual weekly slot that isn't in the calendar (you declined it) is prepped anyway.</summary>
    public static List<PlanRow> MissingSeries(Config config, List<PlanRow> plan, DateOnly start, int days)
    {
        var added = new List<PlanRow>();
        foreach (var (subject, series) in config.SeriesInOrder)
        {
            foreach (var slot in series.Weekly)
            {
                var weekday = Array.IndexOf(Days, slot.Day);
                if (weekday < 0)
                    throw new ArgumentException($"'{slot.Day}' is not in list");
                for (var offset = 0; offset < days; offset++)
                {
                    var day = start.AddDays(offset);
                    var iso = PyTime.Iso(day);
                    if (PyTime.Weekday(day) != weekday || plan.Any(p => p.Subject == subject && p.Start[..10] == iso))
                        continue;
                    var row = Classify(config, new JsonObject
                    {
                        ["subject"] = subject, ["showAs"] = "busy", ["attendees"] = new JsonArray(),
                        ["start"] = new JsonObject { ["dateTime"] = $"{iso}T{slot.Start}:00" },
                        ["end"] = new JsonObject { ["dateTime"] = $"{iso}T{slot.End}:00" },
                    });
                    row.NotAttending = "not in your calendar, so probably declined";
                    added.Add(row);
                }
            }
        }
        return added;
    }

    public static List<PlanRow> PlanAll(Config config, IReadOnlyList<JsonObject> items, DateOnly? start = null, int? days = null)
    {
        var plan = items.Select(e => Classify(config, e)).ToList();
        if (start is { } s && days is { } d && d != 0)
            plan = plan.Concat(MissingSeries(config, plan, s, d)).OrderBy(p => p.Start, PyStr.Order).ToList();
        var away = LeaveDays(config, items);
        foreach (var p in plan)
            if (p.Decision == "prep" && away.Contains(DateOnly.ParseExact(p.Start[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))
                p.Skip("you're on leave");
        foreach (var p in plan)
        {
            var absorbs = config.Series(p.Series ?? "")?.Absorbs;
            if (absorbs is null)
                continue;
            foreach (var q in plan)
            {
                if (q.Subject == absorbs && q.Decision == "prep" && q.Start[..10] == p.Start[..10])
                {
                    q.Skip($"covered by the {p.Subject} prep");
                    p.Absorbed = (q.Subject, q.Start, q.End);
                }
            }
        }
        return plan;
    }

    public static string Show(List<PlanRow> plan, DateOnly start, int days)
    {
        static string When(string iso) => PyTime.Strftime(PyTime.FromIso(iso), "%a %d %b %H:%M");
        var lines = new List<string> { $"Prep plan, {PyTime.Strftime(start, "%a %d %b")} for {days} days. Nothing is written.\n" };
        var preps = plan.Where(p => p.Decision == "prep").ToList();
        lines.Add($"PREP ({preps.Count})");
        foreach (var p in preps)
        {
            var extra = (p.Companion is { Length: > 0 } c ? " + " + BaseName(c) : "") + (p.Exists ? " [already written]" : "");
            lines.Add($"  {When(p.Start)}  {p.Subject}  [{p.Kind}]\n      written {When(p.Due!)} -> {p.File}{extra}");
        }
        var unsure = plan.Where(p => p.Decision == "unsure").ToList();
        lines.Add($"\nUNSURE ({unsure.Count})");
        lines.AddRange(unsure.Select(p => $"  {When(p.Start)}  {p.Subject}: {p.Why}"));
        var skipped = plan.Where(p => p.Decision == "skip").ToList();
        lines.Add($"\nSKIP ({skipped.Count})");
        lines.AddRange(skipped.Select(p => $"  {When(p.Start)}  {p.Subject}: {p.Why}"));
        return string.Join("\n", lines);
    }

    /// <summary>PureWindowsPath(x).name.</summary>
    internal static string BaseName(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        var cut = p.LastIndexOf('\\');
        var name = cut >= 0 ? p[(cut + 1)..] : p;
        return name.Length >= 2 && name[1] == ':' && cut < 0 ? name[2..] : name;
    }

    /// <summary>The plan as `plan --json` prints it: json.dumps(plan, indent=2).</summary>
    public static string Json(List<PlanRow> plan) => PyJson.Dump(new JsonArray(plan.Select(p => (JsonNode)p.Json.DeepClone()).ToArray()), 2);
}

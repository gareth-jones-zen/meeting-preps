using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHeadless;
using PyCompat;
using WinBackground;

namespace MeetingPreps;

/// <summary>Another run is writing the same file (Python's TimeoutError from win_background.exclusive). Its own type,
/// because a Claude run that times out is a TimeoutException and must count as a failure.</summary>
public sealed class LockTimeoutException(string message) : Exception(message);

/// <summary>No meeting that day gets a prep with that person or series (Python's LookupError).</summary>
public sealed class NoSuchMeetingException(string message) : Exception(message);

/// <summary>The calendar couldn't be read and there's no recent copy to plan from (Python's CalendarUnavailable).</summary>
public sealed class CalendarUnavailableException(string message) : Exception(message);

/// <summary>Writing preps, and keeping them current, as the Python's engine.py.
///
/// **run** writes every prep that's due and not yet written (so a run missed while the laptop slept is caught up by
/// the next), then does the pre-meeting refresh. **update** checks preps for meetings still to come (to the end of
/// the next working day) against transcripts that have just landed, and adds a dated "Update HH:MM" block to each
/// one they affect. **refresh**: once per 1:1, in the hour before it, email and Teams with that person since the
/// prep was written are checked. **Claude runs headless in the workspace with read-only tools;** this writes the
/// files. after_write is given a JSON report for every file written.
///
/// **A Microsoft 365 outage doesn't stop preps.** Each run reads the calendar once, for the next CalendarDays days,
/// and keeps a copy (calendar_cache). When the read fails, the run plans from that copy, if it's no older than
/// calendar_cache_days, for the days it covers. Email, Teams, the diary and the context commands each degrade on
/// their own, and a source that has failed isn't asked again in the same run (each failed Graph call costs a minute
/// or more of retries). A prep written without something says so in a line under its title. **One notification per
/// outage**, when the first degraded prep is written or the third run in a row meets it, and none per meeting; the
/// outage ends once everything that failed has been read again. A sign-in problem with the calendar still stops the
/// run, so the watcher asks for a sign-in; so does a calendar that's down with no recent copy, which the watcher's
/// "couldn't run" covers.</summary>
public sealed class Engine(Config config, IGraph graph, Action<string> log, PrepsHost host)
{
    /// <summary>One read a run covers the plan (to the next working day) and the diary (7 days).</summary>
    public const int CalendarDays = 8;

    private static readonly PyRegex Fence = PyRegex.Get(@"^```(?:markdown|md)?\s*\n|\n```\s*$");
    private static readonly PyRegex BlockAt = PyRegex.Get(@"(?m)^(> \*\*Update |## )");
    // re.findall with a group. PyCompat's PyRegex has no finditer yet, so the pattern is translated by PyRegex and
    // run as a .NET Regex, with the options PyRegex would give it (interim, until the library has one).
    private static readonly Regex Speaker = Translated(@"(?m)^\*\*(.+?)\*\* \d");
    private static readonly PyRegex Marks = PyRegex.Get("[#*_>|-]");

    public Config Config { get; } = config;
    private Clock Clock => host.Clock;

    // This run's view of Graph: what failed (label -> why, in the order it failed), what worked, when it first
    // failed, the calendar read once for the run, when the cached copy it fell back to was read (null if live), and
    // how many preps were written without something.
    private readonly OrderedDictionary<string, string> down = new(StringComparer.Ordinal);
    private readonly HashSet<string> ok = new(StringComparer.Ordinal);
    private DateTime? firstFailure;
    private (List<JsonObject> Items, DateOnly First, int Span)? week;

    /// <summary>When the cached copy of the calendar this run planned from was read; null if it read it live.</summary>
    public DateTime? CalendarFrom { get; private set; }

    /// <summary>How many preps this run wrote without something.</summary>
    public int Degraded { get; private set; }

    // ---- Graph, and what to do when it's down -----------------------------------------------------------------

    private string Unavailable(string label, Exception error)
    {
        var why = PyMore.Reason(error);
        down.TryAdd(label, why);
        firstFailure ??= Clock.Now();
        log($"  {label} unavailable: {why}");
        return why;
    }

    /// <summary>One context source (email, Teams). A failure becomes "(unavailable: why)" in the prompt and an entry in
    /// gaps, never an error; a source that has failed this run isn't asked again.</summary>
    public string Read(string label, Func<string> fetch, OrderedDictionary<string, string> gaps)
    {
        if (down.TryGetValue(label, out var why))
        {
            gaps[label] = why;
            return $"(unavailable: {why})";
        }
        string text;
        try
        {
            text = fetch();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            gaps[label] = Unavailable(label, error);
            return $"(unavailable: {gaps[label]})";
        }
        ok.Add(label);
        return text;
    }

    /// <summary>Replaces a file with one written beside it (os.replace). Retried briefly, as another program (a virus
    /// scanner, say) can hold the old one for a moment.</summary>
    private static void Replace(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private void SaveCalendar(DateOnly start, int days, List<JsonObject> items)
    {
        var cache = Config.CalendarCache;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var temp = cache + ".tmp";
            var data = new JsonObject
            {
                ["read"] = PyTime.Iso(Clock.Now(), "seconds"), ["start"] = PyTime.Iso(start), ["days"] = PyJson.Int(days),
                ["timezone"] = Config.TimeZone, ["events"] = new JsonArray(items.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            };
            PyStr.WriteText(temp, PyJson.Dump(data));
            Replace(temp, cache);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log($"  couldn't keep a copy of the calendar: {error.Message}");
        }
    }

    /// <summary>Python's KeyError, TypeError or ValueError while reading the copy: it's unusable.</summary>
    private sealed class UnusableException(string message) : Exception(message);

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String;

    /// <summary>(items, first day, days) from the last good read, or CalendarUnavailableException saying why not.</summary>
    private (List<JsonObject> Items, DateOnly First, int Span) CachedCalendar(string why)
    {
        DateTime read;
        (List<JsonObject>, DateOnly, int) found;
        try
        {
            var data = PyJson.Parse(PyJson.ReadStrict(Config.CalendarCache));
            JsonNode? Key(string key) => data switch
            {
                JsonObject o => o.TryGetPropertyValue(key, out var v) ? v : throw new UnusableException(Py.ReprString(key)),
                JsonArray => throw new UnusableException("list indices must be integers or slices, not str"),
                _ when IsString(data) => throw new UnusableException("string indices must be integers, not 'str'"),
                _ => throw new UnusableException($"'{PyType(data)}' object is not subscriptable"),
            };
            var readNode = Key("read");
            read = IsString(readNode) ? PyTime.FromIso(readNode!.GetValue<string>()) : throw new UnusableException("fromisoformat: argument must be str");
            var zone = data is JsonObject d && IsString(d["timezone"]) ? d["timezone"]!.GetValue<string>() : null;
            if (zone != Config.TimeZone)
                throw new UnusableException("it's in a different time zone");
            var events = Key("events");
            var startNode = Key("start");
            var startText = IsString(startNode) ? startNode!.GetValue<string>() : throw new UnusableException("fromisoformat: argument must be str");
            if (!DateOnly.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                throw new UnusableException($"Invalid isoformat string: {Py.ReprString(startText)}");
            var daysNode = Key("days");
            var days = IsString(daysNode) ? PyStr.Int(daysNode!.GetValue<string>()) : Py.Int(daysNode);
            found = (((JsonArray)events!).Select(e => (JsonObject)e!).ToList(), start, checked((int)days));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CalendarUnavailableException($"calendar unavailable ({why}), and there's no copy of an earlier read");
        }
        catch (Exception error) when (error is UnusableException or IOException or UnauthorizedAccessException or FormatException
                                          or System.Text.Json.JsonException or DecoderFallbackException or OverflowException
                                          or InvalidCastException or InvalidOperationException)
        {
            throw new CalendarUnavailableException($"calendar unavailable ({why}), and the copy of an earlier read is unusable ({error.Message})");
        }
        if (Clock.Now() - read > TimeSpan.FromDays(Config.CalendarCacheDays))
            throw new CalendarUnavailableException($"calendar unavailable ({why}), and the last good read ({F(read, "%a %d %b %H:%M")}) is over "
                                                   + $"{PyMore.FormatG(Config.CalendarCacheDays)} days old");
        if (CalendarFrom is null)
            log($"  planning from the calendar as read {F(read, "%a %d %b %H:%M")}: meeting changes since may be missing");
        CalendarFrom = read;
        return found;
    }

    private static string PyType(JsonNode? node) => node switch
    {
        null => "NoneType",
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "bool",
            System.Text.Json.JsonValueKind.Number => v.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0 ? "float" : "int",
            _ => "str",
        },
        _ => "object",
    };

    /// <summary>The calendar's events from start for days, as (items, start, days). Live if Graph answers, else from the
    /// last good read, clipped to the days that read covered, so nothing is planned for days it never saw.</summary>
    public (List<JsonObject> Items, DateOnly Start, int Days) Calendar(DateOnly start, int days)
    {
        var today = DateOnly.FromDateTime(Clock.Now());
        var standard = today <= start && start.AddDays(days) <= today.AddDays(CalendarDays);
        List<JsonObject> items;
        DateOnly first;
        int span;
        if (standard && week is { } kept)
            (items, first, span) = kept;
        else
        {
            (first, span) = standard ? (today, CalendarDays) : (start, days);
            List<JsonObject>? got = null;
            if (!down.ContainsKey("calendar"))
            {
                try
                {
                    got = Planner.Events(graph, Config, first, span);
                    ok.Add("calendar");
                    if (standard)
                        SaveCalendar(first, span, got);
                }
                catch (M365Graph.SignInNeededException)
                {
                    throw;   // yours to fix, not an outage: the watcher asks for a sign-in
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Unavailable("calendar", error);
                }
            }
            if (got is null)
                (got, first, span) = CachedCalendar(down["calendar"]);
            items = got;
            if (standard)
                week = (items, first, span);
        }
        var begin = start > first ? start : first;
        var end = start.AddDays(days) < first.AddDays(span) ? start.AddDays(days) : first.AddDays(span);
        if (end <= begin)
            return ([], start, 0);
        string lo = PyTime.Iso(begin), hi = PyTime.Iso(end);
        var inside = items.Where(e => string.CompareOrdinal(PyStr.Head(Py.Str(e["start"]!["dateTime"]), 10), hi) < 0
                                      && string.CompareOrdinal(PyStr.Head(Py.Str(e["end"]!["dateTime"]), 19), lo) > 0).ToList();
        return (inside, begin, end.DayNumber - begin.DayNumber);
    }

    /// <summary>The line under a prep's title saying what it was written without, or "" if nothing.</summary>
    public string GapsLine(OrderedDictionary<string, string> gaps, IReadOnlyList<string> failedCommands)
    {
        var parts = new List<string>();
        var missing = gaps.Where(g => g.Key != "calendar").ToList();
        if (missing.Count > 0)
            parts.Add($"Microsoft 365 unavailable ({string.Join("; ", missing.Select(m => m.Value).Distinct().Order(PyStr.Order))}): "
                      + $"{PyMore.AndList(missing.Select(m => m.Key).OrderBy(PyStr.Lower, PyStr.Order).ToList())} not read.");
        if (CalendarFrom is { } from)
            parts.Add($"Written from the calendar as read {F(from, "%a %d %b %H:%M")} (Microsoft 365 unavailable: "
                      + $"{down.GetValueOrDefault("calendar", "unknown")}); meeting changes since may be missing.");
        else if (gaps.TryGetValue("calendar", out var calendar))
            parts.Add($"Diary not read ({calendar}).");
        parts.AddRange(failedCommands.Select(c => $"Context command {c} failed."));
        return parts.Count > 0 ? "> **Not everything could be read:** " + string.Join(" ", parts) : "";
    }

    /// <summary>The note under the title and its italic line, above everything else.</summary>
    public static string WithNote(string text, string note)
    {
        var lines = text.Split('\n').ToList();
        var at = 1;
        for (var i = 0; i < Math.Min(6, lines.Count); i++)
            if (lines[i].StartsWith("*Written automatically", StringComparison.Ordinal))
            {
                at = i + 1;
                break;
            }
        var rest = lines.Skip(at).ToList();
        while (rest.Count > 0 && PyStr.Strip(rest[0]).Length == 0)
            rest.RemoveAt(0);
        return string.Join("\n", lines.Take(at).Concat(["", note, ""]).Concat(rest));
    }

    // ---- the outage, across runs ----------------------------------------------------------------------------

    private JsonObject State()
    {
        try
        {
            return PyJson.Parse(PyStr.ReadText(Config.StateFile)) as JsonObject ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                          or DecoderFallbackException)
        {
            return [];
        }
    }

    /// <summary>Change one key of the state file (null removes it), keeping the rest; replaced whole, so never
    /// half-written.</summary>
    private void SaveState(string name, JsonNode? value)
    {
        var state = State();
        if (value is null)
            state.Remove(name);
        else
            state[name] = value.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(Config.StateFile)!);
        var temp = Config.StateFile + ".tmp";
        PyStr.WriteText(temp, PyJson.Dump(state, 2));
        Replace(temp, Config.StateFile);
    }

    /// <summary>After a run: start, carry on or end the outage record, and notify once per outage. ran=false is a run
    /// that couldn't plan at all: counted, but the watcher's "couldn't run" is the notice for that.</summary>
    public void SettleOutage(bool ran = true)
    {
        var outage = State()["outage"] is JsonObject o && o.Count > 0 ? (JsonObject)o.DeepClone() : null;
        List<string> What() => outage?["what"] is JsonArray what ? what.Select(Py.Str).ToList() : [];
        if (down.Count == 0)
        {
            if (outage is not null && What().All(ok.Contains))
            {
                var since = PyTime.FromIso(Py.Str(outage["since"]));
                log($"Microsoft 365 is back (unavailable since {F(since, "%a %H:%M")})");
                SaveState("outage", null);
            }
            return;
        }
        outage ??= new JsonObject
        {
            ["since"] = PyTime.Iso(firstFailure ?? Clock.Now(), "minutes"), ["runs"] = PyJson.Int(0), ["notified"] = false,
        };
        var runs = Py.Int(outage["runs"]) + 1;
        outage["runs"] = PyJson.Int(runs);
        outage["what"] = new JsonArray(What().Union(down.Keys).Distinct().Order(PyStr.Order).Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
        var why = down.GetAt(0).Value;
        outage["why"] = why;
        if (ran && !Py.Truthy(outage["notified"]) && (Degraded > 0 || runs >= 3))
        {
            var since = PyTime.FromIso(Py.Str(outage["since"]));
            var when = DateOnly.FromDateTime(since) == DateOnly.FromDateTime(Clock.Now()) ? F(since, "%H:%M") : F(since, "%a %d %b %H:%M");
            var cached = CalendarFrom is not null ? " and the cached calendar" : "";
            host.Notify("Microsoft 365 unavailable", [$"Since {when} ({why}); preps are being written from the repo{cached}.", $"Log: {Config.LogFile}"]);
            log($"notified: Microsoft 365 unavailable since {when}");
            outage["notified"] = true;
        }
        SaveState("outage", outage);
    }

    /// <summary>{name} and {slug} in your own rules text, without str.format, which a stray brace in prose would break.
    /// Each is replaced in turn, so a value can itself be filled by a later one (as the Python's replace did).</summary>
    public static string Fill(string text, params (string Key, string Value)[] values)
    {
        foreach (var (k, v) in values)
            text = text.Replace("{" + k + "}", v, StringComparison.Ordinal);
        return text;
    }

    private static string F(DateTime t, string format) => PyTime.Strftime(t, format);

    private string OutputPath(string file) => PyPath.Pure(Path.Combine(Config.Output, file));

    /// <summary>Where a plan's file goes: its series' own folder, or output.</summary>
    public string Target(PlanRow plan) => PyPath.Pure(Path.Combine(plan.Folder ?? Config.Output, plan.File!));

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // ---- the pieces ---------------------------------------------------------------------------------------

    /// <summary>The command line for a headless run (everything after `claude`).</summary>
    public List<string> ClaudeCommand()
    {
        var command = new List<string> { "-p", "--output-format", "json", "--model", Config.Model,
            "--tools", "Read,Grep,Glob", "--strict-mcp-config", "--permission-mode", "dontAsk" };
        // The Python iterated a set of the two, so their order varied from run to run; here it's output, then history.
        var extras = new List<string> { Config.Output };
        if (!PyPath.Same(Config.History, Config.Output))
            extras.Add(Config.History);
        foreach (var (_, series) in Config.SeriesInOrder)   // a series' own folder, where its earlier documents are
            if (series.Folder is { } folder && !extras.Any(e => PyPath.Same(e, folder)))
                extras.Add(folder);
        foreach (var extra in extras)
            if (!Config.IsRelativeTo(extra, Config.Workspace))
                command.AddRange(["--add-dir", extra]);
        return command;
    }

    public string Claude(string prompt, int timeoutSeconds)
    {
        var result = host.Claude.Run(ClaudeCommand(), prompt, Config.Workspace, TimeSpan.FromSeconds(timeoutSeconds));
        log($"  claude: {Py.Str(result["num_turns"])} turns, session {Py.Str(result["session_id"])}");
        return Fence.Sub("", PyStr.Strip(Py.Str(result["result"])));
    }

    /// <summary>One writer per prep file, across processes and across the Python and .NET versions.</summary>
    public string LockName(string file) => $"{host.LockPrefix}meeting-preps-{Locks.Key(Config.Path)}-{Locks.Key(file)}";

    private NamedLock Lock(string file, double wait = 1800)
    {
        var held = new NamedLock(LockName(file));
        if (!held.Acquire(wait))
        {
            held.Dispose();
            var minutes = Math.Round(wait / 60, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
            throw new LockTimeoutException($"Another write of {file} didn't finish within {minutes} minutes.");
        }
        return held;
    }

    private void Limited(IReadOnlyList<string> args, LimitReachedException limit)
    {
        List<string> argv = [host.Exe, "--config", Config.Path, .. args];
        var reset = limit.Reset.DateTime;
        log($"  Claude's usage limit; queued `{string.Join(' ', args)}` for after {F(reset, "%a %H:%M")}");
        if (host.QueueRetry(argv, Config.Workspace, limit.Reset))
            host.Notify("Claude usage limit reached", [$"Preps will carry on after {F(reset, "%H:%M")}."]);
    }

    /// <summary>The after_write report, as json.dumps wrote it.</summary>
    public static string Report(string type, string meeting, string start, IEnumerable<string> files, string? from = null)
    {
        var report = new JsonObject
        {
            ["type"] = type, ["meeting"] = meeting, ["start"] = start,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        };
        if (from is not null)
            report["from"] = from;
        return PyJson.Dump(report);
    }

    private void AfterWrite(string report)
    {
        if (Config.AfterWrite.Count == 0)
            return;
        try
        {
            var done = host.Processes.Run(new ProcessRequest(Config.AfterWrite, Path.GetDirectoryName(Config.Path)!, report,
                new Dictionary<string, string>(), TimeSpan.FromSeconds(600)));
            if (done.ExitCode != 0)
                log($"  after_write exited {done.ExitCode}: {PyStr.Tail(done.StandardError.Length > 0 ? done.StandardError : done.StandardOutput, 300)}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"  after_write failed: {PyMore.ErrorText(error)}");
        }
    }

    /// <summary>Each command's output is added to the kind's rules. It's told which meeting: MEETING_PREPS_START (ISO),
    /// MEETING_PREPS_SUBJECT and MEETING_PREPS_WITH (empty for a series). A command that fails or exits non-zero is
    /// added to failed, for the prep's first lines; it never stops the prep.</summary>
    public string ContextCommands(Kind kind, PlanRow plan, List<string>? failed = null)
    {
        failed ??= [];
        var output = new List<string>();
        var env = new Dictionary<string, string>
        {
            ["MEETING_PREPS_START"] = plan.Start, ["MEETING_PREPS_SUBJECT"] = plan.Subject, ["MEETING_PREPS_WITH"] = plan.With ?? "",
        };
        foreach (var command in kind.ContextCommands)
        {
            var label = PyMore.ListRepr(command.Take(2));
            try
            {
                var done = host.Processes.Run(new ProcessRequest(command, Path.GetDirectoryName(Config.Path)!, null, env, TimeSpan.FromSeconds(900)));
                if (PyStr.Strip(done.StandardOutput).Length > 0)
                    output.Add(PyStr.Strip(done.StandardOutput));
                if (done.ExitCode != 0)
                {
                    log($"  context command {label} exited {done.ExitCode}: {PyStr.Head(PyStr.Strip(done.StandardError), 200)}");
                    failed.Add($"{Planner.BaseName(command[^1])} (exit {done.ExitCode})");
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log($"  context command {label} failed: {PyStr.Head(PyMore.ErrorText(error), 200)}");
                failed.Add($"{Planner.BaseName(command[^1])} ({PyMore.PyName(error)})");
            }
        }
        return string.Join("\n", output);
    }

    /// <summary>Every meeting's decision (prep, unsure, skip), from the live calendar or its last good copy.</summary>
    public List<PlanRow> PlanRows(DateOnly start, int days)
    {
        var (items, first, span) = Calendar(start, days);
        return span != 0 ? Planner.PlanAll(Config, items, first, span) : [];
    }

    public List<PlanRow> Plans(DateOnly start, int days) => PlanRows(start, days).Where(p => p.Decision == "prep").ToList();

    /// <summary>Preps for meetings from now to the end of the next working day. A block of your own (own_block) stays
    /// in for the rest of its day: you may write in it later that day, and a run that wakes after it started (a usage
    /// limit's retry, say) must still write it.</summary>
    public List<PlanRow> Upcoming(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var end = Planner.NextWorkingDay(today);
        return Plans(today, end.DayNumber - today.DayNumber + 1)
            .Where(p => PyTime.FromIso(p.Start) > now
                        || (Config.Series(p.Series ?? "") is { OwnBlock: true } && p.Start[..10] == PyTime.Iso(today)))
            .ToList();
    }

    /// <summary>{date}, {week}, {monday} and {file} for a plan, as the rules text can use them.</summary>
    public (string Key, string Value)[] Tokens(PlanRow plan)
    {
        var day = DateOnly.FromDateTime(PyTime.FromIso(plan.Start));
        return [("date", PyTime.Iso(day)), ("week", PyTime.Iso(Planner.WeekOf(day))), ("monday", PyTime.Iso(Planner.WeekStart(day))),
            ("file", Config.Relative(Target(plan)))];
    }

    public static string When(PlanRow plan)
    {
        var start = PyTime.FromIso(plan.Start);
        return $"{F(start, "%A %d %B %Y")}, {F(start, "%H:%M")}–{plan.End}";
    }

    // ---- writing ------------------------------------------------------------------------------------------

    /// <summary>The prompt for one prep, gathering its context (Graph, context commands) as the Python did, in the
    /// same order, and the line saying what it lacked ("" if nothing).</summary>
    public (string Prompt, string Note) PrepPrompt(PlanRow plan, DateTime now)
    {
        var kind = Config.Kinds[plan.Kind!];
        var gaps = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var failed = new List<string>();
        string name, kindText, teams, mail, shortName;
        if (plan.Series is not null)
        {
            var series = Config.Series(plan.Series)!;
            name = $"the {plan.Subject} group";
            teams = Read("Teams", () => Context.Teams(graph, null, Clock, topic: series.Chat), gaps);
            kindText = Fill(kind.Describe, ("name", plan.Subject), ("slug", ""));
            if (plan.Absorbed is { } absorbed)
                kindText += $" (with {absorbed.Subject} at {absorbed.Start[11..]}–{absorbed.End} folded in)";
            if (plan.NotAttending is { Length: > 0 } notAttending)
                kindText += $". {Config.Owner} isn't attending this one ({notAttending}): write it as what they "
                            + "might raise or pass on through someone who is there, short";
            mail = Read("email", () => Context.Mail(graph, Config, null, Clock, days: 14, log: log), gaps);
            shortName = plan.Subject;
        }
        else
        {
            var person = Config.Person(plan.With!)!;
            name = plan.With!;
            kindText = Fill(kind.Describe, ("name", name), ("slug", person.Slug));
            var who = name;
            teams = Read("Teams", () => Context.Teams(graph, who, Clock), gaps);
            mail = Read("email", () => Context.Mail(graph, Config, who, Clock, days: 14, log: log), gaps);
            shortName = PyStr.Split(name)[0];
        }
        var extra = ContextCommands(kind, plan, failed);
        string diary;
        try
        {
            diary = Context.Diary(Config, Calendar(DateOnly.FromDateTime(now), 7).Items);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // no calendar and no recent copy, or a sign-in: the prep goes ahead without it
            gaps["calendar"] = down.TryGetValue("calendar", out var why) && why.Length > 0 ? why : PyMore.Reason(error);
            diary = $"(unavailable: {gaps["calendar"]})";
        }
        var note = GapsLine(gaps, failed);
        var rules = Fill(kind.Rules, [("name", name), ("slug", Config.Person(plan.With ?? "")?.Slug ?? ""), .. Tokens(plan)]);
        var companion = "";
        if (plan.Companion is { Length: > 0 })
            companion = PyMore.Format(PromptText.Companion, new Dictionary<string, string>
            {
                ["companion_file"] = plan.Companion, ["companion_rules"] = kind.CompanionRules,
            });
        var values = new Dictionary<string, string>
        {
            ["owner"] = Config.Owner, ["kind_text"] = kindText, ["subject"] = plan.Subject, ["when"] = When(plan),
            ["now"] = F(now, "%A %d %B %Y %H:%M"), ["kind_rules"] = rules + (extra.Length > 0 ? $"\n\n{extra}" : ""),
            ["previous"] = plan.Previous is { Length: > 0 } previous ? previous : "(none yet)",
            ["history"] = Config.Relative(Config.History), ["name"] = name, ["short"] = shortName,
            ["house_rules"] = House(Config.HouseRules), ["companion"] = companion,
            ["gaps"] = note.Length > 0 ? PyMore.Format(PromptText.Gaps, new Dictionary<string, string> { ["note"] = RemovePrefix(note, "> ") }) : "",
            ["diary"] = diary, ["teams"] = teams, ["mail"] = mail,
        };
        return (PyMore.Format(PromptText.Prep, values), note);
    }

    /// <summary>The prompt for a document kind (a weekly update written in a block of your own): its rules, then the
    /// week's diary and email (and Teams, if the series names a chat), without a prep's scaffolding. "The week" is
    /// from the Monday of the block's week to now.</summary>
    public (string Prompt, string Note) DocumentPrompt(PlanRow plan, DateTime now)
    {
        var kind = Config.Kinds[plan.Kind!];
        var gaps = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var failed = new List<string>();
        var monday = Planner.WeekStart(DateOnly.FromDateTime(PyTime.FromIso(plan.Start)));
        var since = Clock.Aware(monday.ToDateTime(TimeOnly.MinValue));
        var series = plan.Series is not null ? Config.Series(plan.Series) : null;
        var name = plan.With ?? plan.Subject;
        var slug = Config.Person(plan.With ?? "")?.Slug ?? "";
        var kindText = Fill(kind.Describe, ("name", name), ("slug", slug));
        var mail = Read("email", () => Context.Mail(graph, Config, null, Clock, sinceTime: since, log: log), gaps);
        var chat = series?.Chat;
        var teams = chat is { Length: > 0 } ? Read("Teams", () => Context.Teams(graph, null, Clock, topic: chat, sinceTime: since), gaps) : null;
        var extra = ContextCommands(kind, plan, failed);
        string diary;
        try
        {
            diary = Context.Diary(Config, Calendar(monday, 7).Items);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            gaps["calendar"] = down.TryGetValue("calendar", out var why) && why.Length > 0 ? why : PyMore.Reason(error);
            diary = $"(unavailable: {gaps["calendar"]})";
        }
        var note = GapsLine(gaps, failed);
        var rules = Fill(kind.Rules, [("name", name), ("slug", slug), .. Tokens(plan)]);
        var values = new Dictionary<string, string>
        {
            ["owner"] = Config.Owner, ["kind_text"] = kindText, ["subject"] = plan.Subject, ["when"] = When(plan),
            ["now"] = F(now, "%A %d %B %Y %H:%M"), ["monday"] = F(monday.ToDateTime(TimeOnly.MinValue), "%a %d %b %Y"),
            ["file"] = Config.Relative(Target(plan)),
            ["previous"] = plan.Previous is { Length: > 0 } previous ? previous : "(none yet)",
            ["kind_rules"] = rules + (extra.Length > 0 ? $"\n\n{extra}" : ""),
            ["gaps"] = note.Length > 0 ? PyMore.Format(PromptText.Gaps, new Dictionary<string, string> { ["note"] = RemovePrefix(note, "> ") }) : "",
            ["teams_note"] = teams is null ? "" : ", then the Teams messages in their chat",
            ["diary"] = diary, ["mail"] = mail,
            ["teams"] = teams is null ? "" : $"\n\nTeams messages this week (oldest first):\n{teams}",
        };
        return (PyMore.Format(PromptText.Document, values), note);
    }

    private (string Prompt, string Note) PromptOf(PlanRow plan, DateTime now) =>
        Config.Kinds[plan.Kind!].Document ? DocumentPrompt(plan, now) : PrepPrompt(plan, now);

    private static string RemovePrefix(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    /// <summary>prompts.house(): the house rules, introduced, or nothing.</summary>
    public static string House(string rules) =>
        PyStr.Strip(rules).Length > 0 ? $"\nHouse rules, which win where they differ from the above:\n{PyStr.Strip(rules)}\n" : "";

    /// <summary>Write one prep, and its companion if its kind has one, to their usual places or into outDir. Returns
    /// the files written.</summary>
    public List<string> Write(PlanRow plan, string? outDir = null)
    {
        var now = Clock.Now();
        var (prompt, note) = PromptOf(plan, now);
        log($"writing {plan.File}");
        var text = Claude(prompt, 3600);
        var cut = text.IndexOf("===COMPANION===", StringComparison.Ordinal);
        var prepText = cut >= 0 ? text[..cut] : text;
        var companionText = cut >= 0 ? text[(cut + "===COMPANION===".Length)..] : "";
        var draft = prepText.IndexOf("# [DRAFT]", StringComparison.Ordinal);
        prepText = draft >= 0 ? PyStr.Strip(prepText[draft..]) : "";
        if (prepText.Length == 0)
            throw new InvalidOperationException("the reply wasn't a prep: " + PyStr.Head(text, 200));
        if (note.Length > 0)
        {
            prepText = WithNote(prepText, note);
            Degraded++;
            log($"  written without some sources: {RemovePrefix(note, "> **Not everything could be read:** ")}");
        }
        var written = new List<string>();
        var path = outDir is null ? Target(plan) : PyPath.Pure(Path.Combine(outDir, Planner.BaseName(plan.File!)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PyStr.WriteText(path, prepText + "\n");
        written.Add(path);
        if (plan.Companion is { Length: > 0 })
        {
            companionText = PyStr.Strip(companionText);
            var at = companionText.IndexOf("# [DRAFT]", StringComparison.Ordinal);
            if (at >= 0)
            {
                var second = outDir is null ? OutputPath(plan.Companion) : PyPath.Pure(Path.Combine(outDir, Planner.BaseName(plan.Companion)));
                PyStr.WriteText(second, companionText[at..] + "\n");
                written.Add(second);
                log($"  companion: {PyStr.Split(Marks.Sub(" ", companionText)).Count} words");
            }
            else
                log("  no companion document in the reply");
        }
        foreach (var item in written)
            log($"  wrote {item}");
        return written;
    }

    public int Run(bool dryRun = false)
    {
        var now = Clock.Now();
        List<PlanRow> upcoming;
        try
        {
            upcoming = Upcoming(now);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!dryRun)
                SettleOutage(ran: false);
            throw;
        }
        var due = upcoming.Where(p => PyTime.FromIso(p.Due!) <= now && !Exists(Target(p))).ToList();
        log($"run{(dryRun ? " (dry run)" : "")}: {due.Count} due");
        var failures = 0;
        foreach (var plan in due)
        {
            if (dryRun)
            {
                log($"  would write {plan.File}");
                continue;
            }
            List<string> written;
            try
            {
                using (Lock(plan.File!, 60))
                {
                    if (Exists(Target(plan)))
                        continue;
                    written = Write(plan);
                }
            }
            catch (LimitReachedException limit)
            {
                Limited(["run"], limit);
                SettleOutage();
                return 0;
            }
            catch (LockTimeoutException)
            {
                // Another run (a `write` by hand, say) is writing it now: not a failure, and the next run checks.
                log($"  {plan.File} is being written by another run; leaving it");
                continue;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Graph failures don't get here (they make a degraded prep), so this is the prep's own: Claude, or the file.
                failures++;
                log($"  FAILED {plan.File}: {PyMore.ErrorText(error)}");
                host.Notify("Prep failed", [plan.Subject, $"Log: {Config.LogFile}"]);
                continue;
            }
            AfterWrite(Report("prep", plan.Subject, plan.Start, written.Select(Config.Relative)));
        }
        failures += Refresh(dryRun);
        if (!dryRun)
            SettleOutage();
        return failures > 0 ? 1 : 0;
    }

    // ---- keeping it current -------------------------------------------------------------------------------

    /// <summary>Above any earlier update and the first heading, so the newest news is first.</summary>
    public static void AddBlock(string prep, string block)
    {
        var text = PyStr.ReadText(prep);
        var cut = BlockAt.Search(text) is { } m ? m.Index : text.Length;
        PyStr.WriteText(prep, text[..cut] + block + "\n\n" + text[cut..]);
    }

    private static Regex Translated(string pattern)
    {
        var (translated, options) = PyRegex.Translate(pattern);
        return new Regex(translated, options | RegexOptions.CultureInvariant, PyRegex.Timeout);
    }

    public static HashSet<string> Speakers(string transcript) =>
        Speaker.Matches(PyStr.ReadText(transcript)).Select(m => m.Groups[1].Value).ToHashSet();

    public HashSet<string> NamesFor(PlanRow plan)
    {
        if (plan.Series is not null)
            return Config.Series(plan.Series)!.Members.ToHashSet();
        var names = Config.AliasesInOrder.Where(a => a.Value == plan.With).Select(a => a.Key).ToHashSet();
        names.Add(plan.With!);
        return names;
    }

    /// <summary>"folder stem", as the Python named a transcript's source.</summary>
    private static string Source(string transcript)
    {
        var parent = Path.GetDirectoryName(transcript)!;
        var folder = Path.GetFileName(parent) == "one-to-ones" ? Path.GetFileName(Path.GetDirectoryName(parent)!) : Path.GetFileName(parent);
        return $"{folder} {Path.GetFileNameWithoutExtension(transcript)}";
    }

    public int Update(IEnumerable<string> paths)
    {
        var now = Clock.Now();
        var transcripts = paths.Where(Exists).Select(PyPath.Resolve).ToList();
        if (transcripts.Count == 0)
            return 0;
        var voices = transcripts.SelectMany(Speakers).ToHashSet();
        var source = string.Join(", ", transcripts.Select(Source));
        var failures = 0;
        foreach (var plan in Upcoming(now))
        {
            var prep = Target(plan);
            var kind = Config.Kinds[plan.Kind!];
            if (!Exists(prep) || (!kind.EveryTranscript && !voices.Overlaps(NamesFor(plan))))
                continue;
            var prepName = Path.GetFileName(prep);
            log($"update: checking {prepName} against {source}");
            try
            {
                using (Lock(plan.File!))
                {
                    var values = new Dictionary<string, string>
                    {
                        ["owner"] = Config.Owner, ["prep"] = Config.Relative(prep), ["subject"] = plan.Subject, ["when"] = When(plan),
                        ["now"] = F(now, "%A %d %B %Y %H:%M"), ["transcripts"] = string.Join(", ", transcripts.Select(Config.Relative)),
                        ["hhmm"] = F(Clock.Now(), "%H:%M"), ["source"] = source,
                        ["update_rules"] = kind.UpdateRules.Length > 0 ? " " + kind.UpdateRules : "",
                        ["house_rules"] = House(Config.HouseRules),
                    };
                    if (kind.Document)
                    {
                        var name = plan.With ?? plan.Subject;
                        values["kind_text"] = Fill(kind.Describe, ("name", name), ("slug", ""));
                        values["kind_rules"] = Fill(kind.Rules, [("name", name), ("slug", ""), .. Tokens(plan)]);
                    }
                    var block = PyStr.Strip(Claude(PyMore.Format(kind.Document ? PromptText.DocumentUpdate : PromptText.Update, values), 1800));
                    if (!block.StartsWith("> **Update", StringComparison.Ordinal))
                    {
                        log($"  nothing for {prepName}" + (block.StartsWith("NO UPDATE", StringComparison.Ordinal) ? "" : $": {PyStr.Head(block, 120)}"));
                        continue;
                    }
                    AddBlock(prep, block);
                    log($"  added an update to {prepName}");
                }
            }
            catch (LimitReachedException limit)
            {
                Limited(["update", .. transcripts], limit);
                return 0;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures++;
                log($"  FAILED update of {prepName}: {PyMore.ErrorText(error)}");
                continue;
            }
            AfterWrite(Report("update", plan.Subject, plan.Start, [Config.Relative(prep)], source));
        }
        return failures > 0 ? 1 : 0;
    }

    private JsonObject Refreshed() => State()["refreshed"] is JsonObject done ? (JsonObject)done.DeepClone() : [];

    private void SaveRefreshed(JsonObject done) => SaveState("refreshed", done);

    /// <summary>When the prep was written: the earlier of its creation and modification times (on Windows st_ctime is
    /// creation, so a transcript update since doesn't move "since").</summary>
    private static DateTimeOffset WrittenAt(string prep)
    {
        var created = File.GetCreationTimeUtc(prep);
        var modified = File.GetLastWriteTimeUtc(prep);
        var t = created < modified ? created : modified;
        return new DateTimeOffset(t.Ticks - t.Ticks % 10, TimeSpan.Zero);
    }

    public int Refresh(bool dryRun = false)
    {
        var now = Clock.Now();
        var done = Refreshed();
        var failures = 0;
        foreach (var plan in Upcoming(now))
        {
            var start = PyTime.FromIso(plan.Start);
            if (plan.Series is not null || start - now > TimeSpan.FromMinutes(Config.RefreshWithinMinutes))
                continue;
            var prep = Target(plan);
            var key = $"{plan.File}@{plan.Start}";
            if (!Exists(prep) || done.ContainsKey(key))
                continue;
            var prepName = Path.GetFileName(prep);
            var writtenAt = WrittenAt(prep);
            var written = Clock.ToLocal(writtenAt);
            if (dryRun)
            {
                log($"refresh: would check {prepName} for messages since {F(written, "%a %H:%M")}");
                continue;
            }
            var gaps = new OrderedDictionary<string, string>(StringComparer.Ordinal);
            var mail = Read("email", () => Context.Mail(graph, Config, plan.With, Clock, sinceTime: writtenAt, log: log), gaps);
            var teams = Read("Teams", () => Context.Teams(graph, plan.With, Clock, sinceTime: writtenAt), gaps);
            if (gaps.Count > 0)
            {
                // not marked done: the next run tries again, while the meeting is still within the hour
                log($"refresh: {prepName} waits, {PyMore.AndList(gaps.Keys.ToList())} unavailable");
                continue;
            }
            done[key] = PyTime.Iso(now, "minutes");
            if (mail.StartsWith("(none", StringComparison.Ordinal) && teams.StartsWith("(none", StringComparison.Ordinal))
            {
                log($"refresh: nothing new for {prepName} since {F(written, "%H:%M")}");
                SaveRefreshed(done);
                continue;
            }
            log($"refresh: checking {prepName} against messages since {F(written, "%H:%M")}");
            var kind = Config.Kinds[plan.Kind!];
            try
            {
                using (Lock(plan.File!))
                {
                    var block = PyStr.Strip(Claude(PyMore.Format(PromptText.Refresh, new Dictionary<string, string>
                    {
                        ["owner"] = Config.Owner, ["prep"] = Config.Relative(prep), ["subject"] = plan.Subject, ["when"] = When(plan),
                        ["now"] = F(now, "%A %d %B %Y %H:%M"), ["written"] = F(written, "%A %d %B %H:%M"), ["name"] = plan.With!,
                        ["hhmm"] = F(Clock.Now(), "%H:%M"), ["update_rules"] = kind.UpdateRules.Length > 0 ? " " + kind.UpdateRules : "",
                        ["house_rules"] = House(Config.HouseRules), ["mail"] = mail, ["teams"] = teams,
                    }), 1800));
                    if (!block.StartsWith("> **Update", StringComparison.Ordinal))
                    {
                        log($"  nothing for {prepName}" + (block.StartsWith("NO UPDATE", StringComparison.Ordinal) ? "" : $": {PyStr.Head(block, 120)}"));
                        SaveRefreshed(done);
                        continue;
                    }
                    AddBlock(prep, block);
                    log($"  added an update to {prepName}");
                }
            }
            catch (LimitReachedException limit)
            {
                Limited(["refresh"], limit);
                return 0;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures++;
                done.Remove(key);   // try again on the next run
                log($"  FAILED refresh of {prepName}: {PyMore.ErrorText(error)}");
                continue;
            }
            SaveRefreshed(done);
            AfterWrite(Report("refresh", plan.Subject, plan.Start, [Config.Relative(prep)], "email and Teams"));
        }
        return failures > 0 ? 1 : 0;
    }

    /// <summary>One prep now, whatever the time: the meeting on day with a person, or a series by subject. With outDir,
    /// it's written there instead, and after_write isn't run (a trial: nothing committed or queued).</summary>
    public List<string> WriteOne(DateOnly day, string who, string? outDir = null)
    {
        var match = Plans(day, 1).FirstOrDefault(p => p.With == who || p.Series == who)
                    ?? throw new NoSuchMeetingException($"No meeting on {PyTime.Iso(day)} with {Py.ReprString(who)} that gets a prep.");
        List<string> written;
        using (Lock(match.File!, 60))
            written = Write(match, outDir);
        if (outDir is null)
            AfterWrite(Report("prep", match.Subject, match.Start, written.Select(Config.Relative)));
        return written;
    }

    /// <summary>The prompt one prep would be written from, with nothing written and Claude not run: for comparing the
    /// two versions on the real calendar (a .NET-only command).</summary>
    public string PromptFor(DateOnly day, string who)
    {
        var match = Plans(day, 1).FirstOrDefault(p => p.With == who || p.Series == who)
                    ?? throw new NoSuchMeetingException($"No meeting on {PyTime.Iso(day)} with {Py.ReprString(who)} that gets a prep.");
        return PromptOf(match, Clock.Now()).Prompt;
    }
}

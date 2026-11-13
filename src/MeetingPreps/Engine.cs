using System.Globalization;
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

/// <summary>Writing preps, and keeping them current, as the Python's engine.py.
///
/// **run** writes every prep that's due and not yet written (so a run missed while the laptop slept is caught up by
/// the next), then does the pre-meeting refresh. **update** checks preps for meetings still to come (to the end of
/// the next working day) against transcripts that have just landed, and adds a dated "Update HH:MM" block to each
/// one they affect. **refresh**: once per 1:1, in the hour before it, email and Teams with that person since the
/// prep was written are checked. **Claude runs headless in the workspace with read-only tools;** this writes the
/// files. after_write is given a JSON report for every file written.</summary>
public sealed class Engine(Config config, IGraph graph, Action<string> log, PrepsHost host)
{
    private static readonly PyRegex Fence = PyRegex.Get(@"^```(?:markdown|md)?\s*\n|\n```\s*$");
    private static readonly PyRegex BlockAt = PyRegex.Get(@"(?m)^(> \*\*Update |## )");
    // re.findall with a group. PyCompat's PyRegex has no finditer yet, so the pattern is translated by PyRegex and
    // run as a .NET Regex, with the options PyRegex would give it (interim, until the library has one).
    private static readonly Regex Speaker = Translated(@"(?m)^\*\*(.+?)\*\* \d");
    private static readonly PyRegex Marks = PyRegex.Get("[#*_>|-]");

    public Config Config { get; } = config;
    private Clock Clock => host.Clock;

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
    /// MEETING_PREPS_SUBJECT and MEETING_PREPS_WITH (empty for a series).</summary>
    public string ContextCommands(Kind kind, PlanRow plan)
    {
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
                    log($"  context command {label} exited {done.ExitCode}: {PyStr.Head(PyStr.Strip(done.StandardError), 200)}");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log($"  context command {label} failed: {PyStr.Head(PyMore.ErrorText(error), 200)}");
            }
        }
        return string.Join("\n", output);
    }

    public List<PlanRow> Plans(DateOnly start, int days) =>
        Planner.PlanAll(Config, Planner.Events(graph, Config, start, days), start, days).Where(p => p.Decision == "prep").ToList();

    /// <summary>Preps for meetings from now to the end of the next working day.</summary>
    public List<PlanRow> Upcoming(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var end = Planner.NextWorkingDay(today);
        return Plans(today, end.DayNumber - today.DayNumber + 1).Where(p => PyTime.FromIso(p.Start) > now).ToList();
    }

    public static string When(PlanRow plan)
    {
        var start = PyTime.FromIso(plan.Start);
        return $"{F(start, "%A %d %B %Y")}, {F(start, "%H:%M")}–{plan.End}";
    }

    // ---- writing ------------------------------------------------------------------------------------------

    /// <summary>The prompt for one prep, gathering its context (Graph, context commands) as the Python did, in the
    /// same order.</summary>
    public string PrepPrompt(PlanRow plan, DateTime now)
    {
        var kind = Config.Kinds[plan.Kind!];
        string name, kindText, teams, mail, shortName;
        if (plan.Series is not null)
        {
            var series = Config.Series(plan.Series)!;
            name = $"the {plan.Subject} group";
            teams = Context.Teams(graph, null, Clock, topic: series.Chat, log: log);
            kindText = Fill(kind.Describe, ("name", plan.Subject), ("slug", ""));
            if (plan.Absorbed is { } absorbed)
                kindText += $" (with {absorbed.Subject} at {absorbed.Start[11..]}–{absorbed.End} folded in)";
            if (plan.NotAttending is { Length: > 0 } notAttending)
                kindText += $". {Config.Owner} isn't attending this one ({notAttending}): write it as what they "
                            + "might raise or pass on through someone who is there, short";
            mail = Context.Mail(graph, Config, null, Clock, days: 14, log: log);
            shortName = plan.Subject;
        }
        else
        {
            var person = Config.Person(plan.With!)!;
            name = plan.With!;
            kindText = Fill(kind.Describe, ("name", name), ("slug", person.Slug));
            teams = Context.Teams(graph, name, Clock, log: log);
            mail = Context.Mail(graph, Config, name, Clock, days: 14, log: log);
            shortName = PyStr.Split(name)[0];
        }
        var extra = ContextCommands(kind, plan);
        var rules = Fill(kind.Rules, ("name", name), ("slug", Config.Person(plan.With ?? "")?.Slug ?? ""));
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
        };
        values["diary"] = Context.Diary(graph, Config, DateOnly.FromDateTime(now));
        values["teams"] = teams;
        values["mail"] = mail;
        return PyMore.Format(PromptText.Prep, values);
    }

    /// <summary>prompts.house(): the house rules, introduced, or nothing.</summary>
    public static string House(string rules) =>
        PyStr.Strip(rules).Length > 0 ? $"\nHouse rules, which win where they differ from the above:\n{PyStr.Strip(rules)}\n" : "";

    /// <summary>Write one prep, and its companion if its kind has one. Returns the files written.</summary>
    public List<string> Write(PlanRow plan)
    {
        var now = Clock.Now();
        var prompt = PrepPrompt(plan, now);
        log($"writing {plan.File}");
        var text = Claude(prompt, 3600);
        var cut = text.IndexOf("===COMPANION===", StringComparison.Ordinal);
        var prepText = cut >= 0 ? text[..cut] : text;
        var companionText = cut >= 0 ? text[(cut + "===COMPANION===".Length)..] : "";
        var draft = prepText.IndexOf("# [DRAFT]", StringComparison.Ordinal);
        prepText = draft >= 0 ? PyStr.Strip(prepText[draft..]) : "";
        if (prepText.Length == 0)
            throw new InvalidOperationException("the reply wasn't a prep: " + PyStr.Head(text, 200));
        var written = new List<string>();
        var path = OutputPath(plan.File!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PyStr.WriteText(path, prepText + "\n");
        written.Add(path);
        if (plan.Companion is { Length: > 0 })
        {
            companionText = PyStr.Strip(companionText);
            var at = companionText.IndexOf("# [DRAFT]", StringComparison.Ordinal);
            if (at >= 0)
            {
                var second = OutputPath(plan.Companion);
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
        var due = Upcoming(now).Where(p => PyTime.FromIso(p.Due!) <= now && !Exists(OutputPath(p.File!))).ToList();
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
                    if (Exists(OutputPath(plan.File!)))
                        continue;
                    written = Write(plan);
                }
            }
            catch (LimitReachedException limit)
            {
                Limited(["run"], limit);
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
                failures++;
                log($"  FAILED {plan.File}: {PyMore.ErrorText(error)}");
                host.Notify("Prep failed", [plan.Subject, $"Log: {Config.LogFile}"]);
                continue;
            }
            AfterWrite(Report("prep", plan.Subject, plan.Start, written.Select(Config.Relative)));
        }
        failures += Refresh(dryRun);
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
            var prep = OutputPath(plan.File!);
            var kind = Config.Kinds[plan.Kind!];
            if (!Exists(prep) || (!kind.EveryTranscript && !voices.Overlaps(NamesFor(plan))))
                continue;
            var prepName = Path.GetFileName(prep);
            log($"update: checking {prepName} against {source}");
            try
            {
                using (Lock(plan.File!))
                {
                    var block = PyStr.Strip(Claude(PyMore.Format(PromptText.Update, new Dictionary<string, string>
                    {
                        ["owner"] = Config.Owner, ["prep"] = Config.Relative(prep), ["subject"] = plan.Subject, ["when"] = When(plan),
                        ["now"] = F(now, "%A %d %B %Y %H:%M"), ["transcripts"] = string.Join(", ", transcripts.Select(Config.Relative)),
                        ["hhmm"] = F(Clock.Now(), "%H:%M"), ["source"] = source,
                        ["update_rules"] = kind.UpdateRules.Length > 0 ? " " + kind.UpdateRules : "",
                        ["house_rules"] = House(Config.HouseRules),
                    }), 1800));
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

    private JsonObject Refreshed()
    {
        try
        {
            return PyJson.Parse(PyStr.ReadText(Config.StateFile)) is JsonObject state && state["refreshed"] is JsonObject done
                ? (JsonObject)done.DeepClone()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                          or System.Text.DecoderFallbackException)
        {
            return [];
        }
    }

    private void SaveRefreshed(JsonObject done)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config.StateFile)!);
        PyStr.WriteText(Config.StateFile, PyJson.Dump(new JsonObject { ["refreshed"] = done.DeepClone() }, 2));
    }

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
            var prep = OutputPath(plan.File!);
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
            var mail = Context.Mail(graph, Config, plan.With, Clock, sinceTime: writtenAt, log: log);
            var teams = Context.Teams(graph, plan.With, Clock, sinceTime: writtenAt, log: log);
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

    /// <summary>One prep now, whatever the time: the meeting on day with a person, or a series by subject.</summary>
    public List<string> WriteOne(DateOnly day, string who)
    {
        var match = Plans(day, 1).FirstOrDefault(p => p.With == who || p.Series == who)
                    ?? throw new NoSuchMeetingException($"No meeting on {PyTime.Iso(day)} with {Py.ReprString(who)} that gets a prep.");
        List<string> written;
        using (Lock(match.File!, 60))
            written = Write(match);
        AfterWrite(Report("prep", match.Subject, match.Start, written.Select(Config.Relative)));
        return written;
    }

    /// <summary>The prompt one prep would be written from, with nothing written and Claude not run: for comparing the
    /// two versions on the real calendar (a .NET-only command).</summary>
    public string PromptFor(DateOnly day, string who)
    {
        var match = Plans(day, 1).FirstOrDefault(p => p.With == who || p.Series == who)
                    ?? throw new NoSuchMeetingException($"No meeting on {PyTime.Iso(day)} with {Py.ReprString(who)} that gets a prep.");
        return PrepPrompt(match, Clock.Now());
    }
}

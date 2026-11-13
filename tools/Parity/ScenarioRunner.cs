using System.Text.Json.Nodes;
using System.Web;
using ClaudeHeadless;
using MeetingPreps.Interim;
using PyCompat;
using WinBackground;

namespace MeetingPreps.Parity;

/// <summary>The .NET side of tests/parity/oracle.py: the same scenario, the same fakes, the same record. The two
/// records are compared whole.</summary>
public static class ScenarioRunner
{
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    private sealed class FixtureMissing(string message) : Exception(message);

    private sealed class FakeGraph(JsonObject fx, JsonArray record) : IGraph
    {
        public JsonObject Get(string url)
        {
            record.Add((JsonNode)new JsonArray("GET", url, new JsonObject()));
            if (fx["get"] is JsonObject gets && gets[url] is JsonObject found)
                return (JsonObject)found.DeepClone();
            const string folderPrefix = "/me/mailFolders/", folderSuffix = "?$select=displayName";
            if (url.StartsWith(folderPrefix, StringComparison.Ordinal) && url.EndsWith(folderSuffix, StringComparison.Ordinal)
                && fx["folders"] is JsonObject folders)
            {
                var id = url[folderPrefix.Length..^folderSuffix.Length];
                if (folders[id] is { } name)
                    return new JsonObject { ["displayName"] = name.DeepClone() };
            }
            if (url == "/me/chats?$expand=members&$top=50" && fx["chats"] is JsonArray chats)
                return new JsonObject { ["value"] = chats.DeepClone() };
            if (url.StartsWith("/chats/", StringComparison.Ordinal) && fx["chat_messages"] is JsonObject messages)
            {
                var chat = url.Split('/')[2];
                if (messages[chat] is JsonArray list)
                    return new JsonObject { ["value"] = list.DeepClone() };
            }
            throw new FixtureMissing($"no fixture: {url}");
        }

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null)
        {
            var h = new JsonObject();
            foreach (var (k, v) in headers ?? new Dictionary<string, string>())
                h[k] = v;
            record.Add((JsonNode)new JsonArray("PAGES", url, h));
            if (url.StartsWith("/me/calendarView?", StringComparison.Ordinal) && fx["calendar"] is JsonArray calendar)
            {
                var query = HttpUtility.ParseQueryString(url.Split('?', 2)[1]);
                var start = query["startDateTime"]!;
                var end = query["endDateTime"]!;
                foreach (var e in calendar)
                {
                    var s = PyStr.Head((string)e!["start"]!["dateTime"]!, 19);
                    var f = PyStr.Head((string)e["end"]!["dateTime"]!, 19);
                    if (string.CompareOrdinal(s, end) < 0 && string.CompareOrdinal(f, start) > 0)
                        yield return e.DeepClone();
                }
                yield break;
            }
            if (url.StartsWith("/me/messages?", StringComparison.Ordinal) && fx["inbox"] is JsonArray inbox)
            {
                foreach (var m in inbox)
                    yield return m!.DeepClone();
                yield break;
            }
            if (url.StartsWith("/me/mailFolders/sentitems/messages?", StringComparison.Ordinal) && fx["sent"] is JsonArray sent)
            {
                foreach (var m in sent)
                    yield return m!.DeepClone();
                yield break;
            }
            throw new FixtureMissing($"no fixture: {url}");
        }
    }

    private sealed class FakeClaude(List<JsonNode?> replies, JsonArray record) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout)
        {
            record.Add((JsonNode)new JsonObject
            {
                ["command"] = new JsonArray(command.Select(c => (JsonNode)c).ToArray()), ["prompt"] = prompt, ["cwd"] = cwd,
                ["timeout"] = (int)timeout.TotalSeconds,
            });
            if (replies.Count == 0)
                throw new ClaudeRunException("no more replies");
            var reply = replies[0];
            replies.RemoveAt(0);
            if (reply is JsonObject o && o["limit"] is { } limit)
                throw new LimitReachedException(PyNaive.Aware(PyNaive.FromIso((string)limit!), London), "limit");
            if (reply is JsonObject e && e["error"] is { } error)
                throw new ClaudeRunException((string)error!);
            return new JsonObject { ["result"] = reply!.DeepClone(), ["num_turns"] = 2, ["session_id"] = "sess-1" };
        }
    }

    private sealed class FakeProcesses(JsonObject responses, JsonArray record) : IProcessRunner
    {
        public Completed Run(ProcessRequest request)
        {
            JsonObject? env = null;
            if (request.ExtraEnv.Count > 0)
            {
                env = [];
                foreach (var (k, v) in request.ExtraEnv)
                    env[k] = v;
            }
            record.Add((JsonNode)new JsonObject
            {
                ["argv"] = new JsonArray(request.Argv.Select(a => (JsonNode)a).ToArray()), ["cwd"] = request.Cwd, ["stdin"] = request.Stdin,
                ["timeout"] = (int)request.Timeout.TotalSeconds, ["env"] = env,
            });
            var response = responses[string.Join(' ', request.Argv)] as JsonObject ?? [];
            if (response["raise"] is { } raise)
                throw new IOException((string)raise!);
            return new Completed(response["exit"] is { } exit ? (int)exit : 0, (string?)response["stdout"] ?? "", (string?)response["stderr"] ?? "");
        }
    }

    private static string ErrorKind(Exception error) => error switch
    {
        MissingFieldException => "missing-field",
        NoSuchMeetingException => "no-meeting",
        FileNotFoundException => "no-settings",
        SettingsException => "settings",
        _ => "other",
    };

    public static JsonObject Run(JsonObject scenario)
    {
        var tmpGiven = Path.Combine(Path.GetTempPath(), "mp-net-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpGiven);
        var tmp = RealPath.Resolve(tmpGiven);
        var record = new JsonObject
        {
            ["ops"] = new JsonArray(), ["prompts"] = new JsonArray(), ["graph"] = new JsonArray(), ["processes"] = new JsonArray(),
            ["log"] = new JsonArray(), ["notify"] = new JsonArray(), ["retries"] = new JsonArray(),
        };
        var replies = (scenario["claude"] as JsonArray ?? []).Select(r => r?.DeepClone()).ToList();
        var now = PyNaive.FromIso((string?)scenario["now"] ?? "2026-11-11T08:00:00");
        var clock = Clock.Fixed(now, London);
        var logList = (JsonArray)record["log"]!;
        void Log(string text) => logList.Add((JsonNode)text);
        var host = new PrepsHost
        {
            Clock = clock,
            Claude = new FakeClaude(replies, (JsonArray)record["prompts"]!),
            Retry = (argv, cwd, reset) =>
            {
                ((JsonArray)record["retries"]!).Add((JsonNode)new JsonObject
                {
                    ["argv"] = new JsonArray(argv.Skip(1).Select(a => (JsonNode)a).ToArray()), ["cwd"] = cwd,
                    ["reset"] = PyNaive.Strftime(reset.DateTime, "%Y-%m-%dT%H:%M"),
                });
                return true;
            },
            Notify = (title, lines) => ((JsonArray)record["notify"]!).Add((JsonNode)new JsonArray(title, new JsonArray(lines.Select(l => (JsonNode)l).ToArray()))),
            Processes = new FakeProcesses(scenario["processes"] as JsonObject ?? [], (JsonArray)record["processes"]!),
            LockPrefix = "mp-test-",
            Exe = @"C:\fake\meeting-preps.exe",
        };
        try
        {
            foreach (var (name, text) in scenario["files"] as JsonObject ?? [])
            {
                var path = Path.Combine(tmp, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes((string)text!));
            }
            foreach (var (name, when) in scenario["mtimes"] as JsonObject ?? [])
                File.SetLastWriteTimeUtc(Path.Combine(tmp, name), PyNaive.Aware(PyNaive.FromIso((string)when!), London).UtcDateTime);
            File.WriteAllBytes(Path.Combine(tmp, "config.toml"), new System.Text.UTF8Encoding(false).GetBytes((string)scenario["settings"]!));
            var graph = new FakeGraph(scenario["graph"] as JsonObject ?? [], (JsonArray)record["graph"]!);
            Config? config = null;
            Config LoadConfig() => Config.Load(Path.Combine(tmp, "config.toml"));
            foreach (var opNode in scenario["ops"] as JsonArray ?? [])
            {
                var op = (JsonObject)opNode!;
                var kind = (string)op["op"]!;
                var output = new JsonObject { ["op"] = kind };
                try
                {
                    if (kind == "load")
                    {
                        config = LoadConfig();
                        output["result"] = Describe(config);
                    }
                    else if (kind == "hook_paths")
                        output["result"] = new JsonArray(Cli.HookPaths((string)op["raw"]!).Select(p => (JsonNode)p).ToArray());
                    else
                    {
                        config ??= LoadConfig();
                        var engine = new Engine(config, graph, Log, host);
                        switch (kind)
                        {
                            case "plan":
                                var start = DateOnly.ParseExact((string)op["start"]!, "yyyy-MM-dd");
                                var days = (int)op["days"]!;
                                var plan = Planner.PlanAll(config, Planner.Events(graph, config, start, days), start, days);
                                output["result"] = new JsonObject { ["json"] = Planner.Json(plan), ["show"] = Planner.Show(plan, start, days) };
                                break;
                            case "run":
                                output["result"] = engine.Run((bool?)op["dry_run"] ?? false);
                                break;
                            case "write_one":
                                output["result"] = new JsonArray(engine.WriteOne(DateOnly.ParseExact((string)op["day"]!, "yyyy-MM-dd"), (string)op["who"]!)
                                    .Select(p => (JsonNode)p).ToArray());
                                break;
                            case "update":
                                output["result"] = engine.Update(((JsonArray)op["paths"]!).Select(p => PyPath.Pure(Path.Combine(tmp, (string)p!))));
                                break;
                            case "refresh":
                                output["result"] = engine.Refresh((bool?)op["dry_run"] ?? false);
                                break;
                            case "context":
                                var name = (string?)op["name"];
                                output["result"] = new JsonObject
                                {
                                    ["mail"] = Context.Mail(graph, config, name, clock, log: Log),
                                    ["teams"] = Context.Teams(graph, name, clock, topic: (string?)op["topic"], log: Log),
                                    ["diary"] = Context.Diary(graph, config, DateOnly.FromDateTime(now)),
                                };
                                break;
                            default:
                                throw new InvalidOperationException($"unknown op {kind}");
                        }
                    }
                }
                catch (Exception error)
                {
                    output["error"] = ErrorKind(error);
                    output["message"] = Environment.GetEnvironmentVariable("MP_PARITY_TRACE") is { Length: > 0 } ? error.ToString() : error.Message;
                }
                ((JsonArray)record["ops"]!).Add((JsonNode)output);
            }
            var files = new JsonObject();
            foreach (var path in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                files[Path.GetRelativePath(tmp, path).Replace('\\', '/')] = new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(path));
            record["files"] = files;
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch (IOException)
            {
            }
        }
        return (JsonObject)Normalise(record, tmp)!;
    }

    private static JsonObject Describe(Config c)
    {
        JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());
        var kinds = new JsonObject();
        foreach (var (name, k) in c.Kinds)
            kinds[name] = new JsonObject
            {
                ["describe"] = k.Describe, ["rules"] = k.Rules, ["companion"] = k.Companion, ["companion_rules"] = k.CompanionRules,
                ["every_transcript"] = k.EveryTranscript, ["update_rules"] = k.UpdateRules,
                ["context_commands"] = new JsonArray(k.ContextCommands.Select(cmd => (JsonNode)Strings(cmd)).ToArray()),
            };
        var aliases = new JsonObject();
        foreach (var (k, v) in c.Aliases)
            aliases[k] = v;
        return new JsonObject
        {
            ["me"] = c.Me, ["email"] = c.Email, ["workspace"] = c.Workspace, ["output"] = c.Output, ["history"] = c.History,
            ["owner"] = c.Owner, ["model"] = c.Model, ["house_rules"] = c.HouseRules, ["after_write"] = Strings(c.AfterWrite),
            ["first_thing_before"] = c.FirstThingBefore, ["day_before_at"] = c.DayBeforeAt, ["on_the_day_at"] = c.OnTheDayAt,
            ["refresh_within_minutes"] = c.RefreshWithinMinutes, ["leave_pattern"] = c.LeavePattern,
            ["mail_skip_folders"] = Strings(c.MailSkipFolders), ["noise_senders"] = Strings(c.NoiseSenders),
            ["noise_rules"] = c.NoiseRules, ["org_domain"] = c.OrgDomain, ["login_hint"] = c.LoginHint, ["tenant"] = c.Tenant,
            ["msal_cache"] = c.MsalCache, ["timezone"] = c.TimeZone, ["scopes"] = Strings(c.Scopes),
            ["watch"] = new JsonArray(c.WatchEveryMinutes, Strings([c.WatchHours.Start, c.WatchHours.End]), c.WatchWeekdays),
            ["log_file"] = c.LogFile, ["state_file"] = c.StateFile, ["aliases"] = aliases, ["kinds"] = kinds,
            ["people"] = Strings(c.PeopleInOrder.Select(p => p.Key)), ["series"] = Strings(c.SeriesInOrder.Select(p => p.Key)),
        };
    }

    private static JsonNode? Normalise(JsonNode? node, string tmp)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Replace(tmp, "<TMP>").Replace(tmp.Replace('\\', '/'), "<TMP>");
                foreach (var name in new[] { "LOCALAPPDATA", "APPDATA", "USERPROFILE" })   // never a real user's folder in a golden
                    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                        s = s.Replace(value, $"<{name}>");
                return s;
            case JsonArray a:
                return new JsonArray(a.Select(x => Normalise(x, tmp)).ToArray());
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                    copy[(string)Normalise(JsonValue.Create(k), tmp)!] = Normalise(v, tmp);
                return copy;
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>The first place two records differ, as a path and the two values; null if they're the same.</summary>
    public static string? Difference(JsonNode? expected, JsonNode? actual, string path = "$", bool looseMessage = false)
    {
        switch (expected)
        {
            case JsonObject e when actual is JsonObject a:
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (looseMessage && key == "message")
                        continue;
                    if (!e.ContainsKey(key) || !a.ContainsKey(key))
                        return $"{path}.{key}: only in {(e.ContainsKey(key) ? "Python" : ".NET")}";
                    if (Difference(e[key], a[key], $"{path}.{key}", looseMessage) is { } d)
                        return d;
                }
                return null;
            case JsonArray e when actual is JsonArray a:
                for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
                {
                    if (i >= e.Count || i >= a.Count)
                        return $"{path}: Python has {e.Count} items, .NET {a.Count}; first extra: {(i < e.Count ? e[i] : a[i])?.ToJsonString()}";
                    if (Difference(e[i], a[i], $"{path}[{i}]", looseMessage) is { } d)
                        return d;
                }
                return null;
            default:
                if (JsonNode.DeepEquals(expected, actual))
                    return null;
                if (expected is JsonValue ev && actual is JsonValue av && ev.TryGetValue<string>(out var es) && av.TryGetValue<string>(out var @as))
                {
                    var at = 0;
                    while (at < es.Length && at < @as.Length && es[at] == @as[at])
                        at++;
                    string Around(string s) => s.Substring(Math.Max(0, at - 60), Math.Min(s.Length - Math.Max(0, at - 60), 160));
                    return $"{path}: differ at {at}\n  Python: {JsonValue.Create(Around(es))!.ToJsonString()}\n  .NET:   {JsonValue.Create(Around(@as))!.ToJsonString()}";
                }
                return $"{path}: Python {expected?.ToJsonString()} .NET {actual?.ToJsonString()}";
        }
    }
}

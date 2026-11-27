using PyCompat;
using System.Text.Json.Nodes;
using WinBackground;

using MeetingPreps.Parity;

namespace MeetingPreps.Tests;

public class MoreTests
{
    [Fact]
    public void Lock_names_are_the_pythons()
    {
        // win_background.key(), computed by the Python: a .NET write and a Python watcher take the same lock.
        Assert.Equal("4c0d9da5adb94e68", Locks.Key(@"C:\Users\x\tools\meeting-preps.toml"));
        Assert.Equal("fecccc97532467ad", Locks.Key("a.md"));
        using var f = new Fixture();
        Assert.Equal($"mp-test-meeting-preps-{Locks.Key(f.Config.Path)}-fecccc97532467ad", f.Engine().LockName("a.md"));
        Assert.Equal($"mp-test-meeting-preps-watch-{Locks.Key(f.Config.Path)}", Watch.LockName(f.Config, f.Host()));
    }

    [Theory]
    [InlineData("{date}-x.md", "2026-11-11-x.md")]
    [InlineData("{{date}}-{date}", "{date}-2026-11-11")]
    [InlineData("a}}b{{c", "a}b{c")]
    public void Format_is_str_format_with_names(string template, string expected) =>
        Assert.Equal(expected, PyMore.Format(template, new Dictionary<string, string> { ["date"] = "2026-11-11" }));

    [Fact]
    public void Format_refuses_what_it_cant_do_and_says_which_field_is_missing()
    {
        var values = new Dictionary<string, string> { ["date"] = "d" };
        Assert.Equal("'week'", Assert.Throws<MissingFieldException>(() => PyMore.Format("{week}", values)).Message);
        Assert.Throws<UnsupportedFieldException>(() => PyMore.Format("{date:>10}", values));
        Assert.Throws<UnsupportedFieldException>(() => PyMore.Format("{0}", values));
        Assert.Throws<UnsupportedFieldException>(() => PyMore.Format("a } b", values));
    }

    [Fact]
    public void Url_encoding_is_quote_plus() =>
        Assert.Equal("%24select=subject%2Cstart&x=a+b%2Fc%3A%C3%A9~_.-",
            PyMore.UrlEncode([("$select", "subject,start"), ("x", "a b/c:\u00e9~_.-")]));

    [Fact]
    public void The_after_write_report_is_json_dumps()
    {
        Assert.Equal("{\"type\": \"prep\", \"meeting\": \"Zo\\u00eb 1:1\", \"start\": \"2026-11-11T11:00\", \"files\": [\"a.md\", \"b.md\"]}",
            Engine.Report("prep", "Zo\u00eb 1:1", "2026-11-11T11:00", ["a.md", "b.md"]));
        Assert.EndsWith(", \"from\": \"email and Teams\"}", Engine.Report("refresh", "m", "s", [], "email and Teams"));
    }

    [Fact]
    public void Claude_is_run_read_only_with_the_output_folder_added_when_outside()
    {
        using var f = new Fixture(Fixture.Settings.Replace("output = \"preps\"", "output = \"../out\""));
        Assert.Equal(["-p", "--output-format", "json", "--model", "opus", "--tools", "Read,Grep,Glob", "--strict-mcp-config",
            "--permission-mode", "dontAsk", "--add-dir", Path.Combine(f.Dir, "out")], f.Engine().ClaudeCommand());
    }

    [Theory]
    [InlineData("2026-11-11T06:59", false)]
    [InlineData("2026-11-11T07:00", true)]
    [InlineData("2026-11-11T17:59", true)]
    [InlineData("2026-11-11T18:00", false)]
    [InlineData("2026-11-14T10:00", false)]
    public void The_watcher_runs_in_working_hours(string now, bool due)
    {
        using var f = new Fixture();
        Assert.Equal(due, Watch.Due(f.Config, PyTime.FromIso(now)));
    }

    private sealed class Failing(Exception error) : IGraph
    {
        public JsonObject Get(string url) => throw error;
        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) => throw error;
    }

    [Fact]
    public void The_watcher_notifies_after_three_failures_and_a_sign_in_at_once()
    {
        using var f = new Fixture();
        var notified = new List<string>();
        var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        var ticks = 0;
        var errors = new Queue<Exception>([new IOException("a"), new IOException("b"), new IOException("c"), new IOException("d"),
            new M365Graph.SignInNeededException("sign in"), new M365Graph.SignInNeededException("sign in")]);
        var host = new PrepsHost
        {
            Clock = new Clock { UtcNow = () => now, Local = ScenarioRunner.London },
            LockPrefix = "mp-test-" + Guid.NewGuid().ToString("N"),
            Notify = (title, _) => notified.Add(title),
            Graph = (_, interactive) =>
            {
                Assert.False(interactive);
                return new Failing(errors.Dequeue());
            },
            Sleep = _ => now = now.AddSeconds(30),
        };
        // Six runs, 10 ticks (5 minutes) apart.
        Assert.Equal(0, Watch.Run(f.Config, host, () => ticks++ < 6 * 10 - 5));
        Assert.Equal(["meeting-preps couldn't run", "meeting-preps needs a sign-in"], notified);
        var log = File.ReadAllText(f.Config.LogFile);
        Assert.Contains("watching: a run every 5 min, 07:00-18:00 Mon-Fri", log);
        Assert.Contains("run failed: CalendarUnavailable: calendar unavailable (OSError: a), and there's no copy of an earlier read", log);
        Assert.Contains("sign-in needed: sign in", log);
    }

    [Fact]
    public void A_second_watcher_exits_with_3()
    {
        using var f = new Fixture();
        var prefix = "mp-test-" + Guid.NewGuid().ToString("N");
        using var held = Locks.SingleInstance($"{prefix}meeting-preps-watch-{Locks.Key(f.Config.Path)}");
        Assert.NotNull(held);
        Assert.Equal(Watch.AlreadyRunning, Watch.Run(f.Config, new PrepsHost { LockPrefix = prefix, Sleep = _ => { } }, () => false));
        Assert.Contains("another watcher is already running", File.ReadAllText(f.Config.LogFile));
    }

    private sealed class FakeTask : ITask
    {
        public List<string> Calls { get; } = [];
        private bool on;
        public string Name => Autostart.TaskName;

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
        {
            Calls.Add($"{exe} {string.Join(' ', args)} @ {workingDir}");
            on = true;
            return "cmd";
        }

        public string? Fingerprint { get; set; }

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Fingerprint = schedule.Fingerprint;
            return Enable(exe, args, workingDir);
        }

        public bool Disable()
        {
            var was = on;
            on = false;
            return was;
        }

        public bool Stop() => on;
        public bool Start() => on;
        public IReadOnlyDictionary<string, string>? Status() => on
            ? new Dictionary<string, string> { ["state"] = "Running", ["command"] = "c", ["description"] = ScheduleDrift.WithFingerprint("d", Fingerprint ?? "") }
            : null;
    }

    [Fact]
    public void Autostart_status_says_when_the_task_no_longer_matches_the_schedule()
    {
        using var f = new Fixture();
        var task = new FakeTask();
        var output = new StringWriter();
        var host = new PrepsHost { Task = task, Out = output, BackgroundExe = @"C:\p\meeting-preps-background.exe" };
        Autostart.Command("on", f.Config, host);
        Assert.Equal("v1 watcher logon=yes", task.Fingerprint);
        task.Fingerprint = "v1 job every=15 daily=- logon=yes unlock=0";
        Autostart.Command("status", f.Config, host);
        Assert.Contains("settings changed since this task was registered: run `meeting-preps autostart on`", output.ToString());
    }

    [Fact]
    public void Autostart_runs_the_background_exe_with_the_settings()
    {
        using var f = new Fixture();
        var task = new FakeTask();
        var output = new StringWriter();
        var host = new PrepsHost { Task = task, Out = output, BackgroundExe = @"C:\p\meeting-preps-background.exe" };
        foreach (var action in new[] { "status", "on", "status", "off", "off" })
            Autostart.Command(action, f.Config, host);
        Assert.Equal([$@"C:\p\meeting-preps-background.exe --config {f.Config.Path} watch @ {f.Dir}"], task.Calls);
        Assert.Equal(string.Join(Environment.NewLine, [
            "Autostart is off. `meeting-preps autostart on` turns it on.",
            "Autostart is on: 'meeting-preps watch' runs at every logon, and has started now.", "  cmd",
            "Autostart is on ('meeting-preps watch').", "  state        Running", "  last run     ", "  last result  ", "  command      c",
            "Autostart is off.", "Autostart was already off.", ""]), output.ToString());
    }

    private static (int Code, string Out, string Err) Cli(string[] args, Func<Config, bool?, IGraph>? graph = null, Fixture? f = null,
        string? stdin = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = f?.Host() ?? new PrepsHost();
        var h = new PrepsHost
        {
            Out = output, Err = error, Clock = host.Clock, Claude = host.Claude, Processes = host.Processes, Retry = host.Retry,
            Notify = host.Notify, Graph = graph ?? host.Graph, LockPrefix = "mp-test-", Stdin = () => new StringReader(stdin ?? ""),
            Env = name => name == "APPDATA" ? Path.GetTempPath() : null,
        };
        return (MeetingPreps.Cli.Run(args, h), output.ToString(), error.ToString());
    }

    [Fact]
    public void The_command_line_exit_codes()
    {
        Assert.Equal(2, Cli([]).Code);
        Assert.Equal(2, Cli(["nonsense"]).Code);
        var missing = Cli(["--config", @"C:\nowhere\x.toml", "plan"]);
        Assert.Equal(2, missing.Code);
        Assert.Contains("No settings file at", missing.Err);
        Assert.Equal((0, "given.toml" + Environment.NewLine, ""), Cli(["--config", "given.toml", "config"]));
        Assert.Equal(2, Cli(["autostart", "maybe"]).Code);
        Assert.Equal(2, Cli(["plan", "--wat"]).Code);
        Assert.Equal(2, Cli(["write", "2026-11-11"]).Code);
        Assert.Contains("meeting-preps config", Cli(["--help"]).Out);
        Assert.Equal(0, Cli(["--version"]).Code);
    }

    [Fact]
    public void Version_comes_from_the_build()
    {
        var built = typeof(MeetingPreps.Cli).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Assert.Equal(built, MeetingPreps.Cli.Version);
        Assert.Equal(typeof(MeetingPreps.Cli).Assembly.GetName().Version!.ToString(3), built.Split('-', '+')[0]);
        Assert.DoesNotContain("+", built);   // no commit hash appended
        Assert.Equal((0, built + Environment.NewLine, ""), Cli(["--version"]));
    }

    private sealed class Calendar(params JsonObject[] events) : IGraph
    {
        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal) ? new JsonObject { ["value"] = new JsonArray() } : [];

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? events.Select(e => (JsonNode?)e.DeepClone()) : [];
    }

    [Fact]
    public void Plan_shows_five_days_unless_plan_days_or_a_number_says_otherwise()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var calendar = new Calendar(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"));
        IGraph Graph(Config c, bool? i) => calendar;
        Assert.Equal(5, Config.Load(config).PlanDays);
        Assert.Contains("for 5 days", Cli(["--config", config, "plan", "2026-11-11"], Graph, f).Out);

        File.WriteAllText(config, "plan_days = 3\n" + File.ReadAllText(config));
        Assert.Equal(3, Config.Load(config).PlanDays);
        Assert.Contains("for 3 days", Cli(["--config", config, "plan", "2026-11-11"], Graph, f).Out);
        Assert.Contains("for 2 days", Cli(["--config", config, "plan", "2026-11-11", "2"], Graph, f).Out);   // a number given still wins
    }

    [Fact]
    public void Plan_days_must_be_a_sensible_whole_number()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var original = File.ReadAllText(config);
        foreach (var bad in new[] { "0", "367", "\"five\"", "2.5" })
        {
            File.WriteAllText(config, $"plan_days = {bad}\n" + original);
            Assert.Contains("plan_days", Assert.Throws<SettingsException>(() => Config.Load(config)).Message);
        }
    }

    [Fact]
    public void Plan_write_prompt_and_hook_from_the_command_line()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var calendar = new Calendar(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"));
        IGraph Graph(Config c, bool? i) => calendar;
        var plan = Cli(["--config", config, "plan", "2026-11-11", "1"], Graph, f);
        Assert.Equal(0, plan.Code);
        Assert.Contains("PREP (1)", plan.Out);
        Assert.Contains("\"decision\": \"prep\"", Cli(["--config", config, "plan", "2026-11-11", "1", "--json"], Graph, f).Out);
        var prompt = Cli(["--config", config, "prompt", "2026-11-11", "Sam Jones"], Graph, f);
        Assert.Equal(0, prompt.Code);
        Assert.StartsWith("Write Alex's prep for their weekly 1:1 with Sam Jones", prompt.Out);
        Assert.Empty(f.Prompts);   // prompt never runs Claude
        Assert.Equal(2, Cli(["--config", config, "write", "2026-11-11", "Nobody"], Graph, f).Code);
        f.Replies.Add("# [DRAFT] Sam\n");
        Assert.Equal(0, Cli(["--config", config, "write", "2026-11-11", "Sam Jones"], Graph, f).Code);
        Assert.True(File.Exists(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md")));
        Assert.Equal(0, Cli(["--config", config, "hook"], Graph, f, "{\"written\": [\"nothing.md\"]}").Code);
    }

    [Fact]
    public void The_real_process_runner_passes_stdin_and_reads_utf8()
    {
        var done = new ProcessRunner().Run(new ProcessRequest(
            [Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile", "-Command",
             "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $x = [Console]::In.ReadToEnd(); Write-Output ($x + $env:MEETING_PREPS_WITH + [char]0xe9); exit 3"],
            Path.GetTempPath(), "in-", new Dictionary<string, string> { ["MEETING_PREPS_WITH"] = "Sam" }, TimeSpan.FromSeconds(60)));
        Assert.Equal(3, done.ExitCode);
        Assert.Equal("in-Sam\u00e9\n", done.StandardOutput);
    }

    /// <summary>A 1:1 chat with Sam holding the messages given (HTML bodies), each a minute apart.</summary>
    private sealed class Chat(params string[] bodies) : IGraph
    {
        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal)
            ? new JsonObject
            {
                ["value"] = new JsonArray(new JsonObject
                {
                    ["id"] = "c1", ["chatType"] = "oneOnOne", ["topic"] = null,
                    ["members"] = new JsonArray(new JsonObject { ["displayName"] = "Sam Jones" }, new JsonObject { ["displayName"] = "Me" }),
                }),
            }
            : new JsonObject
            {
                ["value"] = new JsonArray(bodies.Select((b, i) => (JsonNode)new JsonObject
                {
                    ["createdDateTime"] = $"2026-11-10T09:{i:00}:00Z", ["messageType"] = "message",
                    ["body"] = new JsonObject { ["content"] = b },
                    ["from"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = "Sam Jones" } },
                }).ToArray()),
            };

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) => [];
    }

    /// <summary>What Context.Teams says each message is, oldest first.</summary>
    private static string[] TeamsLines(params string[] bodies) =>
        Context.Teams(new Chat(bodies), "Sam Jones", Clock.Fixed(new DateTime(2026, 11, 11, 8, 0, 0), ScenarioRunner.London))
            .Split('\n').Select(l => l[(l.IndexOf("Sam Jones: ", StringComparison.Ordinal) + "Sam Jones: ".Length)..]).ToArray();

    [Fact]
    public void A_teams_message_of_600_characters_comes_through_whole()
    {
        var text = string.Concat(Enumerable.Repeat("The rollout moves a week. ", 24)).Trim() + " Agreed.";
        Assert.Equal(631, text.Length);
        Assert.Equal(text, TeamsLines($"<p>{text}</p>").Single());
    }

    [Fact]
    public void A_teams_message_over_2000_characters_is_cut_and_says_so() =>
        Assert.Equal(new string('a', 2000) + " [\u2026cut]", TeamsLines(new string('a', 2500)).Single());

    [Fact]
    public void Teams_entities_are_decoded_and_a_non_breaking_space_is_a_space() =>
        Assert.Equal("It's \"done\" <soon> & dusted ok",
            TeamsLines("<div>It&#39;s &quot;done&quot; &lt;soon&gt; &amp; dusted&nbsp;<b>ok</b></div>").Single());

    [Fact]
    public void Older_teams_messages_fall_back_to_500_once_the_budget_is_spent()
    {
        var text = new string('b', 1999);
        var lines = TeamsLines(Enumerable.Repeat(text, 22).ToArray());   // 22 x 1,999 is over the 40,000 budget
        Assert.Equal(22, lines.Length);
        Assert.All(lines[1..], l => Assert.Equal(text, l));             // the newest 21 start within it, whole
        Assert.Equal(new string('b', 500) + " [\u2026cut]", lines[0]);       // the oldest is cut, and says so
    }
}

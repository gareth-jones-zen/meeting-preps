using System.Text.Json.Nodes;
using ClaudeHeadless;
using PyCompat;

using MeetingPreps.Parity;

namespace MeetingPreps.Tests;

/// <summary>A settings file in a temporary folder, and fakes for everything outside.</summary>
public sealed class Fixture : IDisposable
{
    public const string Settings = """
        me = "Alex Smith"
        email = "alex@example.com"
        org_domain = "example.com"
        workspace = "work"
        output = "preps"
        owner = "Alex"
        house_rules = "house.md"
        state_file = "state.json"
        log_file = "log.txt"

        [kind.report]
        describe = "weekly 1:1 with {name}, who reports to Alex"
        rules = "Read {name}'s notes in team/{slug}/ longitudinally. A brace in prose {like this} is fine."

        [kind.manager]
        describe = "1:1 with {name}, Alex's manager"
        rules = "Read the manager notes."
        companion = "{date}-{slug}-preread.md"
        companion_rules = "200 to 470 words."
        every_transcript = true
        update_rules = "Mark anything for the pre-read 'mention in the room'."

        [kind.leads]
        describe = "the leads meeting"
        rules = "Carry forward open actions."

        [person."Sam Jones"]
        kind = "report"
        slug = "sam-jones"
        file = "{date}-sam-jones-prep.md"

        [person."Pat Boss"]
        kind = "manager"
        slug = "pat-boss"
        file = "{date}-pat-boss-prep.md"
        always_day_before_at = "14:00"

        [series."Leads weekly"]
        kind = "leads"
        file = "{date}-leads-prep.md"
        members = ["Sam Jones", "Kim Lee"]
        weekly = [{day = "Tue", start = "11:00", end = "12:00"}]
        absorbs = "Leads pre-meet"
        due_previous_working_day_at = "15:00"

        [series."Leads pre-meet"]
        kind = "leads"
        file = "{date}-leads-premeet-prep.md"

        [aliases]
        "Sam J" = "Sam Jones"
        """;

    public string Dir { get; }
    public Config Config { get; }
    public List<string> Prompts { get; } = [];
    public List<string> Replies { get; } = [];
    public List<string> Reports { get; } = [];
    public List<string> Log { get; } = [];
    public Clock Clock { get; set; }

    public Fixture(string? settings = null, DateTime? now = null)
    {
        Dir = PyPath.Resolve(Directory.CreateTempSubdirectory("mp-test-").FullName);
        Directory.CreateDirectory(Path.Combine(Dir, "work", "preps"));
        File.WriteAllText(Path.Combine(Dir, "house.md"), "- No code names.");
        File.WriteAllText(Path.Combine(Dir, "config.toml"), (settings ?? Settings).ReplaceLineEndings("\n"));
        Config = Config.Load(Path.Combine(Dir, "config.toml"));
        Clock = Clock.Fixed(now ?? new DateTime(2026, 11, 11, 8, 0, 0), ScenarioRunner.London);
    }

    public sealed class Claude(Fixture f) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout)
        {
            f.Prompts.Add(prompt);
            var reply = f.Replies.Count > 0 ? f.Replies[0] : "NO UPDATE";
            if (f.Replies.Count > 0)
                f.Replies.RemoveAt(0);
            return new JsonObject { ["result"] = reply, ["num_turns"] = 1, ["session_id"] = "s" };
        }
    }

    public sealed class Processes(Fixture f) : IProcessRunner
    {
        public WinBackground.Completed Run(ProcessRequest request)
        {
            f.Reports.Add(request.Stdin ?? "");
            return new WinBackground.Completed(0, "", "");
        }
    }

    public sealed class NoGraph : IGraph
    {
        public JsonObject Get(string url) => throw new InvalidOperationException("no Graph in this test");
        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            throw new InvalidOperationException("no Graph in this test");
    }

    public PrepsHost Host() => new()
    {
        Clock = Clock, Claude = new Claude(this), Processes = new Processes(this), Retry = (_, _, _) => true,
        Notify = (_, _) => { }, LockPrefix = "mp-test-", Out = TextWriter.Null, Err = TextWriter.Null,
    };

    public Engine Engine(IGraph? graph = null) => new(Config, graph ?? new NoGraph(), Log.Add, Host());

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }

    public static JsonObject Event(string subject, string start, string end, params string[] attendees) => new()
    {
        ["subject"] = subject,
        ["start"] = new JsonObject { ["dateTime"] = start + ":00.0000000" },
        ["end"] = new JsonObject { ["dateTime"] = end + ":00.0000000" },
        ["attendees"] = new JsonArray(attendees.Select(n => (JsonNode)new JsonObject
        {
            ["type"] = "required",
            ["emailAddress"] = new JsonObject { ["name"] = n, ["address"] = n.Split(' ')[0].ToLowerInvariant() + "@example.com" },
        }).ToArray()),
        ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Alex Smith", ["address"] = "alex@example.com" } },
        ["showAs"] = "busy",
    };

    public PlanRow Plan(string start = "2026-11-11T11:00", string kind = "report", string? with = "Sam Jones",
        string file = "2026-11-11-sam-jones-prep.md", string? companion = null)
    {
        var row = Planner.Classify(Config, Event("Sam 1:1", start, start[..11] + "23:59", "Sam Jones"));
        row.Kind = kind;
        row.With = with;
        row.File = file;
        row.Previous = null;
        if (companion is not null)
            row.Companion = companion;
        return row;
    }
}

/// <summary>The Python's own tests (tests/test_preps.py), by name.</summary>
public class PythonTests
{
    private const string PrepReply = "Here you go.\n# [DRAFT] Sam Jones 1:1, Wed 11 Nov\n*Written automatically.*\n\n## Items\n- one";

    [Fact]
    public void test_loads()
    {
        using var f = new Fixture();
        Assert.Equal(["leads", "manager", "report"], f.Config.Kinds.Keys.Order().ToArray());
        Assert.Equal(Path.Combine(f.Dir, "work", "preps"), f.Config.Output);
        Assert.Equal(f.Config.Output, f.Config.History);
        Assert.Contains("code names", f.Config.HouseRules);
    }

    [Fact]
    public void test_a_person_with_an_unknown_kind_is_refused()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Dir, "bad.toml"), "me = \"a\"\nemail = \"a@b\"\n[person.\"X\"]\nkind = \"nope\"\nfile = \"{date}.md\"\n");
        Assert.Throws<SettingsException>(() => Config.Load(Path.Combine(f.Dir, "bad.toml")));
    }

    [Fact]
    public void test_one_to_ones_groups_blocks_and_unknowns()
    {
        using var f = new Fixture();
        var room = Fixture.Event("Sam 1:1", "2026-11-11T09:00", "2026-11-11T09:30", "Alex Smith", "Sam J", "MTG Room 3");
        var rows = Planner.PlanAll(f.Config, [
            room,
            Fixture.Event("Pat 1:1", "2026-11-13T10:30", "2026-11-13T11:00", "Pat Boss"),
            Fixture.Event("Chat with Kim", "2026-11-11T13:00", "2026-11-11T13:30", "Kim Lee"),
            Fixture.Event("Big meeting", "2026-11-11T14:00", "2026-11-11T15:00", "Kim Lee", "Sam Jones"),
            Fixture.Event("Focus time", "2026-11-11T15:00", "2026-11-11T16:00"),
        ]).ToDictionary(r => r.Subject);
        var sam = rows["Sam 1:1"];
        Assert.Equal(("prep", "Sam Jones", "2026-11-10T16:00"), (sam.Decision, sam.With, sam.Due));   // before 10:00
        var pat = rows["Pat 1:1"];
        Assert.Equal(("2026-11-12T14:00", "2026-11-13-pat-boss-preread.md"), (pat.Due, pat.Companion));
        Assert.Equal("unsure", rows["Chat with Kim"].Decision);
        Assert.Equal("skip", rows["Big meeting"].Decision);
        Assert.Equal("your own block", rows["Focus time"].Why);
    }

    [Fact]
    public void test_series_declined_missing_absorbed_and_leave()
    {
        using var f = new Fixture();
        var start = new DateOnly(2026, 11, 9);   // Monday
        var declined = Fixture.Event("Leads weekly", "2026-11-17T11:00", "2026-11-17T12:00", "Sam Jones", "Kim Lee");
        declined["showAs"] = "free";
        var holiday = Fixture.Event("Holiday", "2026-11-20T00:00", "2026-11-21T00:00");
        holiday["isAllDay"] = true;
        var plan = Planner.PlanAll(f.Config, [
            Fixture.Event("Leads pre-meet", "2026-11-10T10:00", "2026-11-10T10:30", "Sam Jones", "Kim Lee"),
            declined, holiday, Fixture.Event("Sam 1:1", "2026-11-20T11:00", "2026-11-20T11:30", "Sam Jones"),
        ], start, 14);
        var tuesday = plan.First(p => p.Subject == "Leads weekly" && p.Start.StartsWith("2026-11-10"));
        Assert.Contains("not in your calendar", tuesday.NotAttending);   // put back from its weekly slot
        Assert.Equal("2026-11-09T15:00", tuesday.Due);
        Assert.Equal("Leads pre-meet", tuesday.Absorbed!.Value.Subject);
        Assert.Equal("skip", plan.First(p => p.Subject == "Leads pre-meet").Decision);
        var later = plan.First(p => p.Subject == "Leads weekly" && p.Start.StartsWith("2026-11-17"));
        Assert.Equal(("prep", "declined"), (later.Decision, later.NotAttending));
        Assert.Equal("you're on leave", plan.First(p => p.Subject == "Sam 1:1").Why);
    }

    [Fact]
    public void test_previous_prep_found_in_the_archive()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md"), "x");
        var old = Path.Combine(f.Config.Output, "archive", "2026-10", "2026-10-28-sam-jones-prep.md");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "x");
        var row = Planner.Classify(f.Config, Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"));
        Assert.Equal("preps/archive/2026-10/2026-10-28-sam-jones-prep.md", row.Previous);
    }

    [Fact]
    public void test_a_reply_keeps_only_the_new_part_and_a_forward_keeps_everything()
    {
        var reply = string.Concat(Enumerable.Repeat("Thanks, agreed: we go Monday and I'll tell the supplier.\n", 6)) + "\nFrom: Sam\nSent: Mon\nold stuff";
        Assert.DoesNotContain("old stuff", Context.FullText(reply, "Thanks"));
        var forward = "See below.\n\nFrom: Kim\nSent: Mon\n" + string.Concat(Enumerable.Repeat("The real substance of the forward. ", 10));
        Assert.Contains("real substance", Context.FullText(forward, "See below"));
        Assert.Equal("", Context.FullText("Short", "Short"));
    }

    private sealed class FixedContext : IGraph
    {
        // Diary, Teams and mail all come back empty: "(nothing)" and "(none)", as the Python test patched them.
        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal) ? new JsonObject { ["value"] = new JsonArray() } : [];
        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) => [];
    }

    [Fact]
    public void test_writes_the_prep_from_the_draft_line_on()
    {
        using var f = new Fixture();
        f.Replies.Add(PrepReply);
        var engine = new Engine(f.Config, new FixedContext(), f.Log.Add, f.Host());
        var written = engine.Write(f.Plan());
        Assert.StartsWith("# [DRAFT] Sam Jones 1:1", File.ReadAllText(written[0]));
        var prompt = f.Prompts[0];
        Assert.Contains("team/sam-jones/", prompt);
        Assert.Contains("{like this}", prompt);
        Assert.Contains("No code names", prompt);
        Assert.Contains("\"[Sam 1:1]\"", prompt);
        Assert.Contains("Diary:\n(nothing)", prompt);
    }

    [Fact]
    public void test_companion_is_split_out()
    {
        using var f = new Fixture();
        f.Replies.Add(PrepReply + "\n===COMPANION===\n# [DRAFT] Pre-read\nWords.");
        var engine = new Engine(f.Config, new FixedContext(), f.Log.Add, f.Host());
        var written = engine.Write(f.Plan(kind: "manager", with: "Pat Boss", file: "p.md", companion: "p-preread.md"));
        Assert.Equal(["p.md", "p-preread.md"], written.Select(Path.GetFileName).ToArray());
        Assert.DoesNotContain("COMPANION", File.ReadAllText(written[0]));
    }

    [Fact]
    public void test_update_blocks_go_above_the_first_heading_newest_first()
    {
        using var f = new Fixture();
        var prep = Path.Combine(f.Config.Output, "x.md");
        File.WriteAllText(prep, "# [DRAFT] X\n*auto*\n\n## Items\n- one\n");
        Engine.AddBlock(prep, "> **Update 10:00, from a** first");
        Engine.AddBlock(prep, "> **Update 11:00, from b** second");
        var text = File.ReadAllText(prep);
        Assert.True(text.IndexOf("Update 11:00") < text.IndexOf("Update 10:00") && text.IndexOf("Update 10:00") < text.IndexOf("## Items"));
        Assert.Contains("\r\n", text);   // written as Python's text mode wrote it on Windows
    }

    private sealed class OneMeeting(JsonObject e) : IGraph
    {
        public JsonObject Get(string url) => [];
        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? [e.DeepClone()] : [];
    }

    [Fact]
    public void test_updates_only_from_transcripts_the_person_spoke_in()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        File.WriteAllText(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md"), "# [DRAFT] Sam\n\n## Items\n");
        var other = Path.Combine(f.Dir, "work", "other.md");
        File.WriteAllText(other, "**Kim Lee** 0:01\nHello\n");
        var theirs = Path.Combine(f.Dir, "work", "sam.md");
        File.WriteAllText(theirs, "**Sam Jones** 0:01\nThe rollout moved.\n");
        f.Config.AfterWrite.Add("after");
        var engine = new Engine(f.Config, new OneMeeting(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones")),
            f.Log.Add, f.Host());
        engine.Update([other]);
        Assert.Empty(f.Prompts);
        f.Replies.Add("> **Update 12:00, from work sam** *(automatic)*\n> - **The rollout moved.**");
        engine.Update([theirs]);
        Assert.Contains("The rollout moved", File.ReadAllText(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md")));
        Assert.Contains("\"type\": \"update\"", f.Reports[^1]);
    }

    [Fact]
    public void test_refresh_happens_once_per_meeting()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 10, 30, 0));
        File.WriteAllText(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md"), "# [DRAFT] Sam\n");
        var inbox = new JsonObject
        {
            ["subject"] = "An email", ["bodyPreview"] = "news", ["receivedDateTime"] = "2099-01-01T00:00:00Z", ["parentFolderId"] = "i",
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Sam Jones", ["address"] = "sam@example.com" } },
            ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Alex Smith", ["address"] = "alex@example.com" } }),
        };
        var graph = new MailAndMeeting(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"), inbox);
        var engine = new Engine(f.Config, graph, f.Log.Add, f.Host());
        engine.Refresh();
        engine.Refresh();
        Assert.Single(f.Prompts);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(f.Config.StateFile))!["refreshed"]);
    }

    private sealed class MailAndMeeting(JsonObject e, JsonObject message) : IGraph
    {
        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal)
            ? new JsonObject { ["value"] = new JsonArray() }
            : new JsonObject { ["displayName"] = "Inbox" };

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? [e.DeepClone()]
            : url.StartsWith("/me/messages", StringComparison.Ordinal) ? [message.DeepClone()] : [];
    }

    [Fact]
    public void test_fill_leaves_other_braces() =>
        Assert.Equal("Sam and {other}", Engine.Fill("{name} and {other}", ("name", "Sam")));
}

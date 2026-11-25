using System.Text;
using System.Text.Json.Nodes;
using MeetingPreps.Parity;
using PyCompat;

namespace MeetingPreps.Tests;

/// <summary>1.11.0: `plan --json` for another program to show, and `mark-read` for the owner having read a prep. Made-up
/// people and meetings only.</summary>
public sealed class MarkReadTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("mp-read-").FullName;

    public void Dispose() => GitRepo.Delete(root);

    private sealed class Calendar(params JsonObject[] events) : IGraph
    {
        public bool Down { get; set; }

        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal)
            ? new JsonObject { ["value"] = new JsonArray() }
            : new JsonObject { ["displayName"] = "Inbox" };

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null)
        {
            if (Down)
                throw new IOException("502 Bad Gateway");
            return url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? events.Select(e => (JsonNode?)e.DeepClone()).ToList() : [];
        }
    }

    private static (int Code, string Out, string Err) Run(string[] args, Fixture f, IGraph? graph = null, bool echo = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var h = new PrepsHost
        {
            Out = output, Err = error, Clock = f.Clock, Graph = (_, _) => graph ?? new Fixture.NoGraph(), LockPrefix = "mp-test-",
            Notify = (_, _) => { }, LogEcho = echo ? output : null, Env = name => name == "APPDATA" ? Path.GetTempPath() : null,
        };
        return (MeetingPreps.Cli.Run(args, h), output.ToString(), error.ToString());
    }

    private static JsonObject Online(JsonObject e, string? room = null)
    {
        e["isOnlineMeeting"] = true;
        e["onlineMeeting"] = new JsonObject { ["joinUrl"] = "https://teams.example.com/join/1" };
        e["location"] = new JsonObject { ["displayName"] = room ?? "" };
        e["webLink"] = "https://outlook.example.com/event/1";
        return e;
    }

    // ---- plan --json ---------------------------------------------------------------------------------------

    [Fact]
    public void Plan_json_has_every_meeting_with_offsets_people_and_the_preps_state()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var prep = Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md");
        File.WriteAllText(prep, "# [DRAFT] Sam\n\n> **Update 09:00, from work sam** *(automatic)*\n\n## Items\n");
        var graph = new Calendar(
            Online(Fixture.Event("Sam 1:1 \u2615", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"), "Room 4"),
            Fixture.Event("All hands", "2026-11-11T14:00", "2026-11-11T15:00", "Sam Jones", "Kim Lee"),
            Fixture.Event("Catch-up", "2026-11-12T09:00", "2026-11-12T09:30", "Robin Ash"));

        var (code, output, _) = Run(["--config", config, "plan", "--from", "2026-11-11", "--days", "2", "--json"], f, graph);

        Assert.Equal(0, code);
        Assert.Contains("\u2615", output);   // UTF-8 as it is, not escaped
        var json = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("2026-11-11T08:00:00+00:00", (string)json["generated"]!);
        Assert.Equal("2026-11-11", (string)json["from"]!);
        Assert.Equal(2, (int)json["days"]!);
        Assert.Null(json["calendarFrom"]);
        var meetings = json["meetings"]!.AsArray();
        Assert.Equal(3, meetings.Count);

        var sam = meetings[0]!.AsObject();
        Assert.Equal("2026-11-11T11:00:00+00:00", (string)sam["start"]!);
        Assert.Equal("2026-11-11T11:30:00+00:00", (string)sam["end"]!);
        Assert.Equal("Sam 1:1 \u2615", (string)sam["subject"]!);
        Assert.Null(sam["series"]);
        Assert.Equal(["Sam Jones"], sam["attendees"]!.AsArray().Select(a => (string)a!));
        Assert.True((bool)sam["online"]!);
        Assert.Equal("Room 4", (string)sam["location"]!);
        Assert.Equal("https://teams.example.com/join/1", (string)sam["joinUrl"]!);
        Assert.Equal("prep", (string)sam["decision"]!);
        Assert.Null(sam["skip"]);
        var p = sam["prep"]!.AsObject();
        Assert.Equal("preps/2026-11-11-sam-jones-prep.md", (string)p["path"]!);
        Assert.True((bool)p["exists"]!);
        Assert.True((bool)p["draft"]!);
        Assert.False((bool)p["read"]!);
        Assert.Equal(1, (int)p["updates"]!);
        Assert.Equal("2026-11-11T07:00:00+00:00", (string)p["plannedFor"]!);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", (string)p["writtenAt"]!);

        var group = meetings[1]!.AsObject();
        Assert.Equal("skip", (string)group["decision"]!);
        Assert.Equal("group, 3 people", (string)group["skip"]!);
        Assert.False((bool)group["online"]!);
        Assert.Null(group["location"]);
        Assert.Null(group["prep"]!["path"]);
        Assert.False((bool)group["prep"]!["exists"]!);

        var unsure = meetings[2]!.AsObject();
        Assert.Equal("unsure", (string)unsure["decision"]!);
        Assert.Contains("Robin Ash", (string)unsure["skip"]!);

        // the same meetings, in the same order, as `plan` shows
        var text = Run(["--config", config, "plan", "2026-11-11", "2"], f, graph).Out;
        Assert.Contains("PREP (1)", text);
        Assert.Contains("UNSURE (1)", text);
        Assert.Contains("SKIP (1)", text);
    }

    [Fact]
    public void Plan_json_times_carry_summer_time()
    {
        using var f = new Fixture(now: new DateTime(2026, 6, 10, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var graph = new Calendar(Fixture.Event("Sam 1:1", "2026-06-10T10:00", "2026-06-10T10:45", "Sam Jones"));
        var json = JsonNode.Parse(Run(["--config", config, "plan", "--json"], f, graph).Out)!.AsObject();
        Assert.Equal("2026-06-10T08:00:00+01:00", (string)json["generated"]!);
        Assert.Equal("2026-06-10", (string)json["from"]!);
        Assert.Equal(5, (int)json["days"]!);   // plan_days, as `plan`
        Assert.Equal("2026-06-10T10:00:00+01:00", (string)json["meetings"]![0]!["start"]!);
        Assert.Equal("2026-06-10T10:45:00+01:00", (string)json["meetings"]![0]!["end"]!);
        Assert.False((bool)json["meetings"]![0]!["prep"]!["exists"]!);
        Assert.Null(json["meetings"]![0]!["prep"]!["writtenAt"]);
    }

    [Fact]
    public void Plan_json_with_no_meetings_is_still_json_and_the_log_stays_off_stdout()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var empty = Run(["--config", config, "plan", "--json"], f, new Calendar());
        Assert.Equal(0, empty.Code);
        Assert.Empty(JsonNode.Parse(empty.Out)!["meetings"]!.AsArray());

        // from the kept copy of the calendar in an outage: the log says so, on stderr, and stdout stays JSON
        var graph = new Calendar(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"));
        Assert.Equal(0, Run(["--config", config, "plan"], f, graph).Code);
        graph.Down = true;
        var cached = Run(["--config", config, "plan", "--json"], f, graph, echo: true);
        Assert.Equal(0, cached.Code);
        var json = JsonNode.Parse(cached.Out)!;
        Assert.Equal("2026-11-11T08:00:00+00:00", (string)json["calendarFrom"]!);
        Assert.Single(json["meetings"]!.AsArray());
        Assert.Contains("planning from the calendar as read", cached.Err);
    }

    [Fact]
    public void Plan_from_and_days_are_the_positional_date_and_days()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 8, 0, 0));
        var config = Path.Combine(f.Dir, "config.toml");
        var graph = new Calendar();
        Assert.Contains("for 3 days", Run(["--config", config, "plan", "--days", "3"], f, graph).Out);
        Assert.Contains("Wed 18 Nov for 2 days", Run(["--config", config, "plan", "--from=2026-11-18", "--days=2"], f, graph).Out);
        Assert.Equal(2, Run(["--config", config, "plan", "2026-11-18", "--from", "2026-11-18"], f, graph).Code);
        Assert.Equal(2, Run(["--config", config, "plan", "--from", "next week"], f, graph).Code);
        Assert.Equal(2, Run(["--config", config, "plan", "--days"], f, graph).Code);
    }

    // ---- the marker ------------------------------------------------------------------------------------------

    [Fact]
    public void Marking_read_takes_the_marker_out_and_undo_puts_it_back_leaving_everything_else()
    {
        const string prep = "# [DRAFT] Sam, Wed 11 Nov\r\n*Written automatically*\r\n\r\n## Items [DRAFT] stays\r\n";
        var (read, changes) = PrepFile.Read(prep);
        Assert.Equal("# Sam, Wed 11 Nov\r\n*Written automatically*\r\n\r\n## Items [DRAFT] stays\r\n", read);
        Assert.Single(changes);
        Assert.False(PrepFile.IsDraft(read));
        Assert.Empty(PrepFile.Read(read).Changes);   // already read
        var (again, undone) = PrepFile.Draft(read);
        Assert.Equal(prep, again);
        Assert.Single(undone);
        Assert.Empty(PrepFile.Draft(again).Changes);   // already a draft

        const string front = "---\nstatus: draft\ntitle: x\n---\n# Notes\n";
        Assert.True(PrepFile.IsDraft(front));
        var (frontRead, _) = PrepFile.Read(front);
        Assert.Equal("---\nstatus: read\ntitle: x\n---\n# Notes\n", frontRead);
        Assert.Equal(front, PrepFile.Draft(frontRead).Text);   // the status back, the title left alone

        Assert.Throws<InvalidDataException>(() => PrepFile.Draft("no title here\n"));
    }

    [Fact]
    public void Saving_keeps_the_byte_order_mark_and_the_creation_time()
    {
        var path = Path.Combine(root, "a.md");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# [DRAFT] Caf\u00e9\r\n")]);
        var created = new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, created);
        var (text, bom) = PrepFile.Load(path);
        Assert.True(bom);
        PrepFile.Save(path, PrepFile.Read(text).Text, bom);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Caf\u00e9\r\n")], File.ReadAllBytes(path));
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.Equal(new DateTimeOffset(created), Engine.WrittenAt(path));
    }

    // ---- mark-read, with [git] and [review] ------------------------------------------------------------------

    private (GitRepo Repo, string Config) Repo()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"), new JsonObject
        {
            ["meetings/preps/2026-11-11-sam-jones-prep.md"] = "# [DRAFT] Sam\r\n\r\n## Items\r\n",
            ["meetings/preps/2026-11-12-pat-boss-prep.md"] = "# [DRAFT] Pat\n",
            ["meetings/preps/2026-11-12-pat-boss-preread.md"] = "# [DRAFT] Pre-read\n",
        });
        var config = Path.Combine(repo.Path, "tools", "meeting-preps.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, $"""
            me = "Alex Smith"
            email = "alex@example.com"
            workspace = ".."
            output = "meetings/preps"
            state_file = "{Path.Combine(root, "state.json").Replace('\\', '/')}"
            log_file = "{Path.Combine(root, "log.txt").Replace('\\', '/')}"

            [git]
            repo = ".."
            trailers = ["Co-Authored-By: Someone <someone@example.com>"]

            [review]
            file = "{Path.Combine(root, "pending.json").Replace('\\', '/')}"
            source = "preps"
            lock = "mp-read-test-{Guid.NewGuid():N}"

            [kind.report]
            describe = "weekly 1:1 with {"{name}"}"
            rules = "Read the notes."

            [person."Sam Jones"]
            kind = "report"
            slug = "sam-jones"
            file = "{"{date}"}-sam-jones-prep.md"
            """.ReplaceLineEndings("\n"));
        return (repo, config);
    }

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = MeetingPreps.Cli.Run(args, new PrepsHost
        {
            Out = output, Err = error, LockPrefix = "mp-test-", Notify = (_, _) => { }, Graph = (_, _) => new Fixture.NoGraph(),
            Clock = Clock.Fixed(new DateTime(2026, 11, 11, 9, 0, 0), ScenarioRunner.London),
        });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Mark_read_commits_the_file_alone_and_clears_its_review_items()
    {
        var (repo, config) = Repo();
        var settings = Config.Load(config);
        var sam = repo.File("meetings/preps/2026-11-11-sam-jones-prep.md");
        var queue = settings.Review!.Open();
        queue.Add("preps", "prep", new JsonObject { ["files"] = new JsonArray("meetings/preps/2026-11-11-sam-jones-prep.md"), ["title"] = "Sam" });
        queue.Add("preps", "update", new JsonObject { ["files"] = new JsonArray("meetings/preps/2026-11-11-sam-jones-prep.md"), ["from"] = "x" });
        queue.Add("preps", "prep", new JsonObject { ["files"] = new JsonArray("meetings/preps/2026-11-12-pat-boss-prep.md") });
        queue.Add("other", "prep", new JsonObject { ["files"] = new JsonArray("meetings/preps/2026-11-11-sam-jones-prep.md") });
        repo.Write(new JsonObject { ["notes.md"] = "something else changed\n" });

        var dry = Cli("--config", config, "mark-read", sam, "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Contains("Would mark meetings/preps/2026-11-11-sam-jones-prep.md read", dry.Out);
        Assert.Contains("Would mark 2 review item(s) done", dry.Out);
        Assert.Equal("# [DRAFT] Sam\r\n\r\n## Items\r\n", File.ReadAllText(sam));
        Assert.Empty(repo.Commits());
        Assert.Equal(4, queue.Items().Count);

        // a path relative to the workspace works too
        var done = Cli("--config", config, "mark-read", "meetings/preps/2026-11-11-sam-jones-prep.md");
        Assert.Equal(0, done.Code);
        Assert.Equal("# Sam\r\n\r\n## Items\r\n", File.ReadAllText(sam));
        var commit = Assert.Single(repo.Commits())!;
        Assert.Equal("Mark 2026-11-11-sam-jones-prep.md read\n\nDone by meeting-preps mark-read (tools/meeting-preps.toml).\n\n"
                     + "Co-Authored-By: Someone <someone@example.com>", (string)commit["message"]!);
        Assert.Equal(["M\tmeetings/preps/2026-11-11-sam-jones-prep.md"], commit["files"]!.AsArray().Select(x => (string)x!));
        Assert.Contains("?? notes.md", repo.Status().Select(x => (string)x!));   // nothing else swept up
        Assert.Equal(["meetings/preps/2026-11-12-pat-boss-prep.md", "meetings/preps/2026-11-11-sam-jones-prep.md"],
            queue.Items().Select(i => (string)i["files"]![0]!));   // the other prep's, and another source's, stay

        // again: nothing to change or commit, and nothing left to clear
        var again = Cli("--config", config, "mark-read", sam);
        Assert.Equal(0, again.Code);
        Assert.Contains("already read", again.Out);
        Assert.Single(repo.Commits());

        // --undo: the marker back, committed; the review items stay done
        var undo = Cli("--config", config, "mark-read", sam, "--undo");
        Assert.Equal(0, undo.Code);
        Assert.Equal("# [DRAFT] Sam\r\n\r\n## Items\r\n", File.ReadAllText(sam));
        Assert.Equal("Mark 2026-11-11-sam-jones-prep.md as a draft again", ((string)repo.Commits()[^1]!["message"]!).Split('\n')[0]);
        Assert.Equal(2, queue.Items().Count);
    }

    [Fact]
    public void A_prep_and_its_companion_are_done_in_the_queue_once_both_are_read()
    {
        var (repo, config) = Repo();
        var queue = Config.Load(config).Review!.Open();
        queue.Add("preps", "prep", new JsonObject
        {
            ["files"] = new JsonArray("meetings/preps/2026-11-12-pat-boss-prep.md", "meetings/preps/2026-11-12-pat-boss-preread.md"),
        });
        Assert.Equal(0, Cli("--config", config, "mark-read", repo.File("meetings/preps/2026-11-12-pat-boss-prep.md")).Code);
        Assert.Single(queue.Items());   // the pre-read is still a draft
        Assert.Equal(0, Cli("--config", config, "mark-read", repo.File("meetings/preps/2026-11-12-pat-boss-preread.md")).Code);
        Assert.Empty(queue.Items());
    }

    [Fact]
    public void Mark_read_refuses_what_isnt_a_prep_and_works_without_git()
    {
        var (repo, config) = Repo();
        Assert.Equal(2, Cli("--config", config, "mark-read", "meetings/preps/nothing.md").Code);
        var outside = Path.Combine(root, "elsewhere.md");
        File.WriteAllText(outside, "# [DRAFT] x\n");
        Assert.Equal(2, Cli("--config", config, "mark-read", outside).Code);
        Assert.Equal("# [DRAFT] x\n", File.ReadAllText(outside));
        repo.Write(new JsonObject { ["meetings/preps/untitled.md"] = "no title\n" });
        Assert.Equal(2, Cli("--config", config, "mark-read", "meetings/preps/untitled.md", "--undo").Code);
        Assert.Equal(2, Cli("--config", config, "mark-read").Code);
        Assert.Equal(2, Cli("--config", config, "mark-read", "a.md", "--yes").Code);

        // no repository: the file is still marked, and nothing is committed
        GitRepo.Delete(Path.Combine(repo.Path, ".git"));
        var done = Cli("--config", config, "mark-read", "meetings/preps/2026-11-12-pat-boss-prep.md");
        Assert.Equal(0, done.Code);
        Assert.Equal("# Pat\n", File.ReadAllText(repo.File("meetings/preps/2026-11-12-pat-boss-prep.md")));
        Assert.DoesNotContain("Committed", done.Out);
    }

    // ---- a read prep, kept current -----------------------------------------------------------------------------

    [Fact]
    public void A_read_prep_is_still_updated_and_refreshed_and_stays_read()
    {
        using var f = new Fixture(now: new DateTime(2026, 11, 11, 10, 30, 0));
        var prep = Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md");
        File.WriteAllText(prep, "# [DRAFT] Sam\n\n## Items\n");
        var written = new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(prep, written);
        File.SetLastWriteTimeUtc(prep, written);
        var config = Path.Combine(f.Dir, "config.toml");
        Assert.Equal(0, Run(["--config", config, "mark-read", prep], f).Code);
        Assert.Equal(new DateTimeOffset(written), Engine.WrittenAt(prep));   // the refresh's "since" hasn't moved

        var inbox = new JsonObject
        {
            ["subject"] = "An email", ["bodyPreview"] = "news", ["receivedDateTime"] = "2026-11-11T09:00:00Z", ["parentFolderId"] = "i",
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Sam Jones", ["address"] = "sam@example.com" } },
            ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Alex Smith", ["address"] = "alex@example.com" } }),
        };
        var graph = new MailAndMeeting(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"), inbox);
        f.Replies.Add("> **Update 10:30, from email** *(automatic)*\n> - **News.**");
        var engine = new Engine(f.Config, graph, f.Log.Add, f.Host());
        Assert.Equal(0, engine.Refresh());
        Assert.Contains("Wednesday 11 November 07:00", Assert.Single(f.Prompts));
        var text = File.ReadAllText(prep);
        Assert.StartsWith("# Sam\r\n", text);   // not marked a draft again: the update's own review item is the news
        Assert.Contains("News.", text);
        Assert.False(PrepFile.IsDraft(text));

        // and `run` leaves it alone: it exists
        Assert.Equal(0, engine.Run());
        Assert.Single(f.Prompts);
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
    public void Plan_json_says_when_a_read_prep_has_been_added_to_since()
    {
        var (repo, config) = Repo();
        var sam = repo.File("meetings/preps/2026-11-11-sam-jones-prep.md");
        var settings = Config.Load(config);
        Assert.Equal(0, Cli("--config", config, "mark-read", sam).Code);
        var f = new Fixture(now: new DateTime(2026, 11, 11, 9, 0, 0));
        try
        {
            var engine = new Engine(settings, new Calendar(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones")), _ => { }, f.Host());
            JsonObject Prep() => PlanJson.Build(settings, engine, engine.PlanRows(new DateOnly(2026, 11, 11), 1), new DateOnly(2026, 11, 11), 1, f.Clock)
                ["meetings"]![0]!["prep"]!.AsObject();
            Assert.Equal(0, (int)Prep()["queued"]!);
            settings.Review!.Open().Add("preps", "update", new JsonObject { ["files"] = new JsonArray("meetings/preps/2026-11-11-sam-jones-prep.md") });
            var p = Prep();
            Assert.True((bool)p["read"]!);
            Assert.True((bool)p["updatedSinceRead"]!);
            Assert.Equal("meetings/preps/2026-11-11-sam-jones-prep.md", (string)p["path"]!);
            Assert.Equal(0, Cli("--config", config, "mark-read", sam).Code);   // reading the update clears it
            Assert.False((bool)Prep()["updatedSinceRead"]!);
        }
        finally
        {
            f.Dispose();
        }
    }
}

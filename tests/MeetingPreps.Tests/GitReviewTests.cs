using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingPreps.Tests;

/// <summary>[git] and [review]: what's committed and queued after each file written is exactly what the EA's after_write
/// hook (tools/preps/after-write.py) did for the same report. Golden/hooks.json is that hook run, unchanged, on each
/// case in a temporary repository with a fixed clock: its commits (message, name-status), git status after, and the
/// pending.json items it left.</summary>
public sealed class GitReviewTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 17, 9, 30, 5);
    private readonly string root = Directory.CreateTempSubdirectory("mp-git-").FullName;
    private readonly List<string> log = [];

    public void Dispose() => GitRepo.Delete(root);

    private static JsonArray Golden() =>
        PyJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "hooks.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    /// <summary>The EA's layout: the settings in tools/ of the repository, the queue outside it.</summary>
    private Config Settings(string repo, string extra = "") =>
        Write(Path.Combine(repo, "tools", "meeting-preps.toml"), $"""
            me = "Alex Smith"
            email = "alex@example.com"
            workspace = ".."
            output = "meetings/preps"

            [git]
            repo = ".."
            trailers = ["Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"]

            [review]
            file = "{Path.Combine(root, "pending.json").Replace('\\', '/')}"
            source = "preps"
            lock = "mp-review-test-{Guid.NewGuid():N}"
            {extra}
            """);

    private static Config Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return Config.Load(path);
    }

    private static string Real(string path, string repo, string outside) =>
        path.StartsWith("<outside>/", StringComparison.Ordinal) ? Path.Combine(outside, path[10..]) : Path.Combine(repo, path.Replace('/', '\\'));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Commits_and_items_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!.AsObject();
        var repo = new GitRepo(Path.Combine(root, "repo"), golden["committed"] as JsonObject, golden["failing_hook"] is not null);
        var outside = Path.Combine(root, "outside");
        repo.Write(golden["untracked"] as JsonObject);
        repo.Write(golden["written"] as JsonObject);
        repo.Write(golden["written_outside"] as JsonObject, outside);
        var config = Settings(repo.Path);
        var report = golden["report"]!.AsObject();

        Publish.After(config, (string)report["type"]!, (string)report["meeting"]!, (string)report["start"]!,
            [.. report["files"]!.AsArray().Select(f => Real((string)f!, repo.Path, outside))], (string?)report["from"], log.Add, () => Now);

        Assert.Equal(golden["commits"]!.ToJsonString(), repo.Commits().ToJsonString());
        Assert.Equal(golden["status"]!.ToJsonString(), repo.Status().ToJsonString());
        Assert.Equal(PyJson.Dump(golden["items"]), PyJson.Dump(new JsonArray([.. config.Review!.Open().Items()])));
    }

    [Fact]
    public void Neither_section_is_todays_behaviour_nothing_committed_or_queued()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        repo.Write(new JsonObject { ["meetings/preps/a.md"] = "a\n" });
        var config = Write(Path.Combine(repo.Path, "tools", "meeting-preps.toml"), "me = \"A\"\nemail = \"a@example.com\"\nworkspace = \"..\"\n");
        Assert.Null(config.Git);
        Assert.Null(config.Review);
        Publish.After(config, "prep", "M", "2026-11-18T08:45", [repo.File("meetings/preps/a.md")], null, log.Add, () => Now);
        Assert.Empty(repo.Commits());
        Assert.False(File.Exists(Path.Combine(root, "pending.json")));
        Assert.Empty(log);
    }

    [Fact]
    public void Commit_false_still_queues_and_a_push_goes_to_the_upstream()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        repo.Write(new JsonObject { ["meetings/preps/a.md"] = "a\n" });
        var off = Settings(repo.Path).Git! with { Commit = false };
        var config = Settings(repo.Path);
        var quiet = new Config
        {
            Path = config.Path, Me = config.Me, Email = config.Email, Workspace = config.Workspace, Output = config.Output, History = config.History,
            LogFile = config.LogFile, StateFile = config.StateFile, CalendarCache = config.CalendarCache, Git = off, Review = config.Review,
        };
        Publish.After(quiet, "prep", "M", "2026-11-18T08:45", [repo.File("meetings/preps/a.md")], null, log.Add, () => Now);
        Assert.Empty(repo.Commits());
        Assert.Single(config.Review!.Open().Items());

        var bare = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(bare);
        new GitRepoBare(bare);
        repo.Git("remote", "add", "origin", bare);
        repo.Git("push", "-q", "-u", "origin", "main");
        var pushing = Settings(repo.Path, "").Git! with { Push = true };
        var withPush = new Config
        {
            Path = config.Path, Me = config.Me, Email = config.Email, Workspace = config.Workspace, Output = config.Output, History = config.History,
            LogFile = config.LogFile, StateFile = config.StateFile, CalendarCache = config.CalendarCache, Git = pushing,
        };
        Publish.After(withPush, "prep", "M", "2026-11-18T08:45", [repo.File("meetings/preps/a.md")], null, log.Add, () => Now);
        Assert.Contains("  git: pushed", log);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), new GitRepoBare(bare, init: false).Git("rev-parse", "main"));
    }

    [Fact]
    public void An_update_from_a_transcript_is_committed_and_queued_by_the_engine()
    {
        using var f = new Fixture(Fixture.Settings + $"""

            [git]
            repo = "work"

            [review]
            file = "queue.json"
            source = "preps"
            lock = "mp-review-test-{Guid.NewGuid():N}"
            """, new DateTime(2026, 11, 11, 8, 0, 0));
        var repo = new GitRepo(f.Config.Workspace);
        try
        {
            UpdateIsCommitted(f, repo);
        }
        finally
        {
            GitRepo.Delete(Path.Combine(f.Config.Workspace, ".git"));   // read-only objects, which the fixture can't delete
        }
    }

    private static void UpdateIsCommitted(Fixture f, GitRepo repo)
    {
        File.WriteAllText(Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md"), "# [DRAFT] Sam\n\n## Items\n");
        var theirs = Path.Combine(f.Dir, "work", "sam.md");
        File.WriteAllText(theirs, "**Sam Jones** 0:01\nThe rollout moved.\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "Before");
        var engine = new Engine(f.Config, new OneMeeting(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones")),
            f.Log.Add, f.Host());
        f.Replies.Add("> **Update 12:00, from work sam** *(automatic)*\n> - **The rollout moved.**");
        engine.Update([theirs]);

        var commit = Assert.Single(repo.Commits().Skip(1));
        Assert.StartsWith("Update the Sam 1:1 prep from ", (string)commit!["message"]!);
        // The settings are outside the repository here, so the message names the file alone.
        Assert.EndsWith("\n\nWritten automatically by meeting-preps (config.toml).", (string)commit["message"]!);
        Assert.Equal("M\tpreps/2026-11-11-sam-jones-prep.md", (string)commit["files"]![0]!);
        var item = Assert.Single(f.Config.Review!.Open().Items());
        Assert.Equal(("preps", "update", "Sam 1:1, 2026-11-11 11:00"), ((string)item["source"]!, (string)item["type"]!, (string)item["title"]!));
        Assert.Equal("[\"preps/2026-11-11-sam-jones-prep.md\"]", item["files"]!.ToJsonString());
        Assert.Contains(f.Log, l => l.StartsWith("  git: committed 1 path(s) as ", StringComparison.Ordinal));
        Assert.Contains("  review: queued the update under preps", f.Log);
    }

    private sealed class OneMeeting(JsonObject e) : IGraph
    {
        public JsonObject Get(string url) => [];
        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? [e.DeepClone()] : [];
    }

    private sealed class GitRepoBare
    {
        private readonly string path;

        public GitRepoBare(string path, bool init = true)
        {
            this.path = path;
            if (init)
                Git("init", "-q", "--bare", "-b", "main");
        }

        public string Git(params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = path };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Replace("\r\n", "\n");
        }
    }

    [Theory]
    [InlineData("[git]\nrepo = \"..\"\nrepository = \"x\"\n", "repository under [git] in ")]
    [InlineData("[git]\npush = true\n", "needs repo")]
    [InlineData("[git]\nrepo = \"no-such-folder\"\n", "isn't a folder: ")]
    [InlineData("[git]\nrepo = \"..\"\ntrailers = [\"not a trailer\"]\n", "trailers under [git] in ")]
    [InlineData("[review]\nsource = \"preps\"\n", "needs file")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"preps\"\nact = true\n", "act under [review] in ")]
    public void Bad_settings_are_refused(string toml, string message)
    {
        var path = Path.Combine(root, "tools", "meeting-preps.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "me = \"A\"\nemail = \"a@example.com\"\nworkspace = \"..\"\n\n" + toml);
        Assert.Contains(message, Assert.Throws<SettingsException>(() => Config.Load(path)).Message);
    }
}

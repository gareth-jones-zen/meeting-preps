using System.Text.Json.Nodes;
using ActivityJournal;

namespace MeetingPreps.Tests;

/// <summary>1.12.0: each prep written, updated or refreshed into the workspace, and each mark-read, is a line in the
/// shared activity journal (background-tools-lib's ActivityJournal), restricted; never a dry run or a trial write, and
/// writing it never fails the run. Made-up people and meetings only.</summary>
public sealed class JournalTests
{
    private const string PrepReply = "Here you go.\n# [DRAFT] Sam Jones 1:1, Wed 11 Nov\n*Written automatically.*\n\n## Items\n- one";

    private static Fixture WithJournal(DateTime? now = null, string journal = "journal_file = \"journal.jsonl\"\n") =>
        new(journal + Fixture.Settings, now ?? new DateTime(2026, 11, 11, 8, 0, 0));

    private static IReadOnlyList<JournalEntry> Lines(Fixture f) =>
        f.Config.JournalFile is { } path && File.Exists(path) ? new Journal(path).Read() : [];

    private sealed class OneMeeting(JsonObject e) : IGraph
    {
        public JsonObject Get(string url) => url.StartsWith("/me/chats", StringComparison.Ordinal)
            ? new JsonObject { ["value"] = new JsonArray() }
            : new JsonObject { ["displayName"] = "Inbox" };

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null) =>
            url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? [e.DeepClone()] : [];
    }

    private static OneMeeting SamAt11() => new(Fixture.Event("Sam 1:1", "2026-11-11T11:00", "2026-11-11T11:30", "Sam Jones"));

    [Fact]
    public void A_prep_written_by_hand_is_a_restricted_line_and_a_trial_write_is_none()
    {
        using var f = WithJournal();
        var engine = new Engine(f.Config, SamAt11(), f.Log.Add, f.Host()) { By = By.Hand };

        f.Replies.Add(PrepReply);
        var scratch = Path.Combine(Path.GetTempPath(), "mp-journal-trial-" + Guid.NewGuid().ToString("N"));
        try
        {
            engine.WriteOne(new DateOnly(2026, 11, 11), "Sam Jones", scratch);   // a trial folder, outside the workspace
            Assert.Empty(Lines(f));
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, true);
        }

        f.Replies.Add(PrepReply);
        engine.WriteOne(new DateOnly(2026, 11, 11), "Sam Jones");
        var line = Assert.Single(Lines(f));
        Assert.Equal("meeting-preps", line.Tool);
        Assert.Equal("prep", line.What);
        Assert.Equal(By.Hand, line.By);
        Assert.Equal("Sam 1:1, Wed 11 Nov 11:00", line.Subject);
        Assert.Equal("prep", line.Rule);
        Assert.Equal("due: a draft to read before the meeting", line.Why);
        Assert.Equal("preps/2026-11-11-sam-jones-prep.md", line.Item);
        Assert.Equal(Sensitivity.Restricted, line.Sensitivity);
        Assert.Null(line.Undo);
    }

    [Fact]
    public void A_runs_prep_and_an_update_are_auto_and_a_dry_run_writes_nothing()
    {
        using var f = WithJournal(new DateTime(2026, 11, 11, 10, 0, 0));
        var engine = new Engine(f.Config, SamAt11(), f.Log.Add, f.Host());
        Assert.Equal(0, engine.Run(dryRun: true));
        Assert.Empty(Lines(f));

        f.Replies.Add(PrepReply);
        Assert.Equal(0, engine.Run());
        var prep = Assert.Single(Lines(f));
        Assert.Equal(("prep", By.Auto), (prep.What, prep.By));

        var theirs = Path.Combine(f.Dir, "work", "sam.md");
        File.WriteAllText(theirs, "**Sam Jones** 0:01\nThe rollout moved.\n");
        f.Replies.Add("> **Update 12:00, from work sam** *(automatic)*\n> - **The rollout moved.**");
        engine.Update([theirs]);
        var update = Lines(f)[1];
        Assert.Equal(("update", By.Auto), (update.What, update.By));
        Assert.StartsWith("Sam 1:1, Wed 11 Nov 11:00 (from ", update.Subject);
        Assert.Equal("a transcript since the prep was written", update.Why);
        Assert.Equal(Sensitivity.Restricted, update.Sensitivity);
    }

    [Fact]
    public void Mark_read_and_undo_are_by_hand_each_with_the_other_as_its_undo()
    {
        using var f = WithJournal();
        var prep = Path.Combine(f.Config.Output, "2026-11-11-sam-jones-prep.md");
        File.WriteAllText(prep, "# [DRAFT] Sam\n\n## Items\n");
        var config = Path.Combine(f.Dir, "config.toml");

        Assert.Equal(0, Cli(f, "--config", config, "mark-read", prep, "--dry-run"));
        Assert.Empty(Lines(f));
        Assert.Equal(0, Cli(f, "--config", config, "mark-read", prep));
        Assert.Equal(0, Cli(f, "--config", config, "mark-read", prep));   // already read: nothing changed, no line
        Assert.Equal(0, Cli(f, "--config", config, "mark-read", prep, "--undo"));

        var lines = Lines(f);
        Assert.Equal(2, lines.Count);
        Assert.Equal(("mark-read", By.Hand, "by hand"), (lines[0].What, lines[0].By, lines[0].Rule));
        Assert.Equal("preps/2026-11-11-sam-jones-prep.md", lines[0].Subject);
        Assert.Equal("meeting-preps mark-read \"preps/2026-11-11-sam-jones-prep.md\" --undo", lines[0].Undo);
        Assert.Equal("mark-draft", lines[1].What);
        Assert.Equal("meeting-preps mark-read \"preps/2026-11-11-sam-jones-prep.md\"", lines[1].Undo);
        Assert.All(lines, l => Assert.Equal(Sensitivity.Restricted, l.Sensitivity));
    }

    [Fact]
    public void Without_journal_file_nothing_is_written_and_a_bad_journal_never_fails_the_write()
    {
        using var f = WithJournal(journal: "");
        Assert.Null(f.Config.JournalFile);
        f.Replies.Add(PrepReply);
        new Engine(f.Config, SamAt11(), f.Log.Add, f.Host()).WriteOne(new DateOnly(2026, 11, 11), "Sam Jones");
        Assert.Empty(Directory.GetFiles(f.Dir, "*.jsonl", SearchOption.AllDirectories));

        using var g = WithJournal();
        Directory.CreateDirectory(g.Config.JournalFile!);   // a folder where the file should be
        g.Replies.Add(PrepReply);
        new Engine(g.Config, SamAt11(), g.Log.Add, g.Host()).WriteOne(new DateOnly(2026, 11, 11), "Sam Jones");
        Assert.True(File.Exists(Path.Combine(g.Config.Output, "2026-11-11-sam-jones-prep.md")));
        Assert.Contains(g.Log, l => l.Contains("journal: couldn't write", StringComparison.Ordinal));
    }

    [Fact]
    public void Journal_file_comes_from_a_base_with_its_path_variables()
    {
        var dir = Directory.CreateTempSubdirectory("mp-journal-base-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "base-config.toml"), $"journal_file = '%SHARED%/journal.jsonl'\n[paths]\nSHARED = '{dir}\\shared'\n");
            var path = Path.Combine(dir, "meeting-preps.toml");
            File.WriteAllText(path, "base = \"base-config.toml\"\nme = \"Alex Smith\"\nemail = \"alex@example.com\"\nworkspace = \".\"\nstate_file = \"state.json\"\nlog_file = \"log.txt\"\n");
            Assert.Equal(Path.Combine(dir, "shared", "journal.jsonl"), Path.GetFullPath(Config.Load(path).JournalFile!));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int Cli(Fixture f, params string[] args) =>
        MeetingPreps.Cli.Run(args, new PrepsHost
        {
            Out = new StringWriter(), Err = new StringWriter(), Clock = f.Clock, Graph = (_, _) => new Fixture.NoGraph(), LockPrefix = "mp-test-",
            Notify = (_, _) => { }, Env = name => name == "APPDATA" ? Path.GetTempPath() : null,
        });
}

using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingPreps.Tests;

/// <summary>A weekly document written in a block of your own (the weekly leadership update): own_block, a series'
/// own folder, {week} and {monday}, same-day timing that holds after the block starts, a document kind's prompt and
/// updates, and write --out.</summary>
public class WeeklyUpdateTests
{
    private const string Weekly = """

        [kind.weekly]
        describe = "weekly update, the team's section"
        rules = "Read updates/whole-team/{monday}.md. File it as {file}, for the week of {week} (written {date})."
        document = true
        every_transcript = true

        [series."Weekly update"]
        kind = "weekly"
        own_block = true
        folder = "updates"
        file = "{week}.md"
        due_same_day_at = "14:00"
        """;

    private const string Reply = "Sure.\n# [DRAFT] Team\n\n*Drafted automatically.*\n\n- **Thing** - happened\n\n---\n## For review\n- none";

    private static Fixture Make(DateTime now, string extra = "")
    {
        var f = new Fixture(Fixture.Settings + Weekly + "\n" + extra, now);
        Directory.CreateDirectory(Path.Combine(f.Dir, "work", "updates", "whole-team"));
        return f;
    }

    /// <summary>The block: Friday 13 Nov 2026, 16:30-17:00, yours, nobody else in it.</summary>
    private static JsonObject Block(string start = "2026-11-13T16:30", string end = "2026-11-13T17:00") =>
        Fixture.Event("Weekly update", start, end);

    /// <summary>A calendar, plus every Graph URL asked for.</summary>
    private sealed class Recording(params JsonObject[] events) : IGraph
    {
        public List<string> Urls { get; } = [];

        public JsonObject Get(string url)
        {
            Urls.Add(url);
            return url.StartsWith("/me/chats", StringComparison.Ordinal) ? new JsonObject { ["value"] = new JsonArray() }
                : new JsonObject { ["displayName"] = "Inbox" };
        }

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null)
        {
            Urls.Add(url);
            return url.StartsWith("/me/calendarView", StringComparison.Ordinal) ? events.Select(e => (JsonNode?)e.DeepClone()).ToList() : [];
        }
    }

    [Fact]
    public void The_settings_are_read()
    {
        using var f = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        var series = f.Config.Series("Weekly update")!;
        Assert.True(series.OwnBlock);
        Assert.Equal(Path.Combine(f.Dir, "work", "updates"), series.Folder);
        Assert.True(f.Config.Kinds["weekly"].Document);
        Assert.False(f.Config.Kinds["report"].Document);
        Assert.False(f.Config.Series("Leads weekly")!.OwnBlock);
        Assert.Null(f.Config.Series("Leads weekly")!.Folder);
    }

    [Fact]
    public void Own_block_and_weekly_together_are_refused_and_monday_counts_as_a_date()
    {
        using var f = new Fixture();
        var bad = Path.Combine(f.Dir, "bad.toml");
        File.WriteAllText(bad, "me = \"a\"\nemail = \"a@b\"\n[kind.k]\ndescribe = \"d\"\n[series.\"S\"]\nkind = \"k\"\nfile = \"{week}.md\"\n"
                               + "own_block = true\nweekly = [{day = \"Fri\", start = \"16:30\", end = \"17:00\"}]\n");
        Assert.Contains("own_block and weekly", Assert.Throws<SettingsException>(() => Config.Load(bad)).Message);
        File.WriteAllText(bad, "me = \"a\"\nemail = \"a@b\"\n[kind.k]\ndescribe = \"d\"\n[series.\"S\"]\nkind = \"k\"\nfile = \"{monday}-x.md\"\n");
        Assert.Equal("{monday}-x.md", Config.Load(bad).Series("S")!.File);
    }

    [Fact]
    public void Your_own_block_is_planned_into_its_folder_under_the_coming_monday()
    {
        using var f = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        var row = Planner.Classify(f.Config, Block());
        Assert.Equal(("prep", "weekly", "Weekly update"), (row.Decision, row.Kind, row.Series));
        Assert.Equal("2026-11-16.md", row.File);   // Friday 13 Nov is filed under Monday 16 Nov
        Assert.Equal(Path.Combine(f.Dir, "work", "updates"), row.Folder);
        Assert.Equal("2026-11-13T14:00", row.Due);
        Assert.Null(row.NotAttending);
        Assert.Equal(Path.Combine(f.Dir, "work", "updates", "2026-11-16.md"), f.Engine().Target(row));
        // A Monday block is filed under that Monday; a block at 14:30 is due an hour before it.
        Assert.Equal("2026-11-16.md", Planner.Classify(f.Config, Block("2026-11-16T09:00", "2026-11-16T09:30")).File);
        Assert.Equal("2026-11-13T13:30", Planner.Classify(f.Config, Block("2026-11-13T14:30", "2026-11-13T15:00")).Due);
    }

    [Fact]
    public void Monday_is_the_blocks_own_week_and_week_the_coming_monday()
    {
        Assert.Equal(new DateOnly(2026, 11, 9), Planner.WeekStart(new DateOnly(2026, 11, 13)));
        Assert.Equal(new DateOnly(2026, 11, 16), Planner.WeekStart(new DateOnly(2026, 11, 16)));
        Assert.Equal(new DateOnly(2026, 11, 16), Planner.WeekOf(new DateOnly(2026, 11, 13)));
        using var f = new Fixture(Fixture.Settings + Weekly.Replace("file = \"{week}.md\"", "file = \"{monday}-week.md\""));
        Assert.Equal("2026-11-09-week.md", Planner.Classify(f.Config, Block()).File);
    }

    [Fact]
    public void Only_your_own_block_matches_an_own_block_series()
    {
        using var f = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        // The same subject with someone in it is a meeting like any other: here a 1:1 with Sam.
        var meeting = Planner.Classify(f.Config, Fixture.Event("Weekly update", "2026-11-13T16:30", "2026-11-13T17:00", "Sam Jones"));
        Assert.Equal(("prep", "report", "Sam Jones"), (meeting.Decision, meeting.Kind, meeting.With));
        // Someone else's invite with nobody else on it isn't your block.
        var theirs = Block();
        theirs["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Kim Lee", ["address"] = "kim@example.com" } };
        Assert.Equal(("skip", "your own block"), (Planner.Classify(f.Config, theirs).Decision, Planner.Classify(f.Config, theirs).Why));
        // Shown as free, it's still yours to write in, and not "not attending".
        var free = Block();
        free["showAs"] = "free";
        var row = Planner.Classify(f.Config, free);
        Assert.Equal("prep", row.Decision);
        Assert.Null(row.NotAttending);
    }

    [Fact]
    public void No_block_means_no_draft_and_leave_means_none_either()
    {
        using var f = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        var start = new DateOnly(2026, 11, 9);
        Assert.DoesNotContain(Planner.PlanAll(f.Config, [], start, 7), p => p.Subject == "Weekly update");
        var holiday = Fixture.Event("Holiday", "2026-11-13T00:00", "2026-11-14T00:00");
        holiday["isAllDay"] = true;
        var plan = Planner.PlanAll(f.Config, [holiday, Block()], start, 7);
        Assert.Equal("you're on leave", plan.Single(p => p.Subject == "Weekly update").Why);
    }

    [Fact]
    public void The_previous_one_is_from_its_folder_only_and_earlier()
    {
        using var f = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        var folder = Path.Combine(f.Dir, "work", "updates");
        foreach (var name in new[] { "2026-11-02.md", "2026-11-09.md", "2026-11-16.md", "notes.md" })
            File.WriteAllText(Path.Combine(folder, name), "x");
        File.WriteAllText(Path.Combine(folder, "whole-team", "2026-11-12.md"), "someone else's");
        File.WriteAllText(Path.Combine(f.Config.Output, "2026-11-12.md"), "a prep, not this");
        var row = Planner.Classify(f.Config, Block());
        Assert.Equal("updates/2026-11-09.md", row.Previous);
        Assert.True(row.Exists);
    }

    [Fact]
    public void Its_folder_is_added_for_claude_when_outside_the_workspace()
    {
        using var f = new Fixture(Fixture.Settings + Weekly.Replace("folder = \"updates\"", "folder = \"../elsewhere\""));
        Assert.Equal(["--add-dir", Path.Combine(f.Dir, "elsewhere")], f.Engine().ClaudeCommand()[^2..]);
        using var inside = Make(new DateTime(2026, 11, 13, 9, 0, 0));
        Assert.DoesNotContain("--add-dir", inside.Engine().ClaudeCommand());
    }

    [Fact]
    public void A_document_prompt_is_its_rules_and_the_weeks_diary_and_email_not_a_prep()
    {
        using var f = Make(new DateTime(2026, 11, 13, 14, 0, 0));
        File.WriteAllText(Path.Combine(f.Dir, "work", "updates", "2026-11-09.md"), "last week");
        var graph = new Recording(Fixture.Event("Sam 1:1", "2026-11-10T11:00", "2026-11-10T11:30", "Sam Jones"), Block());
        var engine = new Engine(f.Config, graph, f.Log.Add, f.Host());
        var prompt = engine.PromptFor(new DateOnly(2026, 11, 13), "Weekly update");
        Assert.StartsWith("Write Alex's weekly update, the team's section, for their \"Weekly update\" slot, Friday 13 November 2026, "
                          + "16:30–17:00. It is now Friday 13 November 2026 14:00.\n", prompt);
        Assert.Contains("It covers the week from Mon 09 Nov 2026 to now, and is filed as updates/2026-11-16.md. The latest earlier one is updates/2026-11-09.md.", prompt);
        Assert.Contains("Read updates/whole-team/2026-11-09.md. File it as updates/2026-11-16.md, for the week of 2026-11-16 (written 2026-11-13).", prompt);
        Assert.Contains("Diary:\n- Tue 10 Nov 11:00–11:30  Sam 1:1  (with Sam Jones)\n- Fri 13 Nov 16:30–17:00  Weekly update", prompt);
        Assert.Contains("Email this week (oldest first):\n(none)", prompt);
        // None of a prep's scaffolding: no "Must hold", no other person, no house rules, no Teams without a chat.
        Assert.DoesNotContain("Must hold", prompt);
        Assert.DoesNotContain("prep", prompt);
        Assert.DoesNotContain("No code names", prompt);
        Assert.DoesNotContain("Teams", prompt);
        // The diary is the block's week from Monday; email is from Monday 00:00 (UK time, GMT in November).
        Assert.Contains(graph.Urls, u => u.StartsWith("/me/calendarView?startDateTime=2026-11-09T00%3A00%3A00&endDateTime=2026-11-16T00", StringComparison.Ordinal));
        Assert.Contains(graph.Urls, u => u.Contains("receivedDateTime%20ge%202026-11-09T00:00:00Z", StringComparison.Ordinal));
        Assert.Contains(graph.Urls, u => u.Contains("sentDateTime%20ge%202026-11-09T00:00:00Z", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Urls, u => u.EndsWith(",body", StringComparison.Ordinal));   // headlines only, no bodies
    }

    [Fact]
    public void A_series_chat_adds_the_weeks_teams_messages()
    {
        using var f = Make(new DateTime(2026, 11, 13, 14, 0, 0), "chat = \"Team chat\"\n");
        var engine = new Engine(f.Config, new Recording(Block()), f.Log.Add, f.Host());
        var prompt = engine.PromptFor(new DateOnly(2026, 11, 13), "Weekly update");
        Assert.Contains("are at the end, then the Teams messages in their chat.", prompt);
        Assert.EndsWith("Teams messages this week (oldest first):\n(none)", prompt);
    }

    [Fact]
    public void Run_writes_it_when_due_and_still_after_the_block_has_started_that_day()
    {
        foreach (var (now, expected) in new[]
                 {
                     (new DateTime(2026, 11, 13, 13, 59, 0), false), (new DateTime(2026, 11, 13, 14, 0, 0), true),
                     (new DateTime(2026, 11, 13, 17, 5, 0), true),   // after the block: a usage-limit retry, say
                 })
        {
            using var f = Make(now);
            f.Always = Reply;
            f.Config.AfterWrite.Add("after");
            var engine = new Engine(f.Config, new Recording(Block()), f.Log.Add, f.Host());
            Assert.Equal(0, engine.Run());
            var written = Path.Combine(f.Dir, "work", "updates", "2026-11-16.md");
            Assert.Equal(expected, File.Exists(written));
            if (!expected)
                continue;
            Assert.StartsWith("# [DRAFT] Team\n\n*Drafted automatically.*", File.ReadAllText(written).ReplaceLineEndings("\n"));
            Assert.Contains("\"files\": [\"updates/2026-11-16.md\"]", f.Reports.Single());
            Assert.False(File.Exists(Path.Combine(f.Config.Output, "2026-11-16.md")));
        }
    }

    [Fact]
    public void The_next_day_it_is_left_alone()
    {
        using var f = Make(new DateTime(2026, 11, 14, 9, 0, 0));
        f.Always = Reply;
        var engine = new Engine(f.Config, new Recording(Block()), f.Log.Add, f.Host());
        Assert.DoesNotContain(engine.Upcoming(f.Clock.Now()), p => p.Series == "Weekly update");
    }

    [Fact]
    public void Every_transcript_updates_it_by_its_rules_even_after_the_block_started()
    {
        using var f = Make(new DateTime(2026, 11, 13, 16, 45, 0));
        var draft = Path.Combine(f.Dir, "work", "updates", "2026-11-16.md");
        File.WriteAllText(draft, "# [DRAFT] Team\n\n- **Thing** - happened\n\n---\n## For review\n- none\n");
        var transcript = Path.Combine(f.Dir, "work", "t.md");
        File.WriteAllText(transcript, "**Kim Lee** 0:01\nThe launch slipped.\n");
        f.Replies.Add("> **Update 16:45, from work t** *(automatic: check against the transcript)*\n> - **Launch slipped.** Add a bullet.");
        var engine = new Engine(f.Config, new Recording(Block()), f.Log.Add, f.Host());
        Assert.Equal(0, engine.Update([transcript]));
        var prompt = f.Prompts.Single();
        Assert.StartsWith("Alex has a draft weekly update, the team's section: updates/2026-11-16.md (for their \"Weekly update\" slot,", prompt);
        Assert.Contains("including anything they say about transcripts that land\nafter the draft is written.", prompt);
        Assert.EndsWith("Read updates/whole-team/2026-11-09.md. File it as updates/2026-11-16.md, for the week of 2026-11-16 (written 2026-11-13).", prompt);
        var text = File.ReadAllText(draft).ReplaceLineEndings("\n");
        Assert.True(text.IndexOf("Launch slipped", StringComparison.Ordinal) < text.IndexOf("## For review", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_out_puts_it_in_a_folder_of_your_choice_and_runs_no_hook()
    {
        using var f = Make(new DateTime(2026, 11, 13, 18, 30, 0));
        f.Always = Reply;
        f.Config.AfterWrite.Add("after");
        var existing = Path.Combine(f.Dir, "work", "updates", "2026-11-16.md");
        File.WriteAllText(existing, "today's draft, untouched");
        var scratch = Path.Combine(f.Dir, "scratch");
        var engine = new Engine(f.Config, new Recording(Block()), f.Log.Add, f.Host());
        var written = engine.WriteOne(new DateOnly(2026, 11, 13), "Weekly update", scratch);
        Assert.Equal([Path.Combine(scratch, "2026-11-16.md")], written);
        Assert.StartsWith("# [DRAFT] Team", File.ReadAllText(written[0]));
        Assert.Equal("today's draft, untouched", File.ReadAllText(existing));
        Assert.Empty(f.Reports);
        // Without --out, after_write runs as usual.
        engine.WriteOne(new DateOnly(2026, 11, 13), "Weekly update");
        Assert.Single(f.Reports);
    }

    [Fact]
    public void Write_out_from_the_command_line()
    {
        using var f = Make(new DateTime(2026, 11, 13, 18, 30, 0));
        f.Always = Reply;
        var scratch = Path.Combine(f.Dir, "scratch");
        var output = new StringWriter();
        var host = f.Host();
        var graph = new Recording(Block());
        var h = new PrepsHost
        {
            Out = output, Err = TextWriter.Null, Clock = host.Clock, Claude = host.Claude, Processes = host.Processes, Retry = host.Retry,
            Notify = host.Notify, Graph = (_, _) => graph, LockPrefix = "mp-test-", Env = _ => null,
        };
        var config = Path.Combine(f.Dir, "config.toml");
        Assert.Equal(0, Cli.Run(["--config", config, "write", "2026-11-13", "Weekly update", "--out", scratch], h));
        Assert.Equal(Path.Combine(scratch, "2026-11-16.md") + Environment.NewLine, output.ToString());
        Assert.False(File.Exists(Path.Combine(f.Dir, "work", "updates", "2026-11-16.md")));
        Assert.Equal(0, Cli.Run(["--config", config, "write", "2026-11-13", "Weekly update", $"--out={scratch}2"], h));
        Assert.True(File.Exists(Path.Combine(scratch + "2", "2026-11-16.md")));
        Assert.Equal(2, Cli.Run(["--config", config, "write", "2026-11-13", "Weekly update", "--out"], h));
        Assert.Equal(2, Cli.Run(["--config", config, "run", "--out", scratch], h));   // only write takes it
    }
}

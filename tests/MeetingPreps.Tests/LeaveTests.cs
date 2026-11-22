using System.Text.Json.Nodes;
using CalendarDays;
using MeetingPreps.Parity;

namespace MeetingPreps.Tests;

/// <summary>The leave documents (1.7.0): a return pack on the first working day back and a before-you-go pack on the
/// last working day before, for your own leave of at least [leave] min_working_days, found with background-tools-lib's
/// CalendarDays day rules. The calendars are made up: leave from Wed 16 Dec to Thu 31 Dec 2026, bank holidays on 25 and
/// 28 Dec and 1 Jan, back Mon 4 Jan 2027.</summary>
public class LeaveTests
{
    private const string Settings = """

        [leave]
        min_working_days = 5

        [kind.re-entry]
        describe = "re-entry pack, for the first morning back"
        rules = "Read reviews/weekly/. Back {return_day}, away from {first_day} to {last_day} ({weekdays} days). File as {file}."
        document = true
        context_commands = [["tool", "due", "--days", "{days_away}"]]

        [kind.before]
        describe = "before-you-go pack"
        rules = "List what happens while away from {first_day}; written {date}."
        document = true

        [series."Re-entry pack"]
        kind = "re-entry"
        leave = "return"
        folder = "reviews/re-entry"
        file = "{return_day}.md"

        [series."Before you go"]
        kind = "before"
        leave = "before"
        folder = "reviews/re-entry"
        file = "{first_day}-before-you-go.md"
        due_same_day_at = "08:30"
        """;

    private const string Pack = "Here.\n# [DRAFT] Re-entry\n\n*Written automatically.*\n\n## Still in place\n- fine";

    private static Fixture Make(DateTime now, string settings = Settings) => new(Fixture.Settings + settings, now);

    private static JsonObject AllDay(string subject, string first, string afterLast, params string[] categories)
    {
        var e = Fixture.Event(subject, first + "T00:00", afterLast + "T00:00");
        e["isAllDay"] = true;
        e["showAs"] = "oof";
        e["categories"] = new JsonArray(categories.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        return e;
    }

    /// <summary>People First's leave entries a week at a time, and calendar-bank-holidays' entries.</summary>
    private static JsonObject[] Christmas() =>
    [
        AllDay("Holiday (Principal Solutions Architect)", "2026-12-16", "2026-12-19"),
        AllDay("Holiday (Principal Solutions Architect)", "2026-12-21", "2026-12-25"),
        AllDay("Christmas Day", "2026-12-25", "2026-12-26", "Bank holiday"),
        AllDay("Boxing Day (substitute day)", "2026-12-28", "2026-12-29", "Bank holiday"),
        AllDay("Holiday (Principal Solutions Architect)", "2026-12-29", "2027-01-01"),
        AllDay("New Year's Day", "2027-01-01", "2027-01-02", "Bank holiday"),
    ];

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

    private static List<DayEvent> Days(Fixture f, IEnumerable<JsonObject> events) =>
        events.Select(e => Leave.ToDayEvent(f.Config, e)).OfType<DayEvent>().ToList();

    [Fact]
    public void The_settings_are_read_and_checked()
    {
        using var f = Make(new DateTime(2026, 12, 15, 9, 0, 0));
        Assert.Equal(5, f.Config.LeaveSettings.MinWorkingDays);
        Assert.Equal(42, f.Config.LeaveSettings.WindowDays);
        Assert.Equal(["Bank holiday"], f.Config.LeaveSettings.BankHolidayCategories);
        Assert.Equal("return", f.Config.Series("Re-entry pack")!.Leave);
        Assert.Equal("before", f.Config.Series("Before you go")!.Leave);
        Assert.Null(f.Config.Series("Leads weekly")!.Leave);

        void Refused(string extra, string says)
        {
            using var g = new Fixture();
            var bad = Path.Combine(g.Dir, "bad.toml");
            File.WriteAllText(bad, "me = \"a\"\nemail = \"a@b\"\n[kind.k]\ndescribe = \"d\"\n" + extra);
            Assert.Contains(says, Assert.Throws<SettingsException>(() => Config.Load(bad)).Message);
        }
        Refused("[series.\"S\"]\nkind = \"k\"\nfile = \"{return_day}.md\"\nleave = \"after\"\n", "leave should be \"return\"");
        Refused("[series.\"S\"]\nkind = \"k\"\nfile = \"{return_day}.md\"\n", "needs a file name with {date}");   // only a leave series has these
        Refused("[series.\"S\"]\nkind = \"k\"\nfile = \"{date}.md\"\nleave = \"return\"\nweekly = [{day = \"Fri\", start = \"15:00\", end = \"16:00\"}]\n",
            "weekly, own_block, absorbs");
        Refused("[person.\"P\"]\nkind = \"k\"\nfile = \"{date}.md\"\nleave = \"return\"\n", "leave is for a series");
        Refused("[leave]\nmin_working_days = 0\n", "[leave] min_working_days");
        Refused("[leave]\nsubjects = \"(\"\n", "isn't a valid regular expression");
        Refused("[leave]\nthreshold = 3\n", "unknown setting 'threshold'");
    }

    [Fact]
    public void A_base_files_leave_supplies_the_shared_keys_and_this_tools_own_stay_here()
    {
        using var g = new Fixture();
        File.WriteAllText(Path.Combine(g.Dir, "base.toml"),
            "[leave]\nsubjects = '^\\s*vacation\\b'\nmin_hours = 4\nhalf_day = '\\(half\\)'\nbank_holiday_categories = [\"BH\"]\n");
        var path = Path.Combine(g.Dir, "with-base.toml");
        File.WriteAllText(path, "base = \"base.toml\"\nme = \"a\"\nemail = \"a@b\"\n[kind.k]\ndescribe = \"d\"\n[leave]\nmin_working_days = 3\n");
        var leave = Config.Load(path).LeaveSettings;
        Assert.Equal(3, leave.MinWorkingDays);
        Assert.Matches(leave.Rules.Subjects, "Vacation in Spain");
        Assert.DoesNotMatch(leave.Rules.Subjects, "Holiday (Example Role)");
        Assert.Equal(4, leave.Rules.MinHours);
        Assert.Matches(leave.HalfDay, "Vacation (half)");
        Assert.Equal(["BH"], leave.BankHolidayCategories);
        Assert.Matches(leave.BankHolidaySubjects, "Bank holiday");   // not in the base: the shared default
    }

    [Fact]
    public void Christmas_is_one_period_with_the_bank_holidays_joined()
    {
        using var f = Make(new DateTime(2026, 12, 1, 9, 0, 0));
        var periods = Leave.Periods(f.Config.LeaveSettings, Days(f, Christmas()), new DateOnly(2026, 11, 20), new DateOnly(2027, 2, 1));
        var p = Assert.Single(periods);
        Assert.Equal((new DateOnly(2026, 12, 16), new DateOnly(2027, 1, 1)), (p.First, p.Last));
        Assert.Equal(new DateOnly(2026, 12, 15), p.BeforeDay);
        Assert.Equal(new DateOnly(2027, 1, 4), p.ReturnDay);
        Assert.Equal(13, p.Weekdays);   // 16 Dec to 1 Jan: 10 days' leave and 3 bank holidays
        Assert.Equal(20, p.DaysAway);
        Assert.False(p.OpenStart || p.OpenEnd);
    }

    [Fact]
    public void Short_leave_others_leave_and_a_bank_holiday_alone_are_not_periods()
    {
        using var f = Make(new DateTime(2026, 12, 1, 9, 0, 0));
        var settings = f.Config.LeaveSettings;
        DateOnly from = new(2026, 11, 1), to = new(2026, 12, 1);
        // Three days, then three and a half (a half day and three): under five.
        Assert.Empty(Leave.Periods(settings, Days(f, [AllDay("Holiday", "2026-11-04", "2026-11-07")]), from, to));
        var half = Fixture.Event("Holiday (half day) (Architect)", "2026-11-09T13:00", "2026-11-09T17:30");
        Assert.Empty(Leave.Periods(settings, Days(f, [half, AllDay("Annual leave", "2026-11-10", "2026-11-13")]), from, to));
        // With a bank holiday joined it's five: the half day at the start is the day it's written.
        var five = Leave.Periods(settings, Days(f, [half, AllDay("Annual leave", "2026-11-10", "2026-11-13"),
            AllDay("Bank holiday", "2026-11-13", "2026-11-14", "Bank holiday"), AllDay("Holiday", "2026-11-16", "2026-11-17")]), from, to);
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 17), 5.5), (five.Single().BeforeDay, five.Single().ReturnDay, five.Single().Weekdays));
        // Someone else's leave (Kim's entry, or one with people invited) isn't yours; a bank holiday alone isn't leave.
        var kims = AllDay("Holiday", "2026-11-02", "2026-11-14");
        kims["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Kim Lee", ["address"] = "kim@example.com" } };
        var invited = Fixture.Event("Holiday handover", "2026-11-16T00:00", "2026-11-28T00:00", "Sam Jones");
        Assert.Empty(Leave.Periods(settings, Days(f, [kims, invited, AllDay("Bank holiday", "2026-11-20", "2026-11-21", "Bank holiday")]), from, to));
        // Cancelled: not there.
        var cancelled = AllDay("Holiday", "2026-11-02", "2026-11-14");
        cancelled["isCancelled"] = true;
        Assert.Empty(Leave.Periods(settings, Days(f, [cancelled]), from, to));
    }

    [Fact]
    public void Pretend_leave_counts_as_leave()
    {
        using var f = Make(new DateTime(2026, 11, 20, 9, 0, 0));
        var days = Enumerable.Range(9, 5).Select(d => new DateOnly(2026, 11, d)).ToList();
        var p = Leave.Periods(f.Config.LeaveSettings, [], new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 1), days).Single();
        Assert.Equal((new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 16)),
            (p.BeforeDay, p.First, p.Last, p.ReturnDay));
        Assert.Equal(new DateOnly(2026, 11, 13), Assert.Single(Cli.PretendDays("2026-11-13")));
        Assert.Equal(5, Cli.PretendDays("2026-11-09..2026-11-13").Count);
    }

    [Fact]
    public void The_plan_has_each_pack_on_its_day()
    {
        using var f = Make(new DateTime(2026, 12, 14, 9, 0, 0));
        var engine = new Engine(f.Config, new Recording(Christmas()), f.Log.Add, f.Host());
        var before = engine.Plans(new DateOnly(2026, 12, 14), 5).Single(p => p.Series == "Before you go");
        Assert.Equal(("2026-12-15T08:30", "2026-12-16-before-you-go.md", "before"), (before.Due, before.File, before.Kind));
        Assert.Equal(Path.Combine(f.Dir, "work", "reviews", "re-entry", "2026-12-16-before-you-go.md"), engine.Target(before));
        Assert.DoesNotContain(engine.Plans(new DateOnly(2026, 12, 14), 5), p => p.Series == "Re-entry pack");

        var back = engine.Plans(new DateOnly(2027, 1, 4), 1).Single();
        Assert.Equal(("Re-entry pack", "2027-01-04T00:00", "2027-01-04.md"), (back.Series, back.Due, back.File));
        Assert.Equal(13, back.Leave!.Weekdays);
        var shown = Planner.Show(engine.PlanRows(new DateOnly(2027, 1, 4), 1), new DateOnly(2027, 1, 4), 1);
        Assert.Contains("written Mon 04 Jan 00:00 -> 2027-01-04.md [leave Wed 16 Dec to Fri 01 Jan, 13 weekdays, back Mon 04 Jan]", shown);
        // Without the leave series, nothing new, and no wider read.
        using var plain = new Fixture(now: new DateTime(2026, 12, 14, 9, 0, 0));
        var graph = new Recording(Christmas());
        Assert.Empty(new Engine(plain.Config, graph, plain.Log.Add, plain.Host()).Plans(new DateOnly(2027, 1, 4), 1));
        Assert.DoesNotContain(graph.Urls, u => u.Contains("categories", StringComparison.Ordinal));
    }

    [Fact]
    public void Under_the_threshold_there_is_no_pack()
    {
        using var f = Make(new DateTime(2026, 12, 14, 9, 0, 0), Settings.Replace("min_working_days = 5", "min_working_days = 14"));
        var engine = new Engine(f.Config, new Recording(Christmas()), f.Log.Add, f.Host());
        Assert.Empty(engine.Plans(new DateOnly(2027, 1, 4), 1));
        Assert.DoesNotContain(engine.Plans(new DateOnly(2026, 12, 15), 1), p => p.Leave is not null);
    }

    [Fact]
    public void The_first_run_back_writes_the_pack_first_unless_a_meeting_is_within_the_hour()
    {
        foreach (var (now, first) in new[]
                 {
                     (new DateTime(2027, 1, 4, 7, 0, 0), "Re-entry pack"),     // Sam at 08:30: not within the hour
                     (new DateTime(2027, 1, 4, 7, 45, 0), "Sam 1:1"),           // within the hour: the prep first
                 })
        {
            using var f = Make(now);
            f.Always = Pack;
            var events = Christmas().Append(Fixture.Event("Sam 1:1", "2027-01-04T08:30", "2027-01-04T09:00", "Sam Jones")).ToArray();
            var engine = new Engine(f.Config, new Recording(events), f.Log.Add, f.Host());
            Assert.Equal(0, engine.Run());
            Assert.Equal(2, f.Prompts.Count);
            Assert.StartsWith(first == "Sam 1:1" ? "Write Alex's prep" : "Write Alex's re-entry pack", f.Prompts[0]);
            Assert.True(File.Exists(Path.Combine(f.Dir, "work", "reviews", "re-entry", "2027-01-04.md")));
            // Written once: later that day it's there, and still in the day's plan.
            Assert.Contains(engine.Upcoming(now.AddHours(8)), p => p.Series == "Re-entry pack");
            Assert.Equal(0, new Engine(f.Config, new Recording(events), f.Log.Add, f.Host()).Run());
            Assert.Equal(2, f.Prompts.Count);
        }
    }

    [Fact]
    public void Before_its_time_on_the_last_day_nothing_is_written()
    {
        using var f = Make(new DateTime(2026, 12, 15, 8, 0, 0));
        f.Always = Pack;
        Assert.Equal(0, new Engine(f.Config, new Recording(Christmas()), f.Log.Add, f.Host()).Run());
        // (Tuesday's "Leads weekly" slot, a series in every test's settings, is due too: it's left out here.)
        Assert.DoesNotContain(f.Prompts, p => p.StartsWith("Write Alex's before-you-go", StringComparison.Ordinal));
        using var g = Make(new DateTime(2026, 12, 15, 8, 30, 0));
        g.Always = Pack;
        Assert.Equal(0, new Engine(g.Config, new Recording(Christmas()), g.Log.Add, g.Host()).Run());
        Assert.StartsWith("Write Alex's before-you-go pack.", g.Prompts[0]);   // first, before the Leads prep
        Assert.True(File.Exists(Path.Combine(g.Dir, "work", "reviews", "re-entry", "2026-12-16-before-you-go.md")));
    }

    [Fact]
    public void The_return_prompt_has_the_period_the_first_week_and_the_mail_since()
    {
        using var f = Make(new DateTime(2027, 1, 4, 7, 0, 0));
        var events = Christmas().Append(Fixture.Event("Sam 1:1", "2027-01-05T10:00", "2027-01-05T10:30", "Sam Jones"))
            .Append(AllDay("Sam Jones annual leave", "2027-01-06", "2027-01-07")).ToArray();
        var graph = new Recording(events);
        var engine = new Engine(f.Config, graph, f.Log.Add, f.Host());
        var prompt = engine.PromptFor(new DateOnly(2027, 1, 4), "Re-entry pack");
        Assert.StartsWith("Write Alex's re-entry pack, for the first morning back. It is now Monday 04 January 2027 07:00.\n"
                          + "Alex is on leave from Wed 16 Dec 2026 to Fri 01 Jan 2027 (13 working days away, bank holidays included), back on Mon 04 Jan 2027.\n"
                          + "This is for the first working day back, Mon 04 Jan 2027. The before-you-go pack for this leave, if one was written: "
                          + "reviews/re-entry/2026-12-16-before-you-go.md. It is filed as reviews/re-entry/2027-01-04.md.", prompt);
        Assert.Contains("Read reviews/weekly/. Back 2027-01-04, away from 2026-12-16 to 2027-01-01 (13 days). File as reviews/re-entry/2027-01-04.md.", prompt);
        // The context command got the period, and its output is headed.
        Assert.Contains("Diary (Mon 04 Jan 2027 to Sun 10 Jan 2027):\n- Tue 05 Jan 10:00–10:30  Sam 1:1  (with Sam Jones)\n- Wed 06 Jan, all day  Sam Jones annual leave  [shown as oof]", prompt);
        Assert.Contains("Email (since Tue 15 Dec 2026, oldest first):\n(none)", prompt);
        Assert.Contains(graph.Urls, u => u.Contains("receivedDateTime%20ge%202026-12-15T00:00:00Z", StringComparison.Ordinal));
        Assert.DoesNotContain("Must hold", prompt);
    }

    [Fact]
    public void Context_commands_get_the_period()
    {
        using var f = Make(new DateTime(2027, 1, 4, 7, 0, 0));
        var requests = new List<ProcessRequest>();
        var host = f.Host();
        var h = new PrepsHost
        {
            Clock = host.Clock, Claude = host.Claude, Retry = host.Retry, Notify = host.Notify, LockPrefix = "mp-test-",
            Processes = new Capture(requests), Out = TextWriter.Null, Err = TextWriter.Null,
        };
        var prompt = new Engine(f.Config, new Recording(Christmas()), f.Log.Add, h).PromptFor(new DateOnly(2027, 1, 4), "Re-entry pack");
        var request = requests.Single();
        Assert.Equal(["tool", "due", "--days", "20"], request.Argv);
        Assert.Equal("2026-12-16", request.ExtraEnv["MEETING_PREPS_FIRST_DAY"]);
        Assert.Equal("2027-01-04", request.ExtraEnv["MEETING_PREPS_RETURN_DAY"]);
        Assert.Contains("Output of `tool due --days 20`:\n2 due", prompt);
    }

    private sealed class Capture(List<ProcessRequest> requests) : IProcessRunner
    {
        public WinBackground.Completed Run(ProcessRequest request)
        {
            requests.Add(request);
            return new WinBackground.Completed(0, "2 due\n", "");
        }
    }

    [Fact]
    public void The_before_prompt_runs_through_the_leave_and_the_week_after()
    {
        using var f = Make(new DateTime(2026, 12, 15, 9, 0, 0));
        var declined = Fixture.Event("Team planning", "2026-12-17T10:00", "2026-12-17T11:00", "Sam Jones", "Kim Lee");
        declined["responseStatus"] = new JsonObject { ["response"] = "declined" };
        var engine = new Engine(f.Config, new Recording(Christmas().Append(declined).ToArray()), f.Log.Add, f.Host());
        var prompt = engine.PromptFor(new DateOnly(2026, 12, 15), "Before you go");
        Assert.Contains("This is written on the last working day before it, Tue 15 Dec 2026. The return pack will be reviews/re-entry/2027-01-04.md, "
                        + "written on Mon 04 Jan 2027.", prompt);
        Assert.Contains("List what happens while away from 2026-12-16; written 2026-12-15.", prompt);
        Assert.Contains("Diary (Tue 15 Dec 2026 to Sun 10 Jan 2027):", prompt);
        Assert.Contains("- Wed 16 Dec to Fri 18 Dec, all day  Holiday (Principal Solutions Architect)  [shown as oof]", prompt);
        Assert.Contains("- Thu 17 Dec 10:00–11:00  Team planning  (with Sam Jones, Kim Lee)  [declined]", prompt);
        Assert.Contains("Email (the last 14 days, oldest first):", prompt);
    }

    [Fact]
    public void The_wider_read_is_kept_for_an_hour()
    {
        using var f = Make(new DateTime(2026, 12, 14, 9, 0, 0));
        var graph = new Recording(Christmas());
        new Engine(f.Config, graph, f.Log.Add, f.Host()).Upcoming(f.Clock.Now());
        new Engine(f.Config, graph, f.Log.Add, f.Host()).Upcoming(f.Clock.Now());
        Assert.Single(graph.Urls, u => u.Contains("categories", StringComparison.Ordinal));
        f.Clock = Clock.Fixed(new DateTime(2026, 12, 14, 10, 1, 0), ScenarioRunner.London);
        new Engine(f.Config, graph, f.Log.Add, f.Host()).Upcoming(f.Clock.Now());
        Assert.Equal(2, graph.Urls.Count(u => u.Contains("categories", StringComparison.Ordinal)));
    }

    [Fact]
    public void Write_by_hand_with_pretend_leave_into_a_scratch_folder()
    {
        using var f = Make(new DateTime(2026, 11, 20, 18, 0, 0));
        f.Always = Pack;
        var scratch = Path.Combine(f.Dir, "scratch");
        var output = new StringWriter();
        var host = f.Host();
        var graph = new Recording();
        var h = new PrepsHost
        {
            Out = output, Err = TextWriter.Null, Clock = host.Clock, Claude = host.Claude, Processes = host.Processes, Retry = host.Retry,
            Notify = host.Notify, Graph = (_, _) => graph, LockPrefix = "mp-test-", Env = _ => null,
        };
        var config = Path.Combine(f.Dir, "config.toml");
        Assert.Equal(0, Cli.Run(["--config", config, "write", "2026-11-16", "Re-entry pack", "--leave", "2026-11-09..2026-11-13", "--out", scratch], h));
        Assert.Equal(Path.Combine(scratch, "2026-11-16.md") + Environment.NewLine, output.ToString());
        Assert.Equal(0, Cli.Run(["--config", config, "write", "2026-11-06", "Before you go", "--leave=2026-11-09..2026-11-13", "--out", scratch], h));
        Assert.True(File.Exists(Path.Combine(scratch, "2026-11-09-before-you-go.md")));
        Assert.False(Directory.Exists(Path.Combine(f.Dir, "work", "reviews")));
        // Pretend leave is never kept as the real thing.
        Assert.False(File.Exists(f.Config.StateFile) && File.ReadAllText(f.Config.StateFile).Contains("\"leave\"", StringComparison.Ordinal));
        // Without the pretend leave there's nothing to write; a bad range is a usage error.
        Assert.Equal(2, Cli.Run(["--config", config, "write", "2026-11-16", "Re-entry pack", "--out", scratch], h));
        Assert.Equal(2, Cli.Run(["--config", config, "write", "2026-11-16", "Re-entry pack", "--leave", "2026-11-13..2026-11-09"], h));
        Assert.Equal(2, Cli.Run(["--config", config, "run", "--leave", "2026-11-09"], h));   // plan, write and prompt only
    }
}

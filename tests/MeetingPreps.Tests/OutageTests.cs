using System.Net;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace MeetingPreps.Tests;

/// <summary>Graph with an outage you choose: down is a set of "calendar", "email" and "Teams". Every call is recorded
/// by label. (The Python's FakeGraph in tests/test_preps.py.)</summary>
internal sealed class OutageGraph(IEnumerable<JsonObject>? events = null, params string[] down) : IGraph
{
    public List<JsonObject> Events { get; } = events?.ToList() ?? [];
    public HashSet<string> Down { get; } = [.. down];
    public List<string> Calls { get; } = [];

    /// <summary>Inbox messages to serve (the Python test patched context.mail instead).</summary>
    public List<JsonObject> Inbox { get; } = [];

    private void Call(string url)
    {
        var label = url.Contains("calendarView", StringComparison.Ordinal) ? "calendar"
            : url.Contains("chats", StringComparison.Ordinal) ? "Teams" : "email";
        Calls.Add(label);
        if (Down.Contains(label))
            throw new GraphRequestException(HttpStatusCode.BadGateway, "GET", url, "");
    }

    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        Call(url);
        if (url.Contains("calendarView", StringComparison.Ordinal))
            return Events.Select(e => (JsonNode?)e.DeepClone()).ToList();
        if (url.StartsWith("/me/messages", StringComparison.Ordinal))
            return Inbox.Select(e => (JsonNode?)e.DeepClone()).ToList();
        return [];
    }

    public JsonObject Get(string url)
    {
        Call(url);
        return url.StartsWith("/me/mailFolders/", StringComparison.Ordinal)
            ? new JsonObject { ["displayName"] = "Inbox" }
            : new JsonObject { ["value"] = new JsonArray() };
    }
}

/// <summary>A Microsoft 365 outage (13 Nov 2026): preps are still written, say what they lacked, and it's notified
/// once. The Python's class Outage in tests/test_preps.py, test by test.</summary>
public class Outage
{
    private const string AutoReply = "# [DRAFT] Sam Jones 1:1\n*Written automatically by meeting-preps.*\nSources: notes.\n\n## Items\n- one";

    private static Fixture Make()
    {
        var f = new Fixture();
        f.Always = AutoReply;
        return f;
    }

    /// <summary>1:1s due now: an hour and a half ahead, so due already and outside the pre-meeting refresh.</summary>
    private static List<JsonObject> Meetings(Fixture f, params string[] names)
    {
        var now = f.Clock.Now();
        return names.Select((name, i) =>
        {
            var begins = now.AddMinutes(90 + 5 * i);
            begins = new DateTime(begins.Year, begins.Month, begins.Day, begins.Hour, begins.Minute, 0);
            return Fixture.Event($"{name} 1:1", PyTime.Iso(begins, "minutes"), PyTime.Iso(begins.AddMinutes(30), "minutes"), name);
        }).ToList();
    }

    private static Engine Engine(Fixture f, IGraph graph) => new(f.Config, graph, f.Log.Add, f.Host());

    /// <summary>A good calendar read, kept as the copy an outage falls back to.</summary>
    private static void KeepCalendar(Fixture f, List<JsonObject> events, DateTime? read = null)
    {
        Engine(f, new OutageGraph(events)).Plans(DateOnly.FromDateTime(f.Clock.Now()), 2);
        if (read is { } when)
        {
            var data = (JsonObject)PyJson.Parse(File.ReadAllText(f.Config.CalendarCache))!;
            data["read"] = PyTime.Iso(when, "seconds");
            File.WriteAllText(f.Config.CalendarCache, PyJson.Dump(data));
        }
    }

    private static string? Prep(Fixture f, string name = "sam-jones")
    {
        var found = Directory.EnumerateFiles(f.Config.Output, $"*-{name}-prep.md").FirstOrDefault();
        return found is null ? null : PyStr.ReadText(found);
    }

    private static List<string> Titles(Fixture f) => f.Notes.Select(n => n.Title).ToList();

    private static JsonObject? OutageState(Fixture f) =>
        File.Exists(f.Config.StateFile) ? PyJson.Parse(File.ReadAllText(f.Config.StateFile))!["outage"] as JsonObject : null;

    [Fact]
    public void test_calendar_down_plans_from_the_copy_and_says_so()
    {
        using var f = Make();
        var events = Meetings(f, "Sam Jones");
        KeepCalendar(f, events);
        var graph = new OutageGraph(events, "calendar");
        Assert.Equal(0, Engine(f, graph).Run());
        var text = Prep(f)!;
        Assert.Contains("Written from the calendar as read", text);
        Assert.Contains("HTTP 502 Bad Gateway", text);
        var lines = PyStr.SplitLines(text);
        Assert.True(lines[0].StartsWith("# [DRAFT]", StringComparison.Ordinal) && lines[1].StartsWith("*Written automatically", StringComparison.Ordinal));
        Assert.StartsWith("> **Not everything could be read:**", lines[3]);   // under the title, above Sources
        Assert.Contains("Some sources couldn't be read", f.Prompts[^1]);
        Assert.Equal(1, graph.Calls.Count(c => c == "calendar"));   // not asked again for the diary
        Assert.Equal(["Microsoft 365 unavailable"], Titles(f));   // a degraded prep: notified at once, once
    }

    [Fact]
    public void test_calendar_down_with_no_copy_or_an_old_one_stops_the_run()
    {
        using var f = Make();
        var graph = new OutageGraph(Meetings(f, "Sam Jones"), "calendar");
        Assert.Contains("no copy", Assert.Throws<CalendarUnavailableException>(() => Engine(f, graph).Run()).Message);
        KeepCalendar(f, graph.Events, read: f.Clock.Now().AddDays(-4));
        Assert.Contains("over 3 days old", Assert.Throws<CalendarUnavailableException>(() => Engine(f, graph).Run()).Message);
        Assert.Null(Prep(f));
        Assert.Empty(Titles(f));   // the watcher's "couldn't run" is the notice for this
        Assert.Equal(2, (int)OutageState(f)!["runs"]!);
    }

    [Fact]
    public void test_the_copy_covers_only_the_days_it_read()
    {
        using var f = Make();
        KeepCalendar(f, []);
        var data = (JsonObject)PyJson.Parse(File.ReadAllText(f.Config.CalendarCache))!;
        data["days"] = 1;
        File.WriteAllText(f.Config.CalendarCache, PyJson.Dump(data));
        var today = DateOnly.FromDateTime(f.Clock.Now());
        var (_, start, days) = Engine(f, new OutageGraph(down: "calendar")).Calendar(today, 5);
        Assert.Equal((today, 1), (start, days));
    }

    [Fact]
    public void test_email_or_teams_down_alone()
    {
        using var f = Make();
        foreach (var label in new[] { "email", "Teams" })
        {
            foreach (var old in Directory.EnumerateFiles(f.Config.Output, "*.md"))
                File.Delete(old);
            var graph = new OutageGraph(Meetings(f, "Sam Jones"), label);
            Assert.Equal(0, Engine(f, graph).Run());
            var text = Prep(f)!;
            Assert.Contains($"Microsoft 365 unavailable (HTTP 502 Bad Gateway): {label} not read.", text);
            Assert.DoesNotContain("Written from the calendar", text);
            Assert.DoesNotContain("Prep failed", Titles(f));
        }
    }

    [Fact]
    public void test_diary_down_with_no_copy_still_writes_the_prep()
    {
        using var f = Make();
        var written = Engine(f, new OutageGraph(down: "calendar")).Write(f.Plan());
        Assert.Contains("Diary not read (HTTP 502 Bad Gateway).", PyStr.ReadText(written[0]));
        Assert.Contains("Diary:\n(unavailable: HTTP 502 Bad Gateway)", f.Prompts[^1]);
    }

    [Fact]
    public void test_everything_down_asks_each_source_once_a_run()
    {
        using var f = Make();
        var events = Meetings(f, "Sam Jones", "Pat Boss");
        KeepCalendar(f, events);
        var graph = new OutageGraph(events, "calendar", "email", "Teams");
        var engine = Engine(f, graph);
        Assert.Equal(0, engine.Run());
        Assert.Equal(2, f.Prompts.Count);
        Assert.Equal((1, 1, 1), (graph.Calls.Count(c => c == "email"), graph.Calls.Count(c => c == "Teams"), graph.Calls.Count(c => c == "calendar")));
        var text = Prep(f)!;
        Assert.Contains("email and Teams not read", text);
        Assert.Contains("Written from the calendar as read", text);
        Assert.Equal(2, engine.Degraded);
        Assert.Equal(["Microsoft 365 unavailable"], Titles(f));   // one for the outage, not one per meeting
    }

    [Fact]
    public void test_a_partial_outage_and_its_end()
    {
        using var f = Make();
        var events = Meetings(f, "Sam Jones");
        Assert.Equal(0, Engine(f, new OutageGraph(events, "email")).Run());
        Assert.True(File.Exists(f.Config.CalendarCache));   // the calendar worked, so it was kept
        Assert.Equal(["Microsoft 365 unavailable"], Titles(f));
        Engine(f, new OutageGraph(events)).Run();   // prep exists, so email isn't read: not yet known to be back
        Assert.Equal(["email"], ((JsonArray)OutageState(f)!["what"]!).Select(w => (string)w!).ToArray());
        var engine = Engine(f, new OutageGraph(events));
        engine.Read("email", () => "(none)", new OrderedDictionary<string, string>());
        engine.SettleOutage();
        Assert.Null(OutageState(f));
        Assert.Contains(f.Log, line => line.Contains("back", StringComparison.Ordinal));
    }

    [Fact]
    public void test_notified_on_the_third_run_then_not_again_until_a_new_outage()
    {
        using var f = Make();
        KeepCalendar(f, []);
        var down = new OutageGraph(down: "calendar");
        for (var i = 0; i < 4; i++)
            Engine(f, down).Run();
        Assert.Equal(["Microsoft 365 unavailable"], Titles(f));
        Assert.Contains("cached calendar", f.Notes[^1].Lines[0]);
        Engine(f, new OutageGraph()).Run();   // everything read: the outage is over
        for (var i = 0; i < 2; i++)
            Engine(f, down).Run();
        Assert.Single(Titles(f));
        Engine(f, down).Run();
        Assert.Equal(2, Titles(f).Count);
    }

    [Fact]
    public void test_a_real_failure_is_still_notified_per_prep()
    {
        using var f = Make();
        f.Failure = new InvalidOperationException("Claude fell over");
        Assert.Equal(1, Engine(f, new OutageGraph(Meetings(f, "Sam Jones"))).Run());
        Assert.Equal(["Prep failed"], Titles(f));
    }

    [Fact]
    public void test_refresh_waits_while_email_is_down()
    {
        using var f = Make();
        var soon = f.Clock.Now().AddMinutes(30);
        var meeting = Fixture.Event("Sam 1:1", PyTime.Iso(soon, "minutes"), PyTime.Iso(DateOnly.FromDateTime(soon)) + "T23:59", "Sam Jones");
        var prep = Path.Combine(f.Config.Output, PyTime.Iso(DateOnly.FromDateTime(soon)) + "-sam-jones-prep.md");
        File.WriteAllText(prep, "# [DRAFT] Sam\n");
        Engine(f, new OutageGraph([meeting], "email")).Refresh();
        Assert.Empty(f.Prompts);
        Assert.Null(File.Exists(f.Config.StateFile) ? PyJson.Parse(File.ReadAllText(f.Config.StateFile))!["refreshed"] : null);
        var graph = new OutageGraph([meeting]);
        graph.Inbox.Add(new JsonObject
        {
            ["subject"] = "An email", ["bodyPreview"] = "news", ["receivedDateTime"] = "2026-11-11T08:05:00Z", ["parentFolderId"] = "i",
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Sam Jones", ["address"] = "sam@example.com" } },
            ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Alex Smith", ["address"] = "alex@example.com" } }),
        });
        Engine(f, graph).Refresh();
        Assert.NotEmpty(f.Prompts);
    }
}

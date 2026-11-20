using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CalendarDays;
using PyCompat;

namespace MeetingPreps;

/// <summary>[leave]: what counts as your own leave, for the documents written around it (a series with `leave`). The
/// day rules are background-tools-lib's CalendarDays, as out-of-office uses them, so both agree on the days away and
/// the day you're back. (Skipping preps on leave days still uses leave_pattern, as it always has.)</summary>
public sealed class LeaveSettings
{
    public const string DefaultSubjects = @"^\s*(holiday\b|annual leave|on leave|summer holidays?)";
    public const string DefaultHalfDay = @"\(half day\)";
    public const string DefaultBankHoliday = @"^\s*bank holiday\b";

    /// <summary>Weekdays away (joined bank holidays counted, half days 0.5) before a leave document is written.</summary>
    public double MinWorkingDays { get; init; } = 5;

    /// <summary>Your own entries (no one else invited) matching subjects or carrying a category, covering min_hours of the
    /// day or all of it.</summary>
    public LeaveRules Rules { get; init; } = new(Pattern(DefaultSubjects), [], 6);

    /// <summary>A leave entry matching this is a half day (as is one shorter than min_hours).</summary>
    public Regex HalfDay { get; init; } = Pattern(DefaultHalfDay);

    public Regex BankHolidaySubjects { get; init; } = Pattern(DefaultBankHoliday);
    public IReadOnlyList<string> BankHolidayCategories { get; init; } = ["Bank holiday"];

    /// <summary>How far either side of the days planned the calendar is read for leave.</summary>
    public int WindowDays { get; init; } = 42;

    internal static Regex Pattern(string text) =>
        new(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>A run of days away holding leave: its first and last weekdays away, the weekdays it counts, the last working
/// day before it (written on) and the day you're back. OpenStart/OpenEnd: the run reached the edge of what was read, so
/// that side isn't known.</summary>
public sealed record LeavePeriod(DateOnly First, DateOnly Last, DateOnly BeforeDay, DateOnly ReturnDay, double Weekdays,
    bool OpenStart = false, bool OpenEnd = false)
{
    /// <summary>Calendar days from the day before to the day back: how far ahead "due while you're away" reaches.</summary>
    public int DaysAway => ReturnDay.DayNumber - BeforeDay.DayNumber;

    public JsonObject Json() => new()
    {
        ["first"] = PyTime.Iso(First), ["last"] = PyTime.Iso(Last), ["before"] = PyTime.Iso(BeforeDay), ["return"] = PyTime.Iso(ReturnDay),
        ["weekdays"] = JsonValue.Create(Weekdays), ["open_start"] = OpenStart, ["open_end"] = OpenEnd,
    };

    public static LeavePeriod FromJson(JsonObject o) => new(Day(o["first"]), Day(o["last"]), Day(o["before"]), Day(o["return"]),
        (double)o["weekdays"]!, (bool?)o["open_start"] ?? false, (bool?)o["open_end"] ?? false);

    private static DateOnly Day(JsonNode? node) => DateOnly.ParseExact((string)node!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public static class Leave
{
    private enum Kind { Working, Weekend, BankHoliday, Leave, HalfDay }

    /// <summary>A calendar event as the day rules see it; null for one cancelled or declined.</summary>
    public static DayEvent? ToDayEvent(Config config, JsonObject e)
    {
        if (Py.Truthy(e["isCancelled"]) || (e["responseStatus"] is JsonObject r && r["response"] is JsonValue v && Py.Str(v) == "declined"))
            return null;
        var start = PyTime.FromIso(PyStr.Head(Py.Str(e["start"]!["dateTime"]), 19));
        var end = PyTime.FromIso(PyStr.Head(Py.Str(e["end"]!["dateTime"]), 19));
        var attendees = 0;
        foreach (var a in e["attendees"] as JsonArray ?? [])
        {
            var address = a?["emailAddress"] as JsonObject;
            var name = address?["name"] is JsonValue n ? Py.Str(n) : "";
            var mail = address?["address"] is JsonValue m ? Py.Str(m).ToLowerInvariant() : "";
            if ((a as JsonObject)?["type"] is JsonValue t && Py.Str(t) == "resource" || name == config.Me || mail == config.Email || Planner.IsRoom(name))
                continue;
            attendees++;
        }
        var mine = true;
        if (e["organizer"] is JsonObject org && org["emailAddress"] is JsonObject who)
            mine = (who["name"] is JsonValue on && Py.Str(on) == config.Me) || (who["address"] is JsonValue oa && Py.Str(oa).ToLowerInvariant() == config.Email);
        var categories = e["categories"] is JsonArray c ? c.Select(x => x is JsonValue cv ? Py.Str(cv) : "").ToList() : [];
        return new DayEvent(Py.Truthy(e["subject"]) ? Py.Str(e["subject"]) : "", start, end, Py.Truthy(e["isAllDay"]),
            e["showAs"] is JsonValue s ? Py.Str(s) : "busy", categories, mine, attendees);
    }

    /// <summary>The leave periods in [from, to): runs of days that aren't working days (weekends and bank holidays
    /// joined) holding leave, of at least min_working_days weekdays. pretend: weekdays to treat as leave whatever the
    /// calendar says (`--leave`, for trying the documents out). Pure.</summary>
    public static List<LeavePeriod> Periods(LeaveSettings settings, IReadOnlyList<DayEvent> events, DateOnly from, DateOnly to,
        IReadOnlyCollection<DateOnly>? pretend = null)
    {
        var days = new List<(DateOnly Date, Kind Kind)>();
        for (var d = from; d < to; d = d.AddDays(1))
            days.Add((d, pretend?.Contains(d) == true && !DayRules.Weekend(d) ? Kind.Leave : Classify(settings, events, d)));
        var periods = new List<LeavePeriod>();
        var i = 0;
        while (i < days.Count)
        {
            if (days[i].Kind == Kind.Working)
            {
                i++;
                continue;
            }
            var j = i;
            while (j < days.Count && days[j].Kind != Kind.Working)
                j++;
            var run = days.GetRange(i, j - i);
            var openStart = i == 0;
            var openEnd = j == days.Count;
            i = j;
            if (!run.Any(d => d.Kind is Kind.Leave or Kind.HalfDay))
                continue;
            var weekdays = run.Sum(d => d.Kind switch { Kind.Leave or Kind.BankHoliday => 1.0, Kind.HalfDay => 0.5, _ => 0.0 });
            if (weekdays < settings.MinWorkingDays - 1e-9)
                continue;
            var firstAway = run.FindIndex(d => d.Kind != Kind.Weekend);
            var lastAway = run.FindLastIndex(d => d.Kind != Kind.Weekend);
            var first = run[firstAway];
            var last = run[lastAway];
            // A half day at the start is the last working morning (written that day); at the end, you're back that afternoon.
            var before = first.Kind == Kind.HalfDay ? first.Date : run[0].Date.AddDays(-1);
            var back = last.Kind == Kind.HalfDay && lastAway > firstAway ? last.Date : run[^1].Date.AddDays(1);
            periods.Add(new LeavePeriod(first.Date, last.Date, before, back, weekdays, openStart, openEnd));
        }
        return periods;
    }

    private static Kind Classify(LeaveSettings settings, IReadOnlyList<DayEvent> events, DateOnly day)
    {
        if (DayRules.Weekend(day))
            return Kind.Weekend;
        if (events.Any(e => e.Mine && e.Attendees == 0 && DayRules.Covers(e, day)
                            && (settings.BankHolidaySubjects.IsMatch(e.Subject)
                                || e.Categories.Any(c => settings.BankHolidayCategories.Contains(c, StringComparer.OrdinalIgnoreCase)))))
            return Kind.BankHoliday;
        var leave = DayRules.Leave(settings.Rules, events, day);
        if (leave.Entries.Count == 0)
            return Kind.Working;
        return leave.Entries.All(e => settings.HalfDay.IsMatch(e.Subject)) || !leave.DayOff ? Kind.HalfDay : Kind.Leave;
    }
}

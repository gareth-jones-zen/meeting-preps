using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingPreps;

/// <summary>`plan --json`: the plan for another program (a local web page, say) to show. Every time has its UTC offset,
/// in the settings' time zone; field names are camelCase and stay put (new ones may be added).
/// <code>
/// {"generated": "...", "from": "2026-11-26", "days": 5, "calendarFrom": null, "meetings": [{"start": ..., "prep": {...}, "skip": null}]}
/// </code>
/// It only reads: the preps' files, and the review queue's file (without its lock, as a reader).</summary>
public static class PlanJson
{
    private static readonly System.Text.RegularExpressions.Regex UpdateBlock = new(@"(?m)^> \*\*Update ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The settings' time zone (timezone, a Windows name), or the clock's if Windows doesn't know it.</summary>
    public static TimeZoneInfo Zone(Config config, Clock clock)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return clock.Local;
        }
    }

    /// <summary>A wall-clock time in the zone as ISO 8601 with its offset: 2026-11-26T08:30:00+00:00.</summary>
    public static string Offset(TimeZoneInfo zone, DateTime wall) => Format(PyTime.At(zone, wall));

    private static string Format(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static JsonObject Build(Config config, Engine engine, IReadOnlyList<PlanRow> plan, DateOnly start, int days, Clock clock)
    {
        var zone = Zone(config, clock);
        var queue = QueuedFiles(config);
        var meetings = new JsonArray();
        foreach (var row in plan)
            meetings.Add((JsonNode)Meeting(config, engine, row, zone, queue));
        return new JsonObject
        {
            ["generated"] = Format(TimeZoneInfo.ConvertTime(clock.NowUtc(), zone)),
            ["from"] = PyTime.Iso(start),
            ["days"] = days,
            ["timeZone"] = config.TimeZone,
            ["calendarFrom"] = engine.CalendarFrom is { } read ? Offset(zone, read) : null,
            ["meetings"] = meetings,
        };
    }

    /// <summary>The JSON as printed: indented, UTF-8 characters as they are.</summary>
    public static string Text(JsonObject json) => PyJson.Dump(json, indent: 2, ensureAscii: false);

    private static JsonObject Meeting(Config config, Engine engine, PlanRow row, TimeZoneInfo zone, Dictionary<string, List<string>> queue)
    {
        var e = row.Event;
        var begins = PyTime.FromIso(row.Start);
        var ends = e?["end"]?["dateTime"] is JsonValue end ? PyTime.FromIso(PyStr.Head(Py.Str(end), 19)) : EndOf(begins, row.End);
        var attendees = e is null ? [] : Planner.Others(config, e);
        string? Text(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length > 0 ? v.GetValue<string>() : null;
        var json = new JsonObject
        {
            ["start"] = Offset(zone, begins),
            ["end"] = Offset(zone, ends),
            ["subject"] = row.Subject,
            ["series"] = row.Series,
            ["attendees"] = new JsonArray(attendees.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["organizer"] = Text(e?["organizer"]?["emailAddress"]?["name"]),
            ["online"] = e?["isOnlineMeeting"] is JsonValue online && online.GetValueKind() == JsonValueKind.True,
            ["joinUrl"] = Text(e?["onlineMeeting"]?["joinUrl"]),
            ["location"] = Text(e?["location"]?["displayName"]),
            ["link"] = Text(e?["webLink"]),
            ["decision"] = row.Decision,
            ["kind"] = row.Kind,
            ["with"] = row.With,
            ["notAttending"] = row.NotAttending,
            ["prep"] = Prep(config, engine, row, zone, queue),
            ["skip"] = row.Decision == "prep" ? null : row.Why,
        };
        if (row.Absorbed is { } absorbed)
            json["absorbed"] = new JsonObject
            {
                ["subject"] = absorbed.Subject, ["start"] = Offset(zone, PyTime.FromIso(absorbed.Start)),
                ["end"] = Offset(zone, EndOf(PyTime.FromIso(absorbed.Start), absorbed.End)),
            };
        if (row.Leave is { } leave)
            json["leave"] = new JsonObject
            {
                ["first"] = PyTime.Iso(leave.First), ["last"] = PyTime.Iso(leave.Last), ["back"] = PyTime.Iso(leave.ReturnDay),
                ["weekdays"] = leave.Weekdays,
            };
        return json;
    }

    /// <summary>"HH:MM" on the start's day, or the next day if that's not after the start.</summary>
    private static DateTime EndOf(DateTime begins, string hhmm)
    {
        var end = Planner.At(DateOnly.FromDateTime(begins), hhmm);
        return end < begins ? end.AddDays(1) : end;
    }

    private static JsonObject Prep(Config config, Engine engine, PlanRow row, TimeZoneInfo zone, Dictionary<string, List<string>> queue)
    {
        if (row.Decision != "prep" || row.File is null)
            return new JsonObject   // the same keys, so a reader needn't check for them
            {
                ["path"] = null, ["exists"] = false, ["draft"] = false, ["read"] = false, ["writtenAt"] = null, ["updates"] = 0,
                ["queued"] = 0, ["updatedSinceRead"] = false, ["plannedFor"] = null, ["previous"] = null,
            };
        var target = engine.Target(row);
        var json = File(config, target, zone, queue);
        json["plannedFor"] = row.Due is { } due ? Offset(zone, PyTime.FromIso(due)) : null;
        json["previous"] = row.Previous;
        if (row.Companion is { Length: > 0 } companion)
            json["companion"] = File(config, PyPath.Pure(Path.Combine(config.Output, companion)), zone, queue);
        return json;
    }

    /// <summary>One file's state: where it is (relative to the workspace), whether it's there, a draft or read, when it
    /// was written, its "Update HH:MM" blocks, and its open review items, of which update or refresh ones mean it's
    /// been added to since it was marked read.</summary>
    private static JsonObject File(Config config, string path, TimeZoneInfo zone, Dictionary<string, List<string>> queue)
    {
        var exists = System.IO.File.Exists(path);
        string? text = null;
        if (exists)
        {
            try
            {
                text = PrepFile.Load(path).Text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                text = null;
            }
        }
        var draft = text is not null && PrepFile.IsDraft(text);
        var queued = Publish.Inside(config, [path]) is [var key] && queue.TryGetValue(key, out var types) ? types : [];
        return new JsonObject
        {
            ["path"] = config.Relative(path).Replace('\\', '/'),
            ["exists"] = exists,
            ["draft"] = draft,
            ["read"] = exists && text is not null && !draft,
            ["writtenAt"] = exists ? Format(TimeZoneInfo.ConvertTime(Engine.WrittenAt(path), zone)) : null,
            ["updates"] = text is null ? 0 : UpdateBlock.Count(text),
            ["queued"] = queued.Count,
            ["updatedSinceRead"] = exists && text is not null && !draft && queued.Any(t => t is "update" or "refresh"),
        };
    }

    /// <summary>The review queue's open items for this tool's source, as file (relative, as the items name them) to
    /// the items' types. Read without the queue's lock (its writes replace the file whole); a queue that can't be read
    /// counts as empty.</summary>
    internal static Dictionary<string, List<string>> QueuedFiles(Config config)
    {
        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (config.Review is not { } review)
            return files;
        try
        {
            if (PyJson.Parse(PyStr.ReadText(review.File)) is not JsonObject data || data["items"] is not JsonArray items)
                return files;
            foreach (var item in items.OfType<JsonObject>())
            {
                if (item["source"] is not JsonValue source || Py.Str(source) != review.Source || item["files"] is not JsonArray list)
                    continue;
                var type = item["type"] is JsonValue t ? Py.Str(t) : "";
                foreach (var f in list.OfType<JsonValue>())
                {
                    var name = Py.Str(f);
                    if (!files.TryGetValue(name, out var types))
                        files[name] = types = [];
                    types.Add(type);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or System.Text.DecoderFallbackException or InvalidOperationException)
        {
        }
        return files;
    }
}

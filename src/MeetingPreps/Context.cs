using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace MeetingPreps;

/// <summary>What a prep can see beyond the workspace: email, Teams and the diary, through Graph.
///
/// **Email** is yours: what you sent, or what names you in To or Cc. Deleted Items counts. Dropped as noise: meeting
/// responses and invites, automated senders, the folders in mail_skip_folders, anything a mail-triage delete rule
/// would delete (noise_rules), and external mail with nobody else from org_domain on it. **For a 1:1, each email
/// comes in full** (quoted history cut, a forward kept whole, each capped, all capped); for a group, a line each.
/// **Teams:** your 1:1 chat with the person and small group chats you share (or the series' chat by name). **The
/// diary** says who each small meeting is with. Mail and Teams throw when Graph fails; the engine catches it
/// (Engine.Read), so a failure never stops a prep: the prep says at the top what couldn't be read, and why.
/// Everything is formatted exactly as the Python's context.py did.</summary>
public static class Context
{
    private static readonly PyRegex NoiseSubject =
        PyRegex.Get(@"^(accepted|declined|canceled|cancelled|tentative|updated invitation|new time proposed)\b", ignoreCase: true);
    private static readonly PyRegex NoiseSender = PyRegex.Get(@"no-?reply|donotreply|notifications|digest|quarantine@", ignoreCase: true);
    private static readonly PyRegex Quoted =
        PyRegex.Get(@"\n\s*(From: .+\n\s*(Sent|Date): |-----\s*Original Message|_{10,}|On .{6,80} wrote:)", ignoreCase: true);
    private static readonly PyRegex Caution = PyRegex.Get(@"(?s)^Caution: This email originated from outside.*?know the content is safe\s*");
    private static readonly PyRegex BlankLines = PyRegex.Get(@"\n\s*\n+");
    private static readonly PyRegex Tag = PyRegex.Get("<[^>]+>");
    private static readonly PyRegex Space = PyRegex.Get(@"\s+");
    public const int BodyCap = 2000, BodiesCap = 40000;

    private static string Text(JsonNode? node) => node is JsonValue v ? Py.Str(v) : "";

    private static string TextOr(JsonNode? node, string fallback) => Py.Truthy(node) ? Py.Str(node) : fallback;

    private static string Strftime(DateTime t, string format) => PyTime.Strftime(t, format);

    /// <summary>The calendar events given (the engine's calendar read, or its cached copy), one line each.</summary>
    public static string Diary(Config config, IEnumerable<JsonObject> items)
    {
        var lines = new List<string>();
        foreach (var e in items)
        {
            if (Py.Truthy(e["isCancelled"]) || Py.Truthy(e["isAllDay"]))
                continue;
            var begins = PyTime.FromIso(PyStr.Head(Py.Str(e["start"]!["dateTime"]), 19));
            var ends = PyTime.FromIso(PyStr.Head(Py.Str(e["end"]!["dateTime"]), 19));
            if (begins == ends)
                continue;
            var people = new List<string>();
            foreach (var a in Py.Truthy(e["attendees"]) ? (JsonArray)e["attendees"]! : [])
            {
                var address = a?["emailAddress"] as JsonObject;
                var name = TextOr(address?["name"], "");
                if (Text(a?["type"]) == "resource" || Planner.IsRoom(name) || TextOr(address?["address"], "").ToLowerInvariant() == config.Email)
                    continue;
                people.Add(name);
            }
            var with = people.Count is > 0 and <= 4 ? $"  (with {string.Join(", ", people)})" : "";
            lines.Add($"- {Strftime(begins, "%a %d %b %H:%M")}–{Strftime(ends, "%H:%M")}  {TextOr(e["subject"], "(no subject)")}{with}");
        }
        return lines.Count > 0 ? string.Join("\n", lines) : "(nothing)";
    }

    /// <summary>A mail-triage rule: its sender and match patterns, and what it does.</summary>
    internal sealed record TriageRule(string Sender, string Match, string? Action);

    internal static List<TriageRule> TriageRules(Config config, Action<string>? log = null)
    {
        if (config.NoiseRules is null)
            return [];
        try
        {
            var raw = TomlSerializer.Deserialize(PyStr.ReadText(config.NoiseRules), TomlContext.Default.TomlTable) ?? [];
            if (!raw.TryGetValue("rule", out var rules) || rules is not TomlTableArray tables)
                return [];
            var list = new List<TriageRule>();
            foreach (var rule in tables)
            {
                if (!Config.Truthy(rule, "sender"))
                    continue;
                list.Add(new TriageRule(Config.Str(rule["sender"]), rule.TryGetValue("match", out var m) ? Config.Str(m) : "",
                    rule.TryGetValue("action", out var a) ? Config.Str(a) : null));
            }
            return list;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.Invoke($"  noise_rules unreadable: {PyStr.Head(error.Message, 200)}");
            return [];
        }
    }

    /// <summary>First rule whose sender and match both match decides, as in mail-triage. A pattern Python's re and
    /// .NET would read differently is skipped (and said once), rather than run with another meaning.</summary>
    internal static bool TriageDeletes(List<TriageRule> rules, string sender, string text, HashSet<string>? warned = null, Action<string>? log = null)
    {
        foreach (var rule in rules)
        {
            try
            {
                if (PyRegex.Get(rule.Sender, true).IsMatch(sender) && PyRegex.Get(rule.Match, true).IsMatch(text))
                    return rule.Action is "delete" or "resolve";
            }
            catch (PyRegexUnsupportedException error)
            {
                if (warned?.Add(rule.Sender + "\n" + rule.Match) ?? false)
                    log?.Invoke($"  a noise rule was skipped: {PyStr.Head(error.Message, 200)}");
            }
        }
        return false;
    }

    private static List<(string Name, string Address)> People(JsonNode? items)
    {
        var list = new List<(string, string)>();
        if (!Py.Truthy(items))
            return list;
        foreach (var r in (JsonArray)items!)
        {
            var address = Py.Truthy(r?["emailAddress"]) ? r!["emailAddress"] : null;
            list.Add((TextOr(address?["name"], ""), TextOr(address?["address"], "").ToLowerInvariant()));
        }
        return list;
    }

    /// <summary>An email body for the prompt: quoted history cut (unless that leaves almost nothing, as with a
    /// forward), blank lines squeezed, the external-mail banner dropped, capped.</summary>
    public static string FullText(string content, string preview)
    {
        content = content.Replace("\r", "");
        var joined = "\n" + content;
        var head = Quoted.Search(joined) is { } m ? joined[..m.Index] : joined;
        content = PyMore.Len(PyStr.Strip(head)) >= 200 ? head : content;
        content = Caution.Sub("", PyStr.Strip(BlankLines.Sub("\n", content)));
        if (PyMore.Len(content) <= PyMore.Len(preview) + 20)
            return "";
        return PyStr.Head(content, BodyCap) + (PyMore.Len(content) > BodyCap ? " […cut]" : "");
    }

    public static string Mail(IGraph graph, Config config, string? name, Clock clock, int days = 14, int cap = 150,
        DateTimeOffset? sinceTime = null, Action<string>? log = null)
    {
        log ??= _ => { };
        var rules = TriageRules(config, log);
        var warned = new HashSet<string>();
        var start = sinceTime is { } s ? s.ToUniversalTime() : clock.NowUtc().AddDays(-days);
        var since = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var folders = new Dictionary<string, string>();
        string Folder(string id)
        {
            if (!folders.TryGetValue(id, out var display))
            {
                try
                {
                    display = TextOr(graph.Get($"/me/mailFolders/{id}?$select=displayName")["displayName"], "");
                }
                catch (Exception)
                {
                    display = "";
                }
                folders[id] = display;
            }
            return display;
        }

        var select = "$select=subject,from,toRecipients,ccRecipients,bodyPreview,receivedDateTime,sentDateTime,parentFolderId"
                     + (name is not null ? ",body" : "");
        var headers = name is not null ? new Dictionary<string, string> { ["Prefer"] = "outlook.body-content-type=\"text\"" } : null;
        var got = new List<(DateTimeOffset When, string Line)>();
        var budget = BodiesCap;
        (string Url, bool Sent)[] sources =
        [
            ($"/me/messages?$filter=receivedDateTime%20ge%20{since}&$orderby=receivedDateTime%20desc&$top=100&{select}", false),
            ($"/me/mailFolders/sentitems/messages?$filter=sentDateTime%20ge%20{since}&$orderby=sentDateTime%20desc&$top=100&{select}", true),
        ];
        foreach (var (url, sent) in sources)
        {
            var count = 0;
            foreach (var node in graph.Pages(url, headers))
            {
                if (count++ >= 500)
                    break;
                var m = (JsonObject)node!;
                var to = People(m["toRecipients"]);
                var cc = People(m["ccRecipients"]);
                var sender = Py.Truthy(m["from"]) ? People(new JsonArray(m["from"]!.DeepClone()))[0] : ("", "");
                var subject = TextOr(m["subject"], "");
                var preview = string.Join(" ", PyStr.Split(TextOr(m["bodyPreview"], "")));
                if (NoiseSubject.MatchStart(subject) is not null || preview.StartsWith("____", StringComparison.Ordinal)
                    || subject.Length == 0 || preview.StartsWith("-----", StringComparison.Ordinal))
                    continue;
                var everyone = to.Concat(cc).ToList();
                if (!sent)
                {
                    if (NoiseSender.IsMatch(sender.Item2) || config.NoiseSenders.Any(n => PyRegex.Get(n, true).IsMatch(sender.Item2))
                        || TriageDeletes(rules, sender.Item2, subject + " " + preview, warned, log))
                        continue;
                    if (sender.Item2 == config.Email || !everyone.Any(p => p.Address == config.Email))
                        continue;
                    if (config.OrgDomain is { } org && !sender.Item2.EndsWith("@" + org, StringComparison.Ordinal)
                        && !everyone.Any(p => p.Address.EndsWith("@" + org, StringComparison.Ordinal) && p.Address != config.Email))
                        continue;   // external mail only on a business thread, with a colleague on it
                    var folder = Folder(TextOr(m["parentFolderId"], "")).ToLowerInvariant();
                    if (config.MailSkipFolders.Any(f => folder.StartsWith(f, StringComparison.Ordinal)))
                        continue;
                }
                var names = everyone.Select(p => p.Name).Append(sender.Item1).ToHashSet();
                if (name is not null && !names.Any(n => n.ToLowerInvariant().Contains(name.ToLowerInvariant(), StringComparison.Ordinal)))
                    continue;
                var when = PyMore.AwareFromIso(Py.Str(m[sent ? "sentDateTime" : "receivedDateTime"]));
                var who = sent
                    ? $"{config.Owner} to " + PyStr.Head(string.Join(", ", to.Select(p => p.Name)), 80)
                    : (sender.Item1.Length > 0 ? sender.Item1 : sender.Item2);
                preview = PyStr.Head(Caution.Sub("", preview), 240);
                var line = $"- {Strftime(clock.ToLocal(when), "%a %d %b %H:%M")} {who}: {subject} | {preview}";
                if (name is not null && budget > 0)
                {
                    var body = FullText(TextOr(m["body"]?["content"], ""), preview);
                    if (body.Length > 0)
                    {
                        budget -= PyMore.Len(body);
                        line += "\n  Full text:\n" + string.Join("\n", PyStr.SplitLines(body).Select(l => "    " + l));
                    }
                }
                got.Add((when, line));
            }
        }
        var sorted = got.OrderBy(g => g.When).ThenBy(g => g.Line, PyStr.Order).ToList();
        var kept = PyMore.Last(sorted, cap);
        return kept.Count > 0 ? string.Join("\n", kept.Select(g => g.Line)) : "(none)";
    }

    public static string Teams(IGraph graph, string? name, Clock clock, int days = 14, int perChat = 50, int cap = 200,
        string? topic = null, DateTimeOffset? sinceTime = null)
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(topic))
            return "(none)";   // a series with no chat named: nothing to look for, so no call to make
        var since = sinceTime is { } s ? s.ToUniversalTime() : clock.NowUtc().AddDays(-days);
        var chats = graph.Get("/me/chats?$expand=members&$top=50");
        var lines = new List<(DateTimeOffset Sent, string Line)>();
        foreach (var chat in chats["value"] as JsonArray ?? [])
        {
            var members = (chat?["members"] as JsonArray ?? []).Select(m => TextOr(m?["displayName"], "")).ToList();
            var type = Text(chat?["chatType"]);
            if (type is not ("oneOnOne" or "group") || members.Count > 12)
                continue;
            var chatTopic = chat?["topic"] is JsonValue t ? Py.Str(t) : null;
            if (!((topic is { Length: > 0 } && chatTopic == topic) || (name is { Length: > 0 } && members.Contains(name))))
                continue;
            var label = Py.Truthy(chat?["topic"]) ? chatTopic! : type == "oneOnOne" ? "1:1 chat" : string.Join(", ", members);
            var found = graph.Get($"/chats/{Py.Str(chat!["id"])}/messages?$top={perChat}");
            foreach (var message in found["value"] as JsonArray ?? [])
            {
                var sent = PyMore.AwareFromIso(Py.Str(message!["createdDateTime"]));
                if (Text(message["messageType"]) != "message" || sent < since)
                    continue;
                var text = Tag.Sub(" ", TextOr(message["body"]?["content"], ""));
                text = PyStr.Strip(Space.Sub(" ", text.Replace("&nbsp;", " ").Replace("&amp;", "&")));
                var from = Py.Truthy(message["from"]) ? message["from"] : null;
                var user = Py.Truthy(from?["user"]) ? from!["user"] : null;
                var sender = TextOr(user?["displayName"], "?");
                if (text.Length > 0)
                    lines.Add((sent, $"- {Strftime(clock.ToLocal(sent), "%a %d %b %H:%M")} [{PyStr.Head(label, 40)}] {sender}: {PyStr.Head(text, 500)}"));
            }
        }
        var sorted = lines.OrderBy(l => l.Sent).ThenBy(l => l.Line, PyStr.Order).ToList();
        var kept = PyMore.Last(sorted, cap);
        return kept.Count > 0 ? string.Join("\n", kept.Select(l => l.Line)) : "(none)";
    }
}

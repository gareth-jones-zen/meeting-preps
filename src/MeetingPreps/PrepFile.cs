using System.Text;
using System.Text.RegularExpressions;

namespace MeetingPreps;

/// <summary>A prep's draft marker, as a file's text: the "[DRAFT]" in its title line (the "# " line that starts every
/// file this tool writes), and "status: draft" in a frontmatter block if the file has one (this tool doesn't write
/// frontmatter, but a document you've given one may use it). A file with either is a draft; one with neither has been
/// read. Marking it read takes "[DRAFT]" out and makes "status: draft" "status: read"; marking it a draft again
/// reverses both. Edits leave everything else, line endings and byte order mark included, exactly as it was.</summary>
public static class PrepFile
{
    private static readonly Regex DraftInTitle = new(@"\A(#[ \t]+)\[DRAFT\][ \t]*", RegexOptions.CultureInvariant);
    private static readonly Regex StatusDraft = new(@"\Astatus:[ \t]*[""']?draft[""']?[ \t]*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex StatusRead = new(@"\Astatus:[ \t]*[""']?read[""']?[ \t]*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>One line of the text: where it starts, how long it is without its line end, and the line end.</summary>
    private readonly record struct Line(int Start, int Length, string End);

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        var at = 0;
        while (at < text.Length)
        {
            var nl = text.IndexOf('\n', at);
            if (nl < 0)
            {
                lines.Add(new Line(at, text.Length - at, ""));
                break;
            }
            var cr = nl > at && text[nl - 1] == '\r';
            lines.Add(new Line(at, nl - at - (cr ? 1 : 0), cr ? "\r\n" : "\n"));
            at = nl + 1;
        }
        return lines;
    }

    private static string Of(string text, Line line) => text.Substring(line.Start, line.Length);

    /// <summary>The frontmatter's lines (between a first line "---" and the next "---"), or (0, 0) if there's none.</summary>
    private static (int First, int Close) Frontmatter(string text, List<Line> lines)
    {
        if (lines.Count == 0 || Of(text, lines[0]).TrimEnd() != "---")
            return (0, 0);
        for (var i = 1; i < lines.Count; i++)
            if (Of(text, lines[i]).TrimEnd() is "---" or "...")
                return (1, i);
        return (0, 0);
    }

    /// <summary>The title line: the first "# " line after any frontmatter, or -1.</summary>
    private static int Title(string text, List<Line> lines)
    {
        var (_, close) = Frontmatter(text, lines);
        for (var i = close == 0 ? 0 : close + 1; i < lines.Count; i++)
            if (Of(text, lines[i]) is var l && (l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("#\t", StringComparison.Ordinal)))
                return i;
        return -1;
    }

    /// <summary>Whether the text is a draft: "[DRAFT]" in its title line, or "status: draft" in its frontmatter.</summary>
    public static bool IsDraft(string text)
    {
        var lines = Lines(text);
        var title = Title(text, lines);
        if (title >= 0 && DraftInTitle.IsMatch(Of(text, lines[title])))
            return true;
        var (first, close) = Frontmatter(text, lines);
        for (var i = first; i < close; i++)
            if (StatusDraft.IsMatch(Of(text, lines[i])))
                return true;
        return false;
    }

    /// <summary>The text marked read: "[DRAFT]" taken out of the title line, and a frontmatter "status: draft" made
    /// "status: read". Returns the text and what changed (empty: it was already read).</summary>
    public static (string Text, List<string> Changes) Read(string text)
    {
        var lines = Lines(text);
        var changes = new List<string>();
        var output = new StringBuilder(text.Length);
        var title = Title(text, lines);
        var (first, close) = Frontmatter(text, lines);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = Of(text, lines[i]);
            if (i == title && DraftInTitle.IsMatch(line))
            {
                line = DraftInTitle.Replace(line, "$1", 1);
                changes.Add("[DRAFT] taken out of the title");
            }
            else if (i >= first && i < close && StatusDraft.IsMatch(line))
            {
                line = "status: read";
                changes.Add("status: draft made status: read in the frontmatter");
            }
            output.Append(line).Append(lines[i].End);
        }
        return (output.ToString(), changes);
    }

    /// <summary>The text marked as a draft again (mark-read --undo): a frontmatter "status: read" made "status: draft"
    /// if there is one, otherwise "[DRAFT]" put back at the start of the title line. Returns the text and what changed
    /// (empty: it was already a draft). A file with neither is an InvalidDataException: there's nowhere to put the
    /// marker back.</summary>
    public static (string Text, List<string> Changes) Draft(string text)
    {
        var lines = Lines(text);
        var title = Title(text, lines);
        var (first, close) = Frontmatter(text, lines);
        var statusRead = Enumerable.Range(first, close - first).Any(i => StatusRead.IsMatch(Of(text, lines[i])));
        if (IsDraft(text))
            return (text, []);
        if (title < 0 && !statusRead)
            throw new InvalidDataException("it has no title line (\"# ...\") to put [DRAFT] back in");
        var changes = new List<string>();
        var output = new StringBuilder(text.Length + 16);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = Of(text, lines[i]);
            if (i == title && !statusRead)
            {
                var hash = line.Length - line[1..].TrimStart(' ', '\t').Length;
                line = line[..hash] + "[DRAFT] " + line[hash..];
                changes.Add("[DRAFT] put back in the title");
            }
            else if (i >= first && i < close && StatusRead.IsMatch(line))
            {
                line = "status: draft";
                changes.Add("status: read made status: draft in the frontmatter");
            }
            output.Append(line).Append(lines[i].End);
        }
        return (output.ToString(), changes);
    }

    /// <summary>A file's text with its byte order mark noted, so it's written back as it was. Invalid UTF-8 is an
    /// InvalidDataException: nothing is changed in a file that can't be read faithfully.</summary>
    public static (string Text, bool Bom) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("it isn't UTF-8 text");
        }
    }

    /// <summary>Writes the text over the file in place, with its byte order mark if it had one, keeping its creation
    /// time: the pre-meeting refresh reads "written at" as the earlier of creation and modification, so marking a prep
    /// read never moves the time it looks for messages since.</summary>
    public static void Save(string path, string text, bool bom)
    {
        var created = File.GetCreationTimeUtc(path);
        var body = new UTF8Encoding(false).GetBytes(text);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            if (bom)
                stream.Write([0xEF, 0xBB, 0xBF]);
            stream.Write(body);
            stream.SetLength(stream.Position);
        }
        File.SetCreationTimeUtc(path, created);
    }
}

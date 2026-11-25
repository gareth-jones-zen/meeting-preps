using ActivityJournal;

namespace MeetingPreps;

/// <summary>
/// The shared activity journal (1.12.0): one line for each prep written, updated or refreshed, and each mark-read,
/// written through background-tools-lib's ActivityJournal to journal_file (by convention %EA_LOCAL%\journal.jsonl), for
/// a "while you were away" view. Only what was written into the workspace: never a dry run, `prompt`, or a trial write
/// to a folder outside it. <b>Every line is "restricted"</b>: a prep's meeting, its file and the transcript it was
/// updated from can name a person (a 1:1 with a report) or need-to-know work, so a screen shown to others hides the
/// whole line. Never a prep's contents. Writing it never fails the run: a problem is logged.
/// </summary>
internal static class Activity
{
    public const string Tool = "meeting-preps";

    /// <summary>A line for a prep, update or refresh written: <paramref name="written"/> in full; nothing when none of
    /// them is inside the workspace (a trial). Never throws.</summary>
    public static void Wrote(Config config, Action<string> log, string by, string type, string meeting, string start,
        IReadOnlyList<string> written, string? from)
    {
        var files = Publish.Inside(config, written);
        if (files.Count == 0)
            return;
        Write(config, log, new JournalEntry
        {
            Tool = Tool,
            What = type,
            By = by,
            Subject = $"{meeting}, {Publish.When(start)}" + (string.IsNullOrEmpty(from) ? "" : $" (from {from})"),
            Rule = type,
            Why = type switch
            {
                "prep" => "due: a draft to read before the meeting",
                "update" => "a transcript since the prep was written",
                _ => "email and Teams since the prep was written, in the hour before the meeting",
            },
            Item = files[0],
            Sensitivity = Sensitivity.Restricted,
        });
    }

    /// <summary>A line for a prep marked read (or, with --undo, a draft again). Never throws.</summary>
    public static void Marked(Config config, Action<string> log, string name, bool undo) =>
        Write(config, log, new JournalEntry
        {
            Tool = Tool,
            What = undo ? "mark-draft" : "mark-read",
            By = By.Hand,
            Subject = name,
            Rule = "by hand",
            Why = undo ? "its draft marker put back" : "read: its draft marker taken out",
            Item = name,
            Undo = undo ? $"meeting-preps mark-read \"{name}\"" : $"meeting-preps mark-read \"{name}\" --undo",
            Sensitivity = Sensitivity.Restricted,
        });

    private static void Write(Config config, Action<string> log, JournalEntry entry)
    {
        if (config.JournalFile is not { } path)
            return;
        try
        {
            new Journal(path).TryWrite(entry, log);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }
}

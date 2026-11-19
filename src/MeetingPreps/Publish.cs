using System.Text.Json.Nodes;
using PyCompat;
using RepoCommit;

namespace MeetingPreps;

/// <summary>After each file written (a prep, an update, a refresh): [git] commits it, and [review] puts it in a review
/// queue. Each is optional; with neither, nothing happens here. Only files inside the repository ([git] repo, or the
/// workspace without [git]) count, so preps written somewhere else (a trial folder) are neither committed nor queued.
/// Nothing here stops a run: a failure is logged.</summary>
public static class Publish
{
    /// <summary>The commit's subject for each kind of write.</summary>
    public static string Subject(string type, string meeting, string start, string? from) => type switch
    {
        "prep" => $"Prep for {meeting}, {When(start)} [DRAFT]",
        "update" => $"Update the {meeting} prep from {from ?? ""}",
        "refresh" => $"Update the {meeting} prep from email and Teams",
        _ => throw new ArgumentException($"Not a kind of write: {type}", nameof(type)),
    };

    /// <summary>The meeting's start as "Wed 18 Nov 08:45" (the time as written, whatever its offset).</summary>
    public static string When(string start) => PyTime.Strftime(PyTime.FromIsoFormat(start, TimeZoneInfo.Utc).DateTime, "%a %d %b %H:%M");

    /// <summary>The review item's title: "Weekly 1-2-1 - Sam, 2026-11-11 10:00".</summary>
    public static string Title(string meeting, string start) => $"{meeting}, {PyStr.Head(start, 16).Replace('T', ' ')}";

    /// <summary>The files that count, relative to the repository (or the workspace) with forward slashes: those that
    /// exist and are inside it.</summary>
    public static List<string> Inside(Config config, IEnumerable<string> written)
    {
        var root = config.Git?.Repo ?? config.Workspace;
        return written.Where(File.Exists).Select(path => Config.RelativeTo(PyPath.Resolve(path), root))
            .OfType<string>().Where(path => path != ".").ToList();
    }

    /// <summary>originals: a copy of each file written inside the workspace, as written, at the same relative path under
    /// the originals folder, replacing the copy from the last write. So with no git, what the person changed in a draft is
    /// the file against its original. Nothing here stops a run: a failure is logged.</summary>
    public static void Keep(Config config, IEnumerable<string> written, Action<string> log)
    {
        if (config.Originals is not { } originals)
            return;
        foreach (var path in written.Where(File.Exists))
        {
            if (Config.RelativeTo(PyPath.Resolve(path), config.Workspace) is not { } relative || relative == ".")
                continue;
            try
            {
                var copy = Path.Combine(originals, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
                log($"  originals: kept {relative} as written");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"  originals: couldn't keep {relative}: {error.Message}");
            }
        }
    }

    /// <summary>originals, then [git] and [review], for one write: type is prep, update or refresh; written the files, in
    /// full. git not set up here (not installed, not a repository) is the library's one log line a process, and nothing
    /// else.</summary>
    public static void After(Config config, string type, string meeting, string start, IReadOnlyList<string> written, string? from,
        Action<string> log, Func<DateTime> clock)
    {
        Keep(config, written, log);
        if (config.Git is null && config.Review is null)
            return;
        var files = Inside(config, written);
        if (files.Count == 0)
            return;   // written outside the repository: a trial, nothing to commit or queue
        if (config.Git is { Commit: true } git)
        {
            try
            {
                var settings = Config.RelativeTo(config.Path, git.Repo) ?? Path.GetFileName(config.Path);
                var message = Committer.Message(Subject(type, meeting, start, from),
                    $"Written automatically by meeting-preps ({settings}).", git.Trailers);
                git.Open(line => log("  " + line)).Commit(message, files.Select(f => Path.Combine(git.Repo, f)), push: git.Push);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log($"  git: commit failed: {error.Message}");
            }
        }
        if (config.Review is { } review)
        {
            try
            {
                var fields = new JsonObject
                {
                    ["meeting"] = meeting, ["start"] = start,
                    ["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                    ["title"] = Title(meeting, start),
                };
                if (!string.IsNullOrEmpty(from))
                    fields["from"] = from;
                review.Open(clock).Add(review.Source, type, fields);
                log($"  review: queued the {type} under {review.Source}");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log($"  review queue failed: {error.Message}");
            }
        }
    }
}

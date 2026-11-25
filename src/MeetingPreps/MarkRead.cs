using System.Text.Json.Nodes;
using PyCompat;
using RepoCommit;
using WinBackground;

namespace MeetingPreps;

/// <summary>`mark-read FILE [--undo] [--dry-run]`: the owner has read a prep. Its draft marker comes out (PrepFile), the
/// file alone is committed as the tool commits its own writes ([git], if set up), and its items in the review queue
/// ([review]) are marked done, each once every file it names has been read. `--undo` puts the marker back (the review
/// items stay done); `--dry-run` says what it would do and changes nothing. Local and reversible, so there's no --yes.
/// Exit codes: 0 done (or nothing to do); 1 the file couldn't be changed; 2 no such file, not a prep, or no title to mark.</summary>
public static class MarkRead
{
    public static int Run(Config config, string given, bool undo, bool dryRun, PrepsHost host)
    {
        var log = Logger.Create(config.LogFile, null, stampEcho: false, clock: host.Clock.Now);
        void Say(string text) => Write(host.Out, text);
        void Warn(string text) => Write(host.Err, text);

        var path = Find(config, given);
        if (path is null)
        {
            Warn($"No such file: {given} (looked from here, in the workspace and in {config.Relative(config.Output)}).");
            return 2;
        }
        if (!Inside(config, path))
        {
            Warn($"{path} isn't in the workspace or a folder preps are written to; nothing changed.");
            return 2;
        }
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            Warn($"{path} isn't a prep (a .md file); nothing changed.");
            return 2;
        }
        var name = config.Relative(path).Replace('\\', '/');
        var state = undo ? "a draft" : "read";

        List<string> changes;
        try
        {
            using (dryRun ? null : Engine.EditLock(host.LockPrefix, path))
            {
                var (text, bom) = PrepFile.Load(path);
                (var changed, changes) = undo ? PrepFile.Draft(text) : PrepFile.Read(text);
                if (!dryRun && changes.Count > 0)
                    PrepFile.Save(path, changed, bom);
            }
        }
        catch (InvalidDataException error)
        {
            Warn($"{name}: {error.Message}; nothing changed.");
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LockTimeoutException)
        {
            Warn($"Couldn't mark {name} {state}: {error.Message}");
            log($"mark-read: couldn't mark {name} {state}: {error.Message}");
            return 1;
        }

        var lines = new List<string>();
        if (changes.Count == 0)
            lines.Add($"{name} is already {state}: nothing to change.");
        else
            lines.Add($"{(dryRun ? "Would mark" : "Marked")} {name} {state}: {string.Join("; ", changes)}.");

        // [git]: the file alone, as the tool commits its own writes
        string? committed = null;
        if (changes.Count > 0 && config.Git is { Commit: true } git && Publish.Inside(config, [path]).Count > 0)
        {
            if (dryRun)
                lines.Add($"Would commit it{(git.Push ? " and push" : "")} (if {git.Repo} is a git repository).");
            else
            {
                var settings = Config.RelativeTo(config.Path, git.Repo) ?? Path.GetFileName(config.Path);
                var subject = undo ? $"Mark {Path.GetFileName(path)} as a draft again" : $"Mark {Path.GetFileName(path)} read";
                var message = Committer.Message(subject, $"Done by meeting-preps mark-read{(undo ? " --undo" : "")} ({settings}).", git.Trailers);
                try
                {
                    var result = git.Open(line => log("  " + line)).Commit(message, [path], push: git.Push);
                    if (result.Committed)
                    {
                        committed = result.Commit ?? "";
                        lines.Add($"Committed it as {committed}.");
                    }
                    else if (result.Error is { } failed)
                        Warn($"git: commit failed: {failed}");
                    else if (result.Skipped is not null)
                        lines.Add("Not committed: git isn't set up there.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Warn($"git: commit failed: {error.Message}");
                    log($"  git: commit failed: {error.Message}");
                }
            }
        }

        // [review]: this file's items, each once every file it names has been read (not for --undo)
        var done = 0;
        if (!undo && config.Review is { } review)
        {
            try
            {
                var queue = review.Open(host.Clock.Now);
                var ids = Finished(config, queue.Items(review.Source), path);
                if (ids.Count > 0 && !dryRun)
                    done = queue.Done(ids);
                else
                    done = ids.Count;
                if (ids.Count > 0)
                    lines.Add($"{(dryRun ? "Would mark" : "Marked")} {done} review item(s) done.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Warn($"review queue: {error.Message}");
                log($"  review queue failed: {error.Message}");
            }
        }

        Say(string.Join("\n", lines));
        if (!dryRun && changes.Count > 0)
            Activity.Marked(config, log, name, undo);
        if (!dryRun && (changes.Count > 0 || done > 0))
            log($"mark-read{(undo ? " --undo" : "")}: {name}: {(changes.Count > 0 ? string.Join("; ", changes) : $"already {state}")}"
                + (committed is not null ? $"; committed {committed}" : "")
                + (done > 0 ? $"; {done} review item(s) done" : ""));
        return 0;
    }

    private static void Write(TextWriter writer, string text)
    {
        writer.Write(text.Replace("\n", Environment.NewLine) + Environment.NewLine);
        writer.Flush();
    }

    /// <summary>The file: as given (from the current folder), else in the workspace, else in the output folder.</summary>
    internal static string? Find(Config config, string given)
    {
        foreach (var candidate in new[] { given, Path.Combine(config.Workspace, given), Path.Combine(config.Output, given) })
        {
            var full = PyPath.Resolve(candidate);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    /// <summary>In the workspace, the output folder or a series' own folder: never a file somewhere else.</summary>
    private static bool Inside(Config config, string path) =>
        Config.IsRelativeTo(path, config.Workspace) || Config.IsRelativeTo(path, config.Output)
        || config.SeriesInOrder.Any(s => s.Value.Folder is { } folder && Config.IsRelativeTo(path, folder));

    /// <summary>The ids of the items that name this file and whose other files (a companion, say) are read or gone.</summary>
    internal static List<string> Finished(Config config, IReadOnlyList<JsonObject> items, string path)
    {
        var root = config.Git?.Repo ?? config.Workspace;
        var key = Publish.Inside(config, [path]) is [var k] ? k : null;
        if (key is null)
            return [];
        var ids = new List<string>();
        foreach (var item in items)
        {
            var files = item["files"] is JsonArray list ? list.OfType<JsonValue>().Select(Py.Str).ToList() : [];
            if (!files.Contains(key, StringComparer.OrdinalIgnoreCase) || item["id"] is not JsonValue id)
                continue;
            if (files.Where(f => !string.Equals(f, key, StringComparison.OrdinalIgnoreCase)).All(f => Read(Path.Combine(root, f))))
                ids.Add(Py.Str(id));
        }
        return ids;
    }

    private static bool Read(string path)
    {
        if (!File.Exists(path))
            return true;
        try
        {
            return !PrepFile.IsDraft(PrepFile.Load(path).Text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MeetingPreps.Tests;

/// <summary>A new git repository in a folder (an identity of its own, no line-end conversion), for checking what a
/// tool commits, and the golden cases' set-up: the files committed first, an optional failing pre-commit hook.</summary>
public sealed class GitRepo
{
    public GitRepo(string path, JsonObject? committed = null, bool failingHook = false)
    {
        Path = path;
        Directory.CreateDirectory(path);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        System.IO.File.AppendAllText(System.IO.Path.Combine(path, ".git", "info", "exclude"), "tools/\n");   // the tool's own settings
        Write(new JsonObject { ["README.md"] = "first\n" });
        if (committed is not null)
            Write(committed);
        Git("add", "-A");
        Git("commit", "-q", "-m", "Set-up");
        if (failingHook)
            System.IO.File.WriteAllText(System.IO.Path.Combine(path, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    /// <summary>Each file's text, written as UTF-8 bytes exactly.</summary>
    public void Write(JsonObject? files, string? root = null)
    {
        foreach (var (relative, text) in files ?? [])
        {
            var file = System.IO.Path.Combine(root ?? Path, relative.Replace('/', '\\'));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllBytes(file, Encoding.UTF8.GetBytes((string)text!));
        }
    }

    /// <summary>The commits after the set-up, as the golden cases record them: message, and name-status with renames.</summary>
    public JsonArray Commits()
    {
        var commits = new JsonArray();
        foreach (var sha in Git("rev-list", "--reverse", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            commits.Add((JsonNode)new JsonObject
            {
                ["message"] = Git("log", "-1", "--format=%B", sha).TrimEnd('\n'),
                ["files"] = Lines(Git("show", "--name-status", "-M", "--format=", sha)),
            });
        return commits;
    }

    public JsonArray Status() => Lines(Git("status", "--porcelain", "-uall"));

    private static JsonArray Lines(string text) =>
        new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());

    public string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path,
        };
        foreach (var arg in (string[])["-c", "core.quotepath=false", .. args])
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Result.Replace("\r\n", "\n");
    }

    /// <summary>Delete a folder holding a repository (git's objects are read-only).</summary>
    public static void Delete(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using ClaudeHeadless;

namespace MeetingPreps.Tests;

/// <summary>hook_queue (1.5.0): the hook queues the update at the front of the claude-headless queue and starts the
/// runner, for the transcripts hook_include and hook_exclude let through; nothing runs Claude there and then.</summary>
public sealed class HookQueueTests
{
    [Fact]
    public void The_hook_queues_an_update_of_the_transcripts_it_wants_at_the_front()
    {
        using var f = new Fixture("hook_queue = true\nhook_include = [\"team/*.md\", \"meetings/*.md\"]\nhook_exclude = [\"*/performance-reviews/*\", \"*/summaries/*\"]\n"
                                  + Fixture.Settings);
        string Write(string rel)
        {
            var path = Path.Combine(f.Config.Workspace, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "**Sam Jones** 0:01\nHello.\n");
            return rel;
        }
        var oneToOne = Write("team/sam-jones/one-to-ones/2026-11-11.md");
        var review = Write("team/sam-jones/performance-reviews/2026-09-review-transcript.md");
        var weekly = Write("comms/weekly-updates/2026-11-16.md");
        var queue = new JobQueue(Path.Combine(f.Dir, "queue"));
        queue.Add(["earlier", "job"], f.Dir);
        var started = 0;
        var host = new PrepsHost
        {
            Out = new StringWriter(), Err = new StringWriter(), Clock = f.Clock, Queue = queue, StartRunner = () => started++,
            Exe = @"C:\tools\meeting-preps.exe", LockPrefix = "mp-test-",
            Claude = new Fixture.Claude(f), Graph = (_, _) => throw new InvalidOperationException("the queued hook reads no calendar"),
            Stdin = () => new StringReader($"{{\"written\": [\"{oneToOne}\", \"{review}\", \"{weekly}\"]}}"),
        };
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = f.Config.Workspace;   // doc-filer runs its hook in the repo, with paths relative to it
        try
        {
            Assert.Equal(0, MeetingPreps.Cli.Run(["--config", f.Config.Path, "hook"], host));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
        var first = queue.Pending()[0];
        Assert.Equal([@"C:\tools\meeting-preps.exe", "--config", f.Config.Path, "update", Path.Combine(f.Config.Workspace, @"team\sam-jones\one-to-ones\2026-11-11.md")], first.Argv);
        Assert.Equal(2, queue.Pending().Count);
        Assert.Equal(1, started);
        Assert.Empty(f.Prompts);
    }

    [Fact]
    public void Nothing_wanted_queues_nothing_and_starts_nothing()
    {
        using var f = new Fixture("hook_queue = true\nhook_include = [\"team/*.md\"]\n" + Fixture.Settings);
        var queue = new JobQueue(Path.Combine(f.Dir, "queue"));
        var started = 0;
        var host = new PrepsHost
        {
            Out = new StringWriter(), Err = new StringWriter(), Clock = f.Clock, Queue = queue, StartRunner = () => started++,
            Stdin = () => new StringReader("{\"written\": [\"nothing.md\"]}"),
        };
        Assert.Equal(0, MeetingPreps.Cli.Run(["--config", f.Config.Path, "hook"], host));
        Assert.Empty(queue.Pending());
        Assert.Equal(0, started);
    }

    [Fact]
    public void Hook_settings_are_checked()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Dir, "bad.toml"), "hook_queue = \"yes\"\n" + Fixture.Settings);
        Assert.Throws<SettingsException>(() => Config.Load(Path.Combine(f.Dir, "bad.toml")));
    }
}

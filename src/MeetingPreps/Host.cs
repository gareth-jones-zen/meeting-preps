using System.Diagnostics;
using System.Text;
using ClaudeHeadless;
using PyCompat;
using WinBackground;

namespace MeetingPreps;

/// <summary>A program ran out of time (Python's subprocess.TimeoutExpired).</summary>
public sealed class ProcessTimeoutException(string message) : TimeoutException(message);

/// <summary>A program meeting-preps starts: the after_write hook, or a kind's context command.</summary>
public sealed record ProcessRequest(IReadOnlyList<string> Argv, string Cwd, string? Stdin,
    IReadOnlyDictionary<string, string> ExtraEnv, TimeSpan Timeout);

/// <summary>Starts programs. Tests and parity checks give a fake that records the request.</summary>
public interface IProcessRunner
{
    /// <summary>Runs it, with no window, and returns its exit code and output (UTF-8, bad bytes replaced, universal
    /// newlines, as Python's text mode with errors="replace"). Throws if it can't start or runs out of time.</summary>
    Completed Run(ProcessRequest request);
}

/// <summary>Everything the tool touches outside itself, so tests and parity checks can give it fakes: Claude, the
/// seat's queue, Graph, the clock, notifications, the programs it starts and where it writes output.</summary>
public sealed class PrepsHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<TextReader> Stdin { get; init; } = () => Console.In;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Clock Clock { get; init; } = Clock.System;

    private IClaudeRunner? claude;

    /// <summary>Claude Code, headless. Created on first use, so a fake can be given instead.</summary>
    public IClaudeRunner Claude
    {
        get => claude ??= new ClaudeRunner(Queue);
        init => claude = value;
    }

    public JobQueue Queue { get; init; } = JobQueue.Default;

    /// <summary>Queue a job that met the usage limit; true if this limit is news (so it's notified once).</summary>
    public Func<IReadOnlyList<string>, string, DateTimeOffset, bool>? Retry { get; init; }

    public bool QueueRetry(IReadOnlyList<string> argv, string cwd, DateTimeOffset reset) =>
        Retry is not null ? Retry(argv, cwd, reset) : Queue.Retry(argv, cwd, reset);

    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;

    public IProcessRunner Processes { get; init; } = new ProcessRunner();

    /// <summary>The Graph for these settings; interactive null decides by whether there's a console.</summary>
    public Func<Config, bool?, IGraph> Graph { get; init; } = (config, interactive) => new GraphReader(MeetingPreps.GraphReader.ClientFor(config, interactive));

    /// <summary>meeting-preps.exe: what a job queued at the usage limit runs.</summary>
    public string Exe { get; init; } = Path.Combine(AppContext.BaseDirectory, "meeting-preps.exe");

    /// <summary>meeting-preps-background.exe: what the autostart task runs.</summary>
    public string BackgroundExe { get; init; } = Path.Combine(AppContext.BaseDirectory, "meeting-preps-background.exe");

    public ITask Task { get; init; } = new ScheduledTask(Autostart.TaskName, Autostart.Description, Autostart.Results);

    /// <summary>Waiting between watcher ticks; tests give one that advances a fake clock.</summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Lock names are taken as given; tests prefix them so they never meet a real run's.</summary>
    public string LockPrefix { get; init; } = "";
}

/// <summary>The real process runner: no window, the environment plus the extra variables.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public Completed Run(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.Argv[0])
        {
            WorkingDirectory = request.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in request.Argv.Skip(1))
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in request.ExtraEnv)
            info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {request.Argv[0]}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (request.Stdin is not null)
                process.StandardInput.Write(request.Stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // it didn't read its input: carry on and see how it ended
        }
        if (!process.WaitForExit(request.Timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw new ProcessTimeoutException(
                $"Command '{PyMore.ListRepr(request.Argv)}' timed out after {request.Timeout.TotalSeconds:0} seconds");
        }
        process.WaitForExit();
        return new Completed(process.ExitCode, PyStr.UniversalNewlines(stdout.Result), PyStr.UniversalNewlines(stderr.Result));
    }
}

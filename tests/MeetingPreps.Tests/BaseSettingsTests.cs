using PyCompat;

namespace MeetingPreps.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("mp-base-").FullName;

    public BaseSettingsTests()
    {
        Write("base-config.toml", $"""
            login_hint = "me@example.com"
            tenant = "contoso"
            msal_cache = "%ROOT%/msal-cache.json"
            time_zone = "Pacific Standard Time"

            [paths]
            ROOT = '{root}\shared'

            [git]
            repo = ".."
            push = true

            [review]
            file = "%ROOT%/pending.json"
            lock = "shared-lock"
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(root, "tools", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    private Config Load(string extra) =>
        Config.Load(Write("meeting-preps.toml", "base = \"base-config.toml\"\nme = \"Alex Smith\"\nemail = \"alex@example.com\"\nworkspace = \"..\"\n" + extra));

    private string Shared(string name) => PyPath.Resolve(Path.Combine(root, "shared", name));

    [Fact]
    public void Sign_in_settings_time_zone_and_path_variables_come_from_the_base()
    {
        var config = Load("log_file = \"%ROOT%/meeting-preps.log\"\n");
        Assert.Equal(("me@example.com", "contoso"), (config.LoginHint, config.Tenant));
        Assert.Equal(Shared("msal-cache.json"), config.MsalCache);
        Assert.Equal(Shared("meeting-preps.log"), config.LogFile);
        Assert.Equal("Pacific Standard Time", config.TimeZone);
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("tenant = \"own\"\nlogin_hint = \"own@example.com\"\ntimezone = \"GMT Standard Time\"\n");
        Assert.Equal(("own@example.com", "own", "GMT Standard Time"), (config.LoginHint, config.Tenant, config.TimeZone));
        Assert.Equal("UTC", Config.Load(Write("own-tz.toml", "me = \"A\"\nemail = \"a@example.com\"\ntime_zone = \"UTC\"\n")).TimeZone);
    }

    [Fact]
    public void Git_and_review_apply_only_where_this_file_has_the_section()
    {
        var none = Load("");
        Assert.Null(none.Git);
        Assert.Null(none.Review);

        var review = Load("\n[review]\nsource = \"preps\"\nlock = \"mine\"\n");
        Assert.Null(review.Git);
        Assert.Equal((Shared("pending.json"), "preps", "mine"), (review.Review!.File, review.Review.Source, review.Review.Lock));

        var git = Load("\n[git]\n");
        Assert.Equal(PyPath.Resolve(root), git.Git!.Repo);
        Assert.True(git.Git.Push);
        Assert.Null(git.Review);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var path = Write("missing.toml", "base = \"nope.toml\"\nme = \"A\"\nemail = \"a@example.com\"\n");
        Assert.Contains("doesn't exist", Assert.Throws<SettingsException>(() => Config.Load(path)).Message);
    }
}

using WinBackground;

namespace MeetingPreps.Tests;

/// <summary>[schedule], background-tools-lib's shared scheduling settings (1.9.0), and the old [watch] form.</summary>
public class ScheduleTests
{
    private static readonly DateTime Monday = new(2026, 11, 9);
    private static readonly DateTime Saturday = new(2026, 11, 14, 10, 0, 0);

    [Fact]
    public void With_no_schedule_the_defaults_are_unchanged()
    {
        using var f = new Fixture();
        Assert.Equal(5, f.Config.WatchEveryMinutes);
        Assert.Equal(("07:00", "18:00"), f.Config.WatchHours);
        Assert.True(f.Config.WatchWeekdays);
        Assert.False(f.Config.ScheduleFromOldKeys);
        Assert.True(Watch.Due(f.Config, Monday.AddHours(7)));
        Assert.False(Watch.Due(f.Config, Monday.AddHours(18)));
        Assert.False(Watch.Due(f.Config, Saturday));
    }

    [Fact]
    public void A_schedule_table_is_read()
    {
        using var f = new Fixture(Fixture.Settings + "\n[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n");
        Assert.Equal(("00:00", "24:00"), f.Config.WatchHours);
        Assert.False(f.Config.WatchWeekdays);
        Assert.False(f.Config.ScheduleFromOldKeys);
        Assert.True(Watch.Due(f.Config, Saturday.AddHours(13)));
        Assert.Equal("a run every 5 min, 00:00-24:00", f.Config.ScheduleText());
    }

    [Fact]
    public void The_old_watch_keys_still_work_and_mean_the_same()
    {
        using var old = new Fixture(Fixture.Settings + "\n[watch]\nevery_minutes = 5\nhours = \"00:00-24:00\"\nweekdays = false\n");
        using var now = new Fixture(Fixture.Settings + "\n[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\"\ndays = \"Mon-Sun\"\n");
        Assert.True(old.Config.ScheduleFromOldKeys);
        Assert.Equal(now.Config.WatchEveryMinutes, old.Config.WatchEveryMinutes);
        Assert.Equal(now.Config.Schedule.Fingerprint(TaskKind.Watcher), old.Config.Schedule.Fingerprint(TaskKind.Watcher));
        foreach (var t in new[] { Saturday, Monday.AddHours(3), Monday.AddHours(23).AddMinutes(59) })
            Assert.Equal(Watch.Due(now.Config, t), Watch.Due(old.Config, t));
    }

    [Fact]
    public void Both_forms_at_once_are_refused() =>
        Assert.Throws<SettingsException>(() => new Fixture(Fixture.Settings + "\n[watch]\nevery_minutes = 5\n\n[schedule]\nevery_minutes = 5\n"));

    [Fact]
    public void A_bad_schedule_is_refused()
    {
        Assert.Throws<SettingsException>(() => new Fixture(Fixture.Settings + "\n[schedule]\nevery_minute = 5\n"));
        Assert.Throws<SettingsException>(() => new Fixture(Fixture.Settings + "\n[schedule]\nafter_unlock_minutes = 1\n"));
    }
}

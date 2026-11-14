namespace ImagepixelBackup.Core.Tests;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 15, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 14, 15, 30, 0)));

    [Fact]
    public void Hourly_folder_without_backup_is_due()
    {
        var folder = new BackupFolder { Schedule = ScheduleKind.Hourly };

        Assert.True(BackupSchedule.IsDue(folder, Now));
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(600, true)]
    public void Hourly_folder_is_due_one_hour_after_the_last_attempt(int minutesAgo, bool expected)
    {
        var folder = new BackupFolder { Schedule = ScheduleKind.Hourly, LastAttempt = Now.AddMinutes(-minutesAgo) };

        Assert.Equal(expected, BackupSchedule.IsDue(folder, Now));
    }

    [Fact]
    public void Daily_folder_waits_for_its_time()
    {
        var folder = new BackupFolder
        {
            Schedule = ScheduleKind.Daily,
            DailyAt = new TimeOnly(18, 0),
            LastAttempt = Now.AddHours(-3),
        };

        Assert.False(BackupSchedule.IsDue(folder, Now));
        Assert.Equal(new TimeSpan(18, 0, 0), BackupSchedule.NextRun(folder, Now)!.Value.TimeOfDay);
        Assert.Equal(Now.Date, BackupSchedule.NextRun(folder, Now)!.Value.Date);
    }

    [Fact]
    public void Daily_folder_without_backup_is_due()
    {
        var folder = new BackupFolder { Schedule = ScheduleKind.Daily, DailyAt = new TimeOnly(18, 0) };

        Assert.True(BackupSchedule.IsDue(folder, Now));
    }

    [Fact]
    public void Daily_folder_makes_up_for_a_run_that_was_missed()
    {
        // the computer was off yesterday evening; it is before today's time now
        var folder = new BackupFolder
        {
            Schedule = ScheduleKind.Daily,
            DailyAt = new TimeOnly(18, 0),
            LastAttempt = Now.AddDays(-2),
        };

        Assert.True(BackupSchedule.IsDue(folder, Now));
        Assert.Equal(Now.Date.AddDays(-1), BackupSchedule.NextRun(folder, Now)!.Value.Date);
    }

    [Fact]
    public void Daily_folder_is_due_after_its_time_if_it_did_not_run_today()
    {
        var folder = new BackupFolder
        {
            Schedule = ScheduleKind.Daily,
            DailyAt = new TimeOnly(12, 0),
            LastAttempt = Now.AddDays(-1),
        };

        Assert.True(BackupSchedule.IsDue(folder, Now));
    }

    [Fact]
    public void Daily_folder_runs_only_once_per_day()
    {
        var folder = new BackupFolder
        {
            Schedule = ScheduleKind.Daily,
            DailyAt = new TimeOnly(12, 0),
            LastAttempt = Now.AddHours(-3),
        };

        Assert.False(BackupSchedule.IsDue(folder, Now));
        Assert.Equal(Now.Date.AddDays(1), BackupSchedule.NextRun(folder, Now)!.Value.Date);
    }

    [Fact]
    public void Manual_folder_is_never_due()
    {
        var folder = new BackupFolder { Schedule = ScheduleKind.Manual };

        Assert.False(BackupSchedule.IsDue(folder, Now));
        Assert.Null(BackupSchedule.NextRun(folder, Now));
    }
}

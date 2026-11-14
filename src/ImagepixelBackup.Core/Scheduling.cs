namespace ImagepixelBackup.Core;

/// <summary>Decides when the folders are due for their next backup.</summary>
public static class BackupSchedule
{
    public static readonly TimeSpan HourlyInterval = TimeSpan.FromHours(1);

    /// <summary>Returns whether a backup of the folder should start now.</summary>
    public static bool IsDue(BackupFolder folder, DateTimeOffset now) =>
        NextRun(folder, now) is { } next && next <= now;

    /// <summary>
    /// Returns when the next backup of the folder should start, or null if it is only
    /// backed up manually. A time in the past means the backup is overdue.
    /// </summary>
    public static DateTimeOffset? NextRun(BackupFolder folder, DateTimeOffset now)
    {
        switch (folder.Schedule)
        {
            case ScheduleKind.Hourly:
                return folder.LastAttempt is { } last ? last + HourlyInterval : now;

            case ScheduleKind.Daily:
                var local = now.ToLocalTime();
                var today = new DateTimeOffset(
                    local.Year, local.Month, local.Day,
                    folder.DailyAt.Hour, folder.DailyAt.Minute, 0, local.Offset);

                // The run of today is still open if there was no attempt since its start time.
                if (folder.LastAttempt is not { } attempt || attempt < today)
                {
                    return today;
                }

                return today.AddDays(1);

            default:
                return null;
        }
    }
}

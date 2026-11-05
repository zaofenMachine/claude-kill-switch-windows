namespace ClaudeSafe.Manager;

public sealed class BenchmarkManagerSnapshot
{
    public BenchmarkPreferences Preferences { get; set; } = new();
    public BenchmarkRun? Run { get; set; }
    public DateTimeOffset? NextDueUtc { get; set; }
    public bool Busy { get; set; }
    public string Message { get; set; } = "";
}

public static class BenchmarkSchedule
{
    public static DateTimeOffset? NextDue(DateTimeOffset now, BenchmarkPreferences preferences)
    {
        DesktopBenchmarkTargets.ValidatePreferences(preferences);
        return preferences.ScheduleEnabled ? now.AddHours(preferences.IntervalHours) : null;
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset? due, bool enabled, bool busy) =>
        enabled && !busy && due.HasValue && now >= due.Value;
}

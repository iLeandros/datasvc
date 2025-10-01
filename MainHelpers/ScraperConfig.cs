namespace DataSvc.MainHelpers;

// Config & date helpers
static class ScraperConfig
{
    public static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR")
        ?? "/var/lib/datasvc";

    public static readonly string BaseAddress = Environment.GetEnvironmentVariable("DATA_SOURCE_BASE")
        ?? "https://www.statarea.com/predictions";

    public static readonly string TimeZone = Environment.GetEnvironmentVariable("SCRAPER_TZ")
        ?? "Europe/Brussels";

    public static TimeZoneInfo Tz => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public static DateOnly TodayLocal() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, Tz).Date);

    public static IEnumerable<DateOnly> DateWindow(DateOnly center, int back = 3, int ahead = 3)
    {
        for (int d = -back; d <= ahead; d++)
            yield return center.AddDays(d);
    }

    public static string SnapshotPath(DateOnly date) =>
        Path.Combine(DataDir, "snapshots", $"{date:yyyy-MM-dd}.json");

    public static string UrlFor(DateOnly date) =>
        $"{BaseAddress.TrimEnd('/')}/date/{date:yyyy-MM-dd}/";
}

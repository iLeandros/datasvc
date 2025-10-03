using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataSvc.Likes;


public static class LikesCalculator
{
    public static long ComputeWithDateRules(
    string likesRaw,
    string hostTeam,
    string guestTeam,
    DateTime targetUtc,   // the date/time you want to use in the calc (UTC)
    DateTime nowUtc)      // "current" moment (UTC) – pass DateTime.UtcNow in prod
    {
        long likes = ParseLikesSafe(likesRaw);
        int teamFactor = LetterScore(hostTeam) + LastLetterScore(guestTeam);
        long timeFactor = GetTimeFactor(targetUtc, nowUtc);
    
        if (likes <= 0) likes = 1;
        if (teamFactor <= 0) teamFactor = 1;
        if (timeFactor <= 0) timeFactor = 1;
        //if (likes <= 0 || teamFactor <= 0 || timeFactor <= 0) return 0;
    
        try
        {
            checked { return likes * teamFactor * timeFactor; }
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Current day: hour(0..23)+1
    /// Past day: 24
    /// Future day: round( (hour+1) / daysAhead ), clamped to [1,24]
    /// </summary>
    private static long GetTimeFactor(DateTime targetUtc, DateTime nowUtc)
    {
        // ensure UTC (in case callers slipped a Kind=Unspecified/Local)
        if (targetUtc.Kind != DateTimeKind.Utc) targetUtc = DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc);
        if (nowUtc.Kind    != DateTimeKind.Utc) nowUtc    = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
    
        int hour = targetUtc.Hour + 1; // 1..24
        int daysDiff = (int)(targetUtc.Date - nowUtc.Date).TotalDays;
    
        if (daysDiff < 0)
            return 24; // previous date → max weight
    
        if (daysDiff == 0)
            return hour; // same date → use current hour weight
    
        // future date → divide by days ahead
        double raw = (double)hour / daysDiff;
        // round to nearest integer, halves away from zero, then clamp to [1,24]
        long rounded = (long)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Max(1, Math.Min(24, rounded));
    }

    public static long Compute(string likesRaw, string hostTeam, string guestTeam, DateTime utcNow)
    {
        long likes = ParseLikesSafe(likesRaw);
        int teamFactor = LetterScore(hostTeam) * LastLetterScore(guestTeam);

        // GMT+0 (UTC) hour multiplier: 1..24 (so 00:00 -> 1, 23:00 -> 24)
        long timeFactor = utcNow.Hour + 1;

        if (likes <= 0) likes = 1;
        if (teamFactor <= 0) teamFactor = 1;
        //if (likes <= 0 || teamFactor <= 0) return 0;

        try
        {
            checked
            {
                return likes * teamFactor * timeFactor;
            }
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    public static string ToCompact(long n, CultureInfo culture)
    {
        if (n >= 1_000_000_000) return (n / 1_000_000_000d).ToString("0.#", culture) + "B";
        if (n >= 1_000_000)     return (n / 1_000_000d).ToString("0.#", culture) + "M";
        if (n >= 1_000)         return (n / 1_000d).ToString("0.#", culture) + "k";
        return n.ToString("0", culture);
    }

    private static long ParseLikesSafe(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;

        if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowThousands,
                          CultureInfo.InvariantCulture, out var n)) return Math.Max(0, n);

        if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowThousands,
                          CultureInfo.CurrentCulture, out n)) return Math.Max(0, n);

        var digitsOnly = Regex.Replace(s, "[^0-9-]", "");
        return long.TryParse(digitsOnly, out n) ? Math.Max(0, n) : 0;
    }

    // A=1..Z=26; first A–Z found, else 0
    private static int LetterScore(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 1;
        foreach (var ch in s.Trim().ToUpperInvariant())
            if (ch >= 'A' && ch <= 'Z') return ch - 'A' + 1;
        return 1;
    }
    private static int LastLetterScore(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 1;
    
        var span = s.AsSpan().Trim();
        for (int i = span.Length - 1; i >= 0; i--)
        {
            char ch = char.ToUpperInvariant(span[i]);
            if (ch >= 'A' && ch <= 'Z')
                return ch - 'A' + 1; // A=1 .. Z=26
        }
        return 1;
    }
}

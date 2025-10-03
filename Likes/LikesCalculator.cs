using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataSvc.Likes;


public static class LikesCalculator
{
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

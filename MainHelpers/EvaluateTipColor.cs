using System.Linq;
using DataSvc.ModelHelperCalls;
using DataSvc.Models;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Maui.Graphics;

namespace DataSvc.MainHelpers;

public static class EvaluationHelper
{
    public static Microsoft.Maui.Graphics.Color EvaluateTipColor(LiveTableDataItemDto live, TableDataItem? item, int home, int away)
    {
        if (home < 0 || away < 0 || live is null) return Microsoft.Maui.Graphics.Colors.Black;
    
        // --- normalize tip cheaply ---
        // Upper, trim, remove spaces, unify BTTS->BTS
        string ts = (item?.Tip ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace("BTTS", "BTS")
            .Replace(" ", string.Empty);
    
        bool isFinal = IsFinal(live?.LiveTime);
        int? minute = TimeStringToMinutes(live?.LiveTime);
        int total = home + away;
    
        // detect halftime market prefix "HT"
        // detect halftime O/U prefix "HT" ONLY when followed by O or U (e.g., HTO2.5 / HTU2.5)
        bool isHTMarket = ts.Length >= 3
            && ts[0] == 'H' && ts[1] == 'T'
            && (ts[2] == 'O' || ts[2] == 'U');
        if (isHTMarket) ts = ts.Substring(2); // now ts starts with 'O' or 'U'
    
    
        // ---------- O/U ----------
        // Fast parse: O{thr} / U{thr}, thr like "2.5"
        if (ts.Length >= 2 && (ts[0] == 'O' || ts[0] == 'U') && TryParseThreshold(ts.AsSpan(1), out double thr))
        {
            bool isOver = ts[0] == 'O';
    
            // --- Early resolution rules ---
            // Over: if already above the threshold at any time => permanently won.
            if (isOver && total > thr) return Microsoft.Maui.Graphics.Colors.Green;
    
            // Under: if already above the threshold at any time => permanently lost.
            if (!isOver && total > thr) return Microsoft.Maui.Graphics.Colors.Red;
    
            // For HT markets: only judge at/before HT; otherwise pending.
            if (isHTMarket)
            {
                if (!minute.HasValue) return Microsoft.Maui.Graphics.Colors.Black; // no clock yet
                bool atHT = string.Equals(live?.LiveTime?.Trim(), "HT", StringComparison.OrdinalIgnoreCase)
                            || minute.Value >= 45; // treat 45+ as HT boundary
    
                if (!atHT) return Microsoft.Maui.Graphics.Colors.Black; // still pending before HT whistle
    
                // decide at HT whistle
                return isOver ? (total > thr ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red)
                              : (total < thr ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red);
            }
    
            // Full-time O/U: decide at FT if not early-resolved
            if (!isFinal) return Microsoft.Maui.Graphics.Colors.Black;
    
            return isOver ? (total > thr ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red)
                          : (total < thr ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red);
        }
    
        // ---------- Other markets ----------
        if (!isFinal)
        {
            // Markets that can turn later stay pending (except the “early green” ones below)
            // 1/X/2, 1X/X2/12, OTS all wait for FT
            // BTS/HTS/GTS can go green early (handled below)
            switch (ts)
            {
                // 1X2 — evaluate only at full time
                case "HTS": 
                    if(home > 0) return Microsoft.Maui.Graphics.Colors.Green;
                    break;
                case "GTS": 
                    if(away > 0) return Microsoft.Maui.Graphics.Colors.Green;
                    break;
                case "BTS": 
                    if (home > 0 && away > 0) return Microsoft.Maui.Graphics.Colors.Green;
                    break;
            }
        }
    
        switch (ts)
        {
            // 1X2 — evaluate only at full time
            case "1": return isFinal ? (home > away ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
            case "X": return isFinal ? (home == away ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
            case "2": return isFinal ? (away > home ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
    
            // Double chance — evaluate only at full time
            case "1X": return isFinal ? (home >= away ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
            case "X2": return isFinal ? (away >= home ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
            case "12": return isFinal ? (home != away ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Red) : Microsoft.Maui.Graphics.Colors.Black;
    
            // Both teams to score — green as soon as both have scored; red at FT if not
            case "BTS":
                if (home > 0 && away > 0) return Microsoft.Maui.Graphics.Colors.Green;
                return isFinal ? Microsoft.Maui.Graphics.Colors.Red : Microsoft.Maui.Graphics.Colors.Black;
    
            // Only one team scores — red as soon as both score; otherwise green only at FT if one stayed 0
            case "OTS":
                if (home > 0 && away > 0) return Microsoft.Maui.Graphics.Colors.Red;
                return isFinal ? Microsoft.Maui.Graphics.Colors.Green : Microsoft.Maui.Graphics.Colors.Black;
    
            // Home team to score — green as soon as they score; red at FT if never
            case "HTS":
                if (home > 0) return Microsoft.Maui.Graphics.Colors.Green;
                return isFinal ? Microsoft.Maui.Graphics.Colors.Red : Microsoft.Maui.Graphics.Colors.Black;
    
            // Guest/Away team to score — same early-green idea
            case "GTS":
                if (away > 0) return Microsoft.Maui.Graphics.Colors.Green;
                return isFinal ? Microsoft.Maui.Graphics.Colors.Red : Microsoft.Maui.Graphics.Colors.Black;
    
            default:
                return Microsoft.Maui.Graphics.Colors.Black;
        }
    }
    public static bool TryParseThreshold(ReadOnlySpan<char> s, out double thr)
        => double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out thr);

    public static int? TimeStringToMinutes(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
    
        // Common tokens
        if (t.Equals("HT", StringComparison.OrdinalIgnoreCase)) return 45;
        if (t.Equals("FT", StringComparison.OrdinalIgnoreCase) || t.Equals("FIN", StringComparison.OrdinalIgnoreCase)) return 90;
        if (t.Equals("ET", StringComparison.OrdinalIgnoreCase)) return 105; // tweak/remove if you prefer pushing ET to end
    
        // Live minute: "55'", "90+2'", "45+1"
        var m = Regex.Match(t, @"^(?<b>\d{1,3})(?:\+(?<e>\d{1,2}))?[' ]*$");
        if (m.Success)
        {
            int b = int.Parse(m.Groups["b"].Value);
            int e = m.Groups["e"].Success ? int.Parse(m.Groups["e"].Value) : 0;
            return b + e;
        }
    
        // Clock time: "14:30"
        m = Regex.Match(t, @"^(?<H>\d{1,2}):(?<M>\d{2})$");
        if (m.Success)
        {
            int h = int.Parse(m.Groups["H"].Value);
            int min = int.Parse(m.Groups["M"].Value);
            return h * 60 + min;
        }
    
        // Fallback: first digits anywhere ("23’" -> 23). If none, push to end.
        return int.TryParse(new string(t.Where(char.IsDigit).ToArray()), out var v) ? v : int.MaxValue;
    }
    public static bool OUHolds(System.Text.RegularExpressions.Match ouMatch, int total)
    {
        var kind = ouMatch.Groups["kind"].Value; // "O" or "U"
        var thr = double.Parse(ouMatch.Groups["thr"].Value, CultureInfo.InvariantCulture);
        //Debug.WriteLine($"Evaluating O/U tip: {kind}{thr}, total={total}");
        return kind == "O" ? total > thr : total < thr; // Over strictly greater; Under strictly less
    }
    public static bool IsFinal(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToLowerInvariant();
    
        // common finals across feeds
        return s is "fin" or "ft" or "ended" or "finished" or "awrd" or "post" or "canc";
    }

    public static bool TryGetScores(string? s1, string? s2, out int home, out int away)
    {
        home = away = -1;
    
        // Adjust these property names if yours differ
        //var s1 = item.ScoreTeamOne?.Trim();
        //var s2 = item.ScoreTeamTwo?.Trim();
    
        if (int.TryParse(s1, out var h) && int.TryParse(s2, out var a))
        {
            home = h; away = a;
            return true;
        }
        return false;
    }
}

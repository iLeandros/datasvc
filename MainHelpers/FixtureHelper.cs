using System.Linq;
using DataSvc.ModelHelperCalls;
using System;
using System.Collections.Generic;
using System.Text;
using DataSvc.Models;

namespace DataSvc.MainHelpers;

    public class FixtureHelper
    {
        public static readonly string[] TeamStopwords = new[]
        {
            "fc","cf","sc","ac","ca","afc","u","u21","ii","2","w","women","ladies",
            "club","de","cd","atletico","atl","sport","sports","sp","deportivo","sd",
            // "united","city",   <-- delete these two
            "team","sv","ifs","bk","fk","ik","sk","as","aas","cs","nk"
        };

        private static string RemoveDiacritics(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var norm = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length);
            foreach (var ch in norm)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
        /*
        public static string Canon(string? s)
        {
            s ??= string.Empty;
        
            // normalize base string
            s = RemoveDiacritics(s.ToLowerInvariant());
        
            // replace punctuation with spaces, collapse whitespace
            var cleaned = new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray());
        
            var tokens = cleaned
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        
            if (tokens.Count == 0) return string.Empty;
        
            // ---- expand common abbreviations / aliases (Elo vs fixture feeds) ----
        
            // normalize "utd" -> "united"
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "utd") tokens[i] = "united";
                if (tokens[i] == "nottm") tokens[i] = "nottingham";
            }
        
            // "man city" / "man united" -> "manchester ..."
            if (tokens.Count >= 2 && tokens[0] == "man")
            {
                if (tokens[1] == "city")
                {
                    tokens[0] = "manchester";
                }
                else if (tokens[1] == "united")
                {
                    tokens[0] = "manchester";
                }
            }
        
            // Elo has "Forest" meaning "Nottingham Forest" in your dataset
            // (only when the whole name is exactly "forest")
            if (tokens.Count == 1 && tokens[0] == "forest")
            {
                tokens = new List<string> { "nottingham", "forest" };
            }
        
            // remove generic stopwords (but keep city/united now)
            var filtered = tokens
                .Where(t => !TeamStopwords.Contains(t))
                .ToArray();
        
            return string.Join(' ', filtered);
        }
        */
        public static string Canon(string? s)
        {
            s ??= string.Empty;
        
            s = RemoveDiacritics(s.ToLowerInvariant());
            var cleaned = new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray());
        
            var tokens = cleaned
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        
            if (tokens.Count == 0) return string.Empty;
        
            // ---- expand common abbreviations / aliases (Elo vs fixture feeds) ----
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "utd") tokens[i] = "united";
                if (tokens[i] == "nottm") tokens[i] = "nottingham";
        
                // NEW: "weds" -> "wednesday" (and a common typo)
                if (tokens[i] == "weds" || tokens[i] == "wed")
                    tokens[i] = "wednesday";
            }
        
            // "man city" / "man united" -> "manchester ..."
            if (tokens.Count >= 2 && tokens[0] == "man")
            {
                if (tokens[1] == "city" || tokens[1] == "united")
                    tokens[0] = "manchester";
            }
        
            // Elo has "Forest" meaning "Nottingham Forest" in your dataset (only exact)
            if (tokens.Count == 1 && tokens[0] == "forest")
                tokens = new List<string> { "nottingham", "forest" };
            
            if (tokens.Count == 1 && tokens[0] == "stoke")
                tokens = new List<string> { "stoke", "city" };

            if (tokens.Count == 1 && tokens[0] == "hull")
                tokens = new List<string> { "hull", "city" };

            if (tokens.Count == 1 && tokens[0] == "oxford")
                tokens = new List<string> { "oxford", "united" };

            if (tokens.Count == 1 && tokens[0] == "swansea")
                tokens = new List<string> { "swansea", "city" };
        
            // NEW: drop decorative suffixes that Elo often omits (keep if sole token)
            // Keep "city" and "united" (do not reintroduce earlier issue).
            var dropSuffixes = new HashSet<string>
            {
                "rovers","athletic","hotspur","wanderers","orient",
                "stanley","albion","vale","alexandra","county","town"
            };
            if (tokens.Count > 1)
                tokens = tokens.Where(t => !dropSuffixes.Contains(t)).ToList();
        
            // remove generic stopwords (but keep city/united now)
            var filtered = tokens.Where(t => !TeamStopwords.Contains(t)).ToArray();
        
            return string.Join(' ', filtered);
        }

        public static HashSet<string> TokenSet(string s) =>
            Canon(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        public static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1.0;
            int inter = a.Intersect(b).Count();
            int union = a.Count + b.Count - inter;
            return union == 0 ? 0 : (double)inter / union;
        }

        // Replace your current JaroWinkler proxy with this:
        public static double JaroWinkler(string s1, string s2)
        {
            // Distinct characters → HashSet<string> so it matches Jaccard<string>
            var a = new HashSet<string>(s1.Distinct().Select(c => c.ToString()));
            var b = new HashSet<string>(s2.Distinct().Select(c => c.ToString()));
            return Jaccard(a, b);
        }

        public static TimeSpan? ParseKick(string? hhmm)
        {
            if (TimeSpan.TryParse(hhmm, out var t)) return t;
            return null;
        }

        public static bool CloseKick(TimeSpan? a, TimeSpan? b, int minutes = 30)
        {
            if (a is null || b is null) return true; // tolerate when time missing
            return Math.Abs((a.Value - b.Value).TotalMinutes) <= minutes;
        }

    }

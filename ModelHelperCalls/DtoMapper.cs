using System.Collections.ObjectModel;
using System.Globalization;
using DataSvc.Models;

namespace DataSvc.ModelHelperCalls
{
    public static class DtoMapper
    {
        public static ObservableCollection<LiveTableDataGroupDto> Map(LiveScoresResponse src)
        {
            var groups = new ObservableCollection<LiveTableDataGroupDto>();
            if (src?.Groups == null) return groups;

            foreach (var g in src.Groups)
            {
                var items = new ObservableCollection<LiveTableDataItemDto>();
                foreach (var m in g.Matches ?? Enumerable.Empty<LiveScoreItemResponse>())
                {
                    items.Add(new LiveTableDataItemDto
                    {
                        Time = m.Time?.Trim(),
                        LiveTime = m.Status?.Trim(),
                        LiveTimeBgColor = LiveStatusColor(m.Status),
                        HomeTeam = m.HomeTeam,
                        HomeGoals = m.HomeGoals,
                        AwayGoals = m.AwayGoals,
                        AwayTeam = m.AwayTeam,
                        HalfTimeHomeGoals = ParseNullableInt(m.HalfTimeHomeGoals),
                        HalfTimeAwayGoals = ParseNullableInt(m.HalfTimeAwayGoals),
                        Action = m.Action,
                        MatchID = m.MatchID
                    });
                }

                if (items.Count > 0)
                    groups.Add(new LiveTableDataGroupDto("DarkSlateGray", g.Competition, items));
            }

            return groups;
        }

        private static int? ParseNullableInt(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static string LiveStatusColor(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Black";
            var s = status.Trim().ToLowerInvariant();
            if (s == "pen") return "Red";
            if (s == "et")  return "DarkRed";
            if (s == "ht")  return "Orange";
            if (s.Any(char.IsDigit)) return "DarkGreen";
            return "Black";
        }
    }
}

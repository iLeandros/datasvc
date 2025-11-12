using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HtmlAgilityPack;
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
                        // default color logic; tweak if you want PEN/ET/FIN specific colors
                        LiveTimeBgColor = LiveStatusColor(m.Status),
                        HomeTeam = m.HomeTeam,
                        HomeGoals = m.HomeGoals,
                        AwayGoals = m.AwayGoals,
                        AwayTeam = m.AwayTeam,
                        Action = m.Action,
                        MatchID = m.MatchID
                    });
                }
    
                if (items.Count > 0)
                    groups.Add(new LiveTableDataGroupDto(Colors.DarkSlateGray, g.Competition, items));
            }
    
            return groups;
        }
    
        private static Color LiveStatusColor(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return Colors.Black;
            var s = status.Trim().ToLowerInvariant();
            if (s == "pen") return Colors.Red;
            if (s == "et") return Colors.DarkRed;
            if (s == "ht") return Colors.Orange;
            if (s.Any(char.IsDigit)) return Colors.DarkGreen; // in-play minutes
            return Colors.Black; // fin/awrd/post/sched/canc
        }
    }
}

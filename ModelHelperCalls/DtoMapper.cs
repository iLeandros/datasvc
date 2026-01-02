using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Maui.Graphics;
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
                        HalfTimeHomeGoals = ParseNullableInt(m.HalfTimeHomeGoals),
                        HalfTimeAwayGoals = ParseNullableInt(m.HalfTimeAwayGoals),
                        Action = m.Action,
                        MatchID = m.MatchID
                    });
                }
    
                if (items.Count > 0)
                    groups.Add(new LiveTableDataGroupDto(Microsoft.Maui.Graphics.Colors.DarkSlateGray, g.Competition, items));
            }
    
            return groups;
        }
    
        private static int? ParseNullableInt(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static Microsoft.Maui.Graphics.Color LiveStatusColor(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return Microsoft.Maui.Graphics.Colors.Black;
            var s = status.Trim().ToLowerInvariant();
            if (s == "pen") return Microsoft.Maui.Graphics.Colors.Red;
            if (s == "et") return Microsoft.Maui.Graphics.Colors.DarkRed;
            if (s == "ht") return Microsoft.Maui.Graphics.Colors.Orange;
            if (s.Any(char.IsDigit)) return Microsoft.Maui.Graphics.Colors.DarkGreen; // in-play minutes
            return Microsoft.Maui.Graphics.Colors.Black; // fin/awrd/post/sched/canc
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using MySqlConnector;
using DataSvc.Models;

namespace DataSvc.Likes;

public static class VoteMixing
{
    public static async Task ApplyUserVotesAsync(
        ObservableCollection<TableDataGroup> groups,
        DateTime whenUtc,
        DateTime nowUtc,
        MySqlConnection conn,
        CancellationToken ct)
    {
        if (groups is null || groups.Count == 0) return;

        // 1) Collect ALL hrefs across all groups (deduped)
        var hrefs = groups
            .SelectMany(g => g?.Items ?? Enumerable.Empty<TableDataItem>())
            .Select(i => i.Href)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hrefs.Length == 0) return;

        // 2) Bulk fetch href -> score (up - down)
        var scores = await VoteTotalsRepo.GetScoresByHrefAsync(conn, hrefs, ct);

        // 3) Recompute each item: calculator on scraped likes ONLY, then add user score AFTER
        foreach (var item in groups.SelectMany(g => g?.Items ?? Enumerable.Empty<TableDataItem>()))
        {
            var likesRaw  = item.LikePositive ?? "0";
            var hostName  = item.HostTeam     ?? string.Empty;
            var guestName = item.GuestTeam    ?? string.Empty;
        
            // Parse scraped likes
            long baseLikes = 0;
            long.TryParse(likesRaw, out baseLikes);
        
            // 3a) Run your existing rules/calculator using only scraped likes
            var computedFromScrape = LikesCalculator.ComputeWithDateRules(
                baseLikes.ToString(CultureInfo.InvariantCulture),
                hostName, guestName, whenUtc, nowUtc);
        
            // 3b) Add user votes AFTER the multiplier logic so they are not multiplied
            var bonusScore = (item.Href is not null && scores.TryGetValue(item.Href, out var s)) ? s : 0; // up - down
        
            // Final value (no weighting, not multiplied)
            var finalValue = Math.Max(0, computedFromScrape + bonusScore);
        
            item.ServerComputedLikes = finalValue;
            item.ServerComputedLikesFormatted = LikesCalculator.ToCompact(finalValue, CultureInfo.InvariantCulture);
        }
    }
}

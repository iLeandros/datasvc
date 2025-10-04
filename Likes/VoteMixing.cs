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

        // 3) Recompute each item using (scrapedLikes + score) then LikesCalculator
        foreach (var item in groups.SelectMany(g => g?.Items ?? Enumerable.Empty<TableDataItem>()))
        {
            var likesRaw  = item.LikePositive ?? "0";
            var hostName  = item.HostTeam     ?? string.Empty;
            var guestName = item.GuestTeam    ?? string.Empty;

            long baseLikes = 0;
            long.TryParse(likesRaw, out baseLikes);

            var bonus = (item.Href is not null && scores.TryGetValue(item.Href, out var s)) ? s : 0;
            var effectiveLikes = Math.Max(0, baseLikes + bonus);

            var computed = LikesCalculator.ComputeWithDateRules(
                effectiveLikes.ToString(CultureInfo.InvariantCulture),
                hostName, guestName, whenUtc, nowUtc);

            item.ServerComputedLikes = computed;
            item.ServerComputedLikesFormatted =
                LikesCalculator.ToCompact(computed, CultureInfo.InvariantCulture);
        }
    }
}

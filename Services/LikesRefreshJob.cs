using System.Globalization;
using DataSvc.Models;
using DataSvc.Likes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataSvc.Services;

public sealed class LikesRefreshJob : BackgroundService
{
    private readonly ILogger<LikesRefreshJob> _log;
    private readonly ResultStore _root;                   // current “main” snapshot
    private readonly SnapshotPerDateStore _perDate;       // per-date store

    private readonly TimeSpan _period;

    public LikesRefreshJob(
        ILogger<LikesRefreshJob> log,
        ResultStore root,
        SnapshotPerDateStore perDateStore)
    {
        _log = log;
        _root = root;
        _perDate = perDateStore;

        // default hourly; override with env var LIKES_REFRESH_MIN=5 etc.
        var minutes = Environment.GetEnvironmentVariable("LIKES_REFRESH_MIN");
        _period = TimeSpan.FromMinutes(int.TryParse(minutes, out var m) && m > 0 ? m : 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("LikesRefreshJob starting; period={Period}", _period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;

                // 1) Recompute for the “current” snapshot (main page)
                var current = _root.Current; // guarded by store internally
                if (current?.Payload?.TableDataGroup is not null)
                {
                    // For the rolling “current” snapshot, treat the target as NOW (same-day behavior).
                    RecomputeLikesForGroups(current.Payload.TableDataGroup, nowUtc /*whenUtc*/, nowUtc /*nowUtc*/);
                }

                // 2) Recompute for per-date snapshots in today±3 window (same window used elsewhere)
                var center = ScraperConfig.TodayLocal();
                foreach (var d in ScraperConfig.DateWindow(center, 3, 3))
                {
                    if (_perDate.TryGet(d, out var snap) && snap?.Payload?.TableDataGroup is not null)
                    {
                        // Per-date: use that date’s midnight UTC as the "chosen UTC moment"
                        var whenUtc = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                        RecomputeLikesForGroups(snap.Payload.TableDataGroup, whenUtc, nowUtc);
                    }
                }

                _log.LogDebug("LikesRefreshJob pass complete at {NowUtc:o}", nowUtc);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LikesRefreshJob failed pass");
            }

            try { await Task.Delay(_period, stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }

        _log.LogInformation("LikesRefreshJob stopping");
    }

    private static void RecomputeLikesForGroups(
        System.Collections.ObjectModel.ObservableCollection<TableDataGroup> groups,
        DateTime whenUtc,
        DateTime nowUtc)
    {
        foreach (var g in groups ?? Enumerable.Empty<TableDataGroup>())
        {
            foreach (var item in g?.Items ?? Enumerable.Empty<TableDataItem>())
            {
                var likesRaw  = item.LikePositive ?? "0";
                var hostName  = item.HostTeam     ?? string.Empty;
                var guestName = item.GuestTeam    ?? string.Empty;

                // same rule used at parse time
                var computed = LikesCalculator.ComputeWithDateRules(likesRaw, hostName, guestName, whenUtc, nowUtc);
                item.ServerComputedLikes = computed;
                item.ServerComputedLikesFormatted = LikesCalculator.ToCompact(computed, CultureInfo.InvariantCulture);
            }
        }
    }
}

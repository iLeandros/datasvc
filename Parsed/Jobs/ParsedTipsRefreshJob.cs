// Parsed/Jobs/ParsedTipsRefreshJob.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth;
using DataSvc.MainHelpers;
using DataSvc.Likes;
using DataSvc.Services;
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;

namespace DataSvc.Parsed;

public sealed class ParsedTipsRefreshJob : BackgroundService
{
    private readonly SnapshotPerDateStore _perDate;
    private readonly ParsedTipsService _tips;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ParsedTipsRefreshJob(SnapshotPerDateStore perDateStore, ParsedTipsService tips)
    {
        _perDate = perDateStore;
        _tips = tips;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            await RunOnceSafe(stoppingToken); // initial run
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceSafe(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOnceSafe(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var center = ScraperConfig.TodayLocal();
            foreach (var d in ScraperConfig.DateWindow(center, back: 3, ahead: 3))
            {
                if (!_perDate.TryGet(d, out var snap) || snap?.Payload?.TableDataGroup is null || snap.Payload.TableDataGroup.Count == 0)
                    continue;

                // Past dates: skip if every item that can have a tip already has VIPTip set.
                // Re-running ApplyTipsForDate on past dates risks overwriting correct H2H/Elo
                // analysis with weaker values when DetailsStore isn't fully loaded (e.g. after
                // a service restart). Past results are final — freeze them once complete.
                if (d < center)
                {
                    bool allComplete = snap.Payload.TableDataGroup
                        .SelectMany(g => g?.Items ?? Enumerable.Empty<TableDataItem>())
                        .All(item => item.VIPTip != null);
                    if (allComplete) continue;
                }

                await _tips.ApplyTipsForDate(d, snap.Payload.TableDataGroup, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

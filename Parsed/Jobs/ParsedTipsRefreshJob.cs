// Parsed/Jobs/ParsedTipsRefreshJob.cs
using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;


namespace DataSvc.Parsed;

public sealed class ParsedTipsRefreshJob : BackgroundService
{
    private readonly SnapshotPerDateStore _perDate;
    private readonly ParsedTipsService _tips;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ParsedTipsRefreshJob(
        SnapshotPerDateStore perDateStore,
        ParsedTipsService tips)
    {
        _perDate = perDateStore;
        _tips = tips;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // same 5-min cadence pattern as your other jobs
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            // initial run
            await RunOnceSafe(stoppingToken);

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
            var center = ScraperConfig.TodayLocal(); // you already use this for windows
            foreach (var d in ScraperConfig.DateWindow(center, back: 3, ahead: 3))
            {
                if (!_perDate.TryGet(d, out var snap) || snap?.Payload?.TableDataGroup is null || snap.Payload.TableDataGroup.Count == 0)
                    continue;

                await _tips.ApplyTipsForDate(d, snap.Payload.TableDataGroup, ct); // exact signature in file
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

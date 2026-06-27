// Tips/TipsRefreshJob.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth; // AuthController + SessionAuthHandler namespace
using DataSvc.MainHelpers; // MainHelpers
using DataSvc.Likes; // MainHelpers
using DataSvc.Services; // Services
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.MainHelpers;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;

namespace DataSvc.Tips;

public sealed class TipsRefreshJob : BackgroundService
{
    private readonly TipsScraperService _svc;
    private readonly TipsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeZoneInfo _tz;

    public TipsRefreshJob(TipsScraperService svc, TipsStore store)
    {
        _svc = svc;
        _store = store;
        var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
        _tz = !string.IsNullOrWhiteSpace(tzId)
            ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
            : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm from disk
        var prev = await TipsFiles.LoadAsync();
        if (prev is not null) _store.Set(prev);

        // Initial run
        await RunSafelyOnce(stoppingToken);

        // Hourly tick aligned to TOP_OF_HOUR_TZ
        _ = HourlyLoop(stoppingToken);

        // 5-minute cadence
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyOnce(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task HourlyLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
                int nextHour = nowLocal.Minute == 0 && nowLocal.Second == 0 ? nowLocal.Hour : nowLocal.Hour + 1;
                if (nextHour == 24) nextHour = 0;
                var nextTopLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, nextHour, 0, 0, nowLocal.Offset);
                if (nextTopLocal <= nowLocal) nextTopLocal = nextTopLocal.AddHours(1);

                var delay = nextTopLocal - nowLocal;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await RunSafelyOnce(ct);

                using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                while (await hourly.WaitForNextTickAsync(ct))
                    await RunSafelyOnce(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try { await _svc.FetchAndStoreAsync(ct); }
        catch (Exception ex) { Debug.WriteLine($"[tips] run failed: {ex}"); }
        finally { _gate.Release(); }
    }
}

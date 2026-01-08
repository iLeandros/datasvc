// LiveScores/Jobs/LiveScoresRefreshJob.cs
using System;
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

namespace DataSvc.LiveScores;

public sealed class LiveScoresRefreshJob : BackgroundService
{
    private readonly LiveScoresScraperService _svc;
    private readonly LiveScoresStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LiveScoresRefreshJob(LiveScoresScraperService svc, LiveScoresStore store)
    {
        _svc = svc; _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prev = await LiveScoresFiles.LoadAsync();
        if (prev is not null) _store.Import(prev.Value.days);

        await RunSafelyOnce(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyOnce(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSafelyOnce(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try { await _svc.FetchAndStoreAsync(ct); }
        catch { /* keep last good */ }
        finally { _gate.Release(); }
    }
}

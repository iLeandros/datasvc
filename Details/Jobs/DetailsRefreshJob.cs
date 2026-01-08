// Details/Jobs/DetailsRefreshJob.cs
using System;
using System.Threading;
using System.Threading.Tasks;

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

using Microsoft.Extensions.Hosting;

namespace DataSvc.Details
{
    public sealed class DetailsRefreshJob : BackgroundService
    {
        private readonly DetailsScraperService _svc;
        private readonly DetailsStore _store;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TimeZoneInfo _tz;
    	private readonly DetailsRefreshService _refresher; // <— add
    
        public DetailsRefreshJob(DetailsScraperService svc, DetailsStore store, DetailsRefreshService refresher)
        {
            _svc = svc; 
            _store = store;
    		_refresher = refresher; // <— add
            // Use local server timezone by default; allow override via env var TOP_OF_HOUR_TZ (e.g., "Europe/Brussels")
            var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
            _tz = !string.IsNullOrWhiteSpace(tzId)
                ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
                : TimeZoneInfo.Local;
        }
    
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Load previous cache on startup
            var prev = await DetailsFiles.LoadAsync();
            if (prev.Count > 0) _store.Import(prev);
    
            // Let the main page warm up first
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }
    
            // Initial run
            await RunSafelyOnce("initial", stoppingToken);
    
            // Start hourly aligned loop in parallel
            _ = HourlyLoop(stoppingToken);
    
            // Every 5 minutes — keep existing cadence
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await RunSafelyOnce("tick", stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
        }
    
        private async Task HourlyLoop(CancellationToken ct)
        {
            try
            {
                // Wait until the next top of the hour in the configured timezone
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
    
                    await RunSafelyOnce("hourly", ct);
    
                    // After the first aligned tick, continue hourly
                    using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
                    while (await hourly.WaitForNextTickAsync(ct))
                    {
                        await RunSafelyOnce("hourly", ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    
        private async Task RunSafelyOnce(string reason, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct)) return;
            try
            {
                // Keep the fast “current day” details refresh…
                var r = await _svc.RefreshAllFromCurrentAsync(ct);
                Debug.WriteLine($"[details] {reason} current refreshed={r.Refreshed} skipped={r.Skipped} errors={r.Errors.Count}");
    
                // …and then refresh details for all hrefs that appear across parsed D±3:
                await _refresher.RefreshAllFromParsedWindowAsync(back: 3, ahead: 3, maxConcurrency: 8, ct: ct);
                Debug.WriteLine($"[details] {reason} window D±3 refresh completed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[details] {reason} failed: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

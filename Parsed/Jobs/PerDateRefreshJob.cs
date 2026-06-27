// Parsed/Jobs/PerDateRefreshJob.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DataSvc.MainHelpers;

namespace DataSvc.Parsed;

// FIX C6: Converted from IHostedService + System.Threading.Timer (async void callback)
// to BackgroundService + PeriodicTimer — exceptions are properly observed.
public sealed class PerDateRefreshJob : BackgroundService
{
    private readonly SnapshotPerDateStore _store;
    private readonly ILogger<PerDateRefreshJob> _log;
    private readonly IConfiguration _cfg;
    private readonly ParsedTipsService _tips;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PerDateRefreshJob(
        SnapshotPerDateStore store,
        ILogger<PerDateRefreshJob> log,
        IConfiguration cfg,
        ParsedTipsService tips)
    {
        _store = store;
        _log = log;
        _cfg = cfg;
        _tips = tips;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short startup delay so the main page can warm up first
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Initial run immediately after startup delay
        await TickAsync(stoppingToken);

        // Then run every 5 minutes
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var center = ScraperConfig.TodayLocal();
            var hourUtc = DateTime.UtcNow.Hour;

            var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
                store: _store,
                cfg: _cfg,
                tips: _tips,
                hourUtc: hourUtc,
                center: center,
                back: 3,
                ahead: 3,
                ct: ct);

            if (errors.Count > 0)
                _log.LogWarning("PerDate refresh had {Count} error(s): {Errors}",
                    errors.Count, string.Join("; ", errors.Select(kv => $"{kv.Key}:{kv.Value}")));
            else
                _log.LogDebug("PerDate refresh OK: {Count} date(s) refreshed", refreshed.Count);

            BulkRefresh.CleanupRetention(_store, center, 3, 3);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PerDate refresh tick failed");
        }
        finally
        {
            _gate.Release();
        }
    }
}

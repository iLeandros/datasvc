// Parsed/Jobs/PerDateRefreshJob.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace DataSvc.Parsed;

public sealed class PerDateRefreshJob : IHostedService, IDisposable
{
    private readonly SnapshotPerDateStore _store;
    private readonly ILogger<PerDateRefreshJob> _log;
    private readonly IConfiguration _cfg;          // <-- inject cfg
	private readonly ParsedTipsService _tips;    // <-- add
	
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PerDateRefreshJob(
        SnapshotPerDateStore store,
        ILogger<PerDateRefreshJob> log,
        IConfiguration cfg,
		ParsedTipsService tips)                       // <-- DI will supply this
    {
        _store = store;
        _log = log;
        _cfg = cfg;
		_tips = tips;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Initial run shortly after startup; then every 5 minutes
        _timer = new Timer(async _ => await TickAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    private async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var center = ScraperConfig.TodayLocal();
			var hourUtc = DateTime.UtcNow.Hour;

            // *** pass cfg as 2nd arg, keep parameter order (or use named args) ***
            var (refreshed, errors) = await BulkRefresh.RefreshWindowAsync(
			    store: _store,
			    cfg:   _cfg,
				tips:   _tips, 
			    hourUtc: hourUtc,     // << use the current hour
			    center: center,
			    back:   3,
			    ahead:  3);

            if (errors.Count > 0)
                _log.LogWarning("PerDate refresh had {Count} errors: {Errors}", errors.Count, string.Join("; ", errors.Select(kv => $"{kv.Key}:{kv.Value}")));

            // Enforce retention after refresh
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

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}

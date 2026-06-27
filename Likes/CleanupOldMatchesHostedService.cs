using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DataSvc.Likes;

public sealed class CleanupOldMatchesHostedService : BackgroundService
{
    private readonly ILogger<CleanupOldMatchesHostedService> _log;
    private readonly string _connString;

    public CleanupOldMatchesHostedService(IConfiguration cfg, ILogger<CleanupOldMatchesHostedService> log)
    {
        _log = log;
        _connString = cfg.GetConnectionString("Default") 
                      ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run shortly after startup, then hourly
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cleanup failed");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task CleanupOnce(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var startWindowUtc = nowUtc.Date.AddDays(-3);                // day start (00:00) UTC
        var endWindowUtc   = nowUtc.Date.AddDays(+4).AddTicks(-1);   // end of day +3 (23:59:59.9999999)

        // for records without match_utc yet, allow up to 14 days age before culling to be safe
        var orphanCreatedCutoff = nowUtc.AddDays(-14);

        const string sql = @"
            DELETE m, t, v
              FROM matches m
              LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
              LEFT JOIN user_match_votes v  ON v.match_id = m.match_id
             WHERE
               (
                 m.match_utc IS NOT NULL AND (m.match_utc < @minUtc OR m.match_utc > @maxUtc)
               )
               OR
               (
                 m.match_utc IS NULL AND m.created_at < @createdCutoff
               );";

        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new
        {
            minUtc = startWindowUtc,
            maxUtc = endWindowUtc,
            createdCutoff = orphanCreatedCutoff
        });

        if (affected > 0)
            _log.LogInformation("Pruned {Count} rows across matches/totals/votes outside {Start}..{End}", affected, startWindowUtc, endWindowUtc);
    }
}

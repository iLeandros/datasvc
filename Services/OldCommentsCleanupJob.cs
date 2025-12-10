using System;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace DataSvc.Services
{
    /// <summary>
    /// Runs hourly and deletes comments older than current-day minus 3 days
    /// (cutoff = local midnight of (Today - 3), converted to UTC for DB comparison).
    /// </summary>
    public sealed class OldCommentsCleanupJob : BackgroundService
    {
        private readonly ILogger<OldCommentsCleanupJob> _log;
        private readonly IConfiguration _cfg;

        public OldCommentsCleanupJob(ILogger<OldCommentsCleanupJob> log, IConfiguration cfg)
        {
            _log = log;
            _cfg = cfg;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run once at startup, then hourly
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            try
            {
                var cs = _cfg.GetConnectionString("Default");
                if (string.IsNullOrWhiteSpace(cs))
                {
                    _log.LogWarning("OldCommentsCleanupJob: missing ConnectionStrings:Default; skipping.");
                    return;
                }

                // Determine server's working timezone (same pattern as elsewhere)
                var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
                var tz = !string.IsNullOrWhiteSpace(tzId)
                    ? TimeZoneInfo.FindSystemTimeZoneById(tzId)
                    : TimeZoneInfo.Local;

                var nowUtc = DateTimeOffset.UtcNow;
                var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
                var cutoffLocalDate = nowLocal.Date.AddDays(-3);               // local date (00:00)
                var cutoffLocal = new DateTime(cutoffLocalDate.Year, cutoffLocalDate.Month, cutoffLocalDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
                var cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(cutoffLocal, tz); // convert boundary to UTC

                int likesDeleted = 0, commentsDeleted = 0;

                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync(ct);

                using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    // 1) Delete likes for doomed comments
                    const string delLikes = @"
                      DELETE FROM comment_likes
                      WHERE comment_id IN (SELECT c.comment_id FROM comments c WHERE c.created_at < @cutoffUtc);";
                    likesDeleted = await conn.ExecuteAsync(new CommandDefinition(
                        delLikes, new { cutoffUtc }, transaction: (IDbTransaction)tx, cancellationToken: ct));

                    // 2) Delete the comments themselves
                    const string delComments = @"DELETE FROM comments WHERE created_at < @cutoffUtc;";
                    commentsDeleted = await conn.ExecuteAsync(new CommandDefinition(
                        delComments, new { cutoffUtc }, transaction: (IDbTransaction)tx, cancellationToken: ct));

                    await tx.CommitAsync(ct);
                }

                _log.LogInformation("OldCommentsCleanupJob: cutoffLocal={CutoffLocal} [{Tz}], cutoffUtc={CutoffUtc:o}. Deleted {Comments} comments and {Likes} likes.",
                    cutoffLocal.ToString("yyyy-MM-dd HH:mm"), tz.Id, cutoffUtc, commentsDeleted, likesDeleted);
            }
            catch (OperationCanceledException) { /* normal on shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "OldCommentsCleanupJob failed.");
            }
        }
    }
}

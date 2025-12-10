using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DataSvc.Services
{
    /// <summary>
    /// Hourly cleanup of expired/old operational data.
    /// - Sessions: expired/revoked
    /// - Password resets: expired or used (with grace)
    /// - Debug tokens: expired / too old
    /// - Provider events: old logs
    /// - Matches with no activity (no comments, no votes): older than retention
    /// - Match vote totals with zeroes: old rows
    /// </summary>
    public sealed class OldDataCleanupJob : BackgroundService
    {
        private readonly ILogger<OldDataCleanupJob> _log;
        private readonly IConfiguration _cfg;

        // Retention knobs (tune freely)
        private static readonly TimeSpan SessionsGrace      = TimeSpan.FromDays(7);
        private static readonly TimeSpan ResetsGrace        = TimeSpan.FromDays(30);
        private static readonly TimeSpan DebugTokensGrace   = TimeSpan.FromDays(30);
        private static readonly TimeSpan ProviderEventsKeep = TimeSpan.FromDays(60);

        // For matches without activity. We’ll align day-boundary to local tz and then convert to UTC.
        private const int InactiveMatchesKeepDays = 7;

        public OldDataCleanupJob(ILogger<OldDataCleanupJob> log, IConfiguration cfg)
        {
            _log = log; _cfg = cfg;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            try
            {
                var cs = _cfg.GetConnectionString("Default");
                if (string.IsNullOrWhiteSpace(cs))
                {
                    _log.LogWarning("OldDataCleanupJob: missing ConnectionStrings:Default; skipping.");
                    return;
                }

                // Build time cutoffs
                var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
                var tz   = !string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;

                var nowUtc      = DateTimeOffset.UtcNow;
                var nowLocal    = TimeZoneInfo.ConvertTime(nowUtc, tz);
                var dayBoundary = nowLocal.Date; // today@00:00 local
                var inactiveMatchesCutoffLocal = dayBoundary.AddDays(-InactiveMatchesKeepDays);
                var inactiveMatchesCutoffUtc   = TimeZoneInfo.ConvertTimeToUtc(inactiveMatchesCutoffLocal, tz);

                var sessionsCutoff   = nowUtc.UtcDateTime - SessionsGrace;
                var resetsCutoff     = nowUtc.UtcDateTime - ResetsGrace;
                var tokensCutoff     = nowUtc.UtcDateTime - DebugTokensGrace;
                var eventsCutoff     = nowUtc.UtcDateTime - ProviderEventsKeep;
                var zeroTotalsCutoff = inactiveMatchesCutoffUtc; // reuse

                int delSessions = 0, delResets = 0, delTokens = 0, delEvents = 0, delZeroTotals = 0, delMatches = 0;

                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync(ct);

                using var tx = await conn.BeginTransactionAsync(ct);

                // 1) Sessions: expired or revoked long ago
                //    sessions(expires_at, revoked_at) – indexed. :contentReference[oaicite:7]{index=7}
                delSessions = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE FROM sessions
 WHERE (expires_at < @sessionsCutoff)
    OR (revoked_at IS NOT NULL AND revoked_at < @sessionsCutoff);
", new { sessionsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // 2) Password reset tokens: expired or used (with grace)  :contentReference[oaicite:8]{index=8}
                delResets = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE FROM password_resets
 WHERE (expires_at < @resetsCutoff)
    OR (used_at IS NOT NULL AND used_at < @resetsCutoff);
", new { resetsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // 3) Debug tokens: expired or very old  :contentReference[oaicite:9]{index=9}
                delTokens = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE FROM debug_tokens
 WHERE (expires_at IS NOT NULL AND expires_at < @tokensCutoff)
    OR (expires_at IS NULL     AND created_at < @tokensCutoff);
", new { tokensCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // 4) Provider events: log-like; trim old rows  :contentReference[oaicite:10]{index=10}
                delEvents = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE FROM provider_events
 WHERE occurred_at < @eventsCutoff;
", new { eventsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // 5) Match vote totals with all zeros and old  :contentReference[oaicite:11]{index=11}
                delZeroTotals = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE FROM match_vote_totals
 WHERE upvotes = 0 AND downvotes = 0
   AND updated_at < @zeroTotalsCutoff;
", new { zeroTotalsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // 6) Inactive matches (no comments, no user votes) older than cutoff
                //    This will also cascade-delete comments/likes/flags/totals due to FKs. 
                delMatches = await conn.ExecuteAsync(new CommandDefinition(@"
DELETE m FROM matches m
LEFT JOIN comments c ON c.match_id = m.match_id
LEFT JOIN user_match_votes v ON v.match_id = m.match_id
WHERE m.created_at < @inactiveMatchesCutoffUtc
  AND c.comment_id IS NULL
  AND v.match_id IS NULL;
", new { inactiveMatchesCutoffUtc }, (IDbTransaction)tx, cancellationToken: ct));

                await tx.CommitAsync(ct);

                _log.LogInformation(
                    "Cleanup done. sessions={Sessions}, resets={Resets}, debugTokens={Tokens}, providerEvents={Events}, zeroTotals={ZeroTotals}, inactiveMatches={Matches}. Cutoff(local D-7)={CutoffLocal}, cutoffUtc={CutoffUtc:o}",
                    delSessions, delResets, delTokens, delEvents, delZeroTotals, delMatches,
                    inactiveMatchesCutoffLocal.ToString("yyyy-MM-dd HH:mm"), inactiveMatchesCutoffUtc);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "OldDataCleanupJob failed.");
            }
        }
    }
}

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
    public sealed class OldDataCleanupJob : BackgroundService
    {
        private readonly ILogger<OldDataCleanupJob> _log;
        private readonly IConfiguration _cfg;

        // Retention knobs
        private static readonly TimeSpan SessionsGrace      = TimeSpan.FromDays(7);
        private static readonly TimeSpan ResetsGrace        = TimeSpan.FromDays(30);
        private static readonly TimeSpan DebugTokensGrace   = TimeSpan.FromDays(30);
        private static readonly TimeSpan ProviderEventsKeep = TimeSpan.FromDays(60);

        // Comments older than current day - 3 (local midnight boundary)
        private const int CommentsKeepDays       = 4;

        // Matches with no activity older than D-7 (adjust if you like)
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

                // Timezone alignment (use TOP_OF_HOUR_TZ if set; else local)
                var tzId = Environment.GetEnvironmentVariable("TOP_OF_HOUR_TZ");
                var tz   = !string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;

                var nowUtc      = DateTimeOffset.UtcNow;
                var nowLocal    = TimeZoneInfo.ConvertTime(nowUtc, tz);
                var todayLocal  = nowLocal.Date;

                // 1) Comments: cutoff = local midnight of (today - 3), then convert to UTC
                var commentsCutoffLocal = todayLocal.AddDays(-CommentsKeepDays);
                var commentsCutoffUtc   = TimeZoneInfo.ConvertTimeToUtc(commentsCutoffLocal, tz);

                // 2) Inactive matches: local midnight of (today - N)
                var inactiveMatchesCutoffLocal = todayLocal.AddDays(-InactiveMatchesKeepDays);
                var inactiveMatchesCutoffUtc   = TimeZoneInfo.ConvertTimeToUtc(inactiveMatchesCutoffLocal, tz);

                // Other absolute time cutoffs (UTC)
                var sessionsCutoff   = nowUtc.UtcDateTime - SessionsGrace;
                var resetsCutoff     = nowUtc.UtcDateTime - ResetsGrace;
                var tokensCutoff     = nowUtc.UtcDateTime - DebugTokensGrace;
                var eventsCutoff     = nowUtc.UtcDateTime - ProviderEventsKeep;
                var zeroTotalsCutoff = inactiveMatchesCutoffUtc;

                int delCommentLikes = 0, delComments = 0,
                    delSessions = 0, delResets = 0, delTokens = 0, delEvents = 0, delZeroTotals = 0, delMatches = 0;

                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync(ct);

                using var tx = await conn.BeginTransactionAsync(ct);

                // --- A) COMMENTS + LIKES older than D-3 (UTC boundary from local midnight) ---

                // Delete likes for doomed comments (if you don't have ON DELETE CASCADE)
                const string delLikesSql = @"
DELETE FROM comment_likes
WHERE comment_id IN (
    SELECT comment_id FROM comments WHERE created_at < @cutoff
);";
                delCommentLikes = await conn.ExecuteAsync(
                    new CommandDefinition(delLikesSql, new { cutoff = commentsCutoffUtc }, (IDbTransaction)tx, cancellationToken: ct));

                // Delete old comments themselves
                const string delCommentsSql = @"DELETE FROM comments WHERE created_at < @cutoff;";
                delComments = await conn.ExecuteAsync(
                    new CommandDefinition(delCommentsSql, new { cutoff = commentsCutoffUtc }, (IDbTransaction)tx, cancellationToken: ct));

                // --- B) SESSIONS / TOKENS / EVENTS ---

                const string delSessionsSql = @"
DELETE FROM sessions
 WHERE (expires_at < @sessionsCutoff)
    OR (revoked_at IS NOT NULL AND revoked_at < @sessionsCutoff);";
                delSessions = await conn.ExecuteAsync(
                    new CommandDefinition(delSessionsSql, new { sessionsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                const string delResetsSql = @"
DELETE FROM password_resets
 WHERE (expires_at < @resetsCutoff)
    OR (used_at IS NOT NULL AND used_at < @resetsCutoff);";
                delResets = await conn.ExecuteAsync(
                    new CommandDefinition(delResetsSql, new { resetsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                const string delDebugTokensSql = @"
DELETE FROM debug_tokens
 WHERE (expires_at IS NOT NULL AND expires_at < @tokensCutoff)
    OR (expires_at IS NULL     AND created_at < @tokensCutoff);";
                delTokens = await conn.ExecuteAsync(
                    new CommandDefinition(delDebugTokensSql, new { tokensCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                const string delProviderEventsSql = @"
DELETE FROM provider_events
 WHERE occurred_at < @eventsCutoff;";
                delEvents = await conn.ExecuteAsync(
                    new CommandDefinition(delProviderEventsSql, new { eventsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // --- C) MATCH TOTALS (optional tidy-up) ---
                const string delZeroTotalsSql = @"
DELETE FROM match_vote_totals
 WHERE upvotes = 0 AND downvotes = 0
   AND updated_at < @zeroTotalsCutoff;";
                delZeroTotals = await conn.ExecuteAsync(
                    new CommandDefinition(delZeroTotalsSql, new { zeroTotalsCutoff }, (IDbTransaction)tx, cancellationToken: ct));

                // --- D) INACTIVE MATCHES (no comments, no user votes) older than D-7 ---
                const string delMatchesSql = @"
DELETE m FROM matches m
LEFT JOIN comments c ON c.match_id = m.match_id
LEFT JOIN user_match_votes v ON v.match_id = m.match_id
WHERE m.created_at < @inactiveCutoff
  AND c.comment_id IS NULL
  AND v.match_id IS NULL;";
                delMatches = await conn.ExecuteAsync(
                    new CommandDefinition(delMatchesSql, new { inactiveCutoff = inactiveMatchesCutoffUtc }, (IDbTransaction)tx, cancellationToken: ct));

                await tx.CommitAsync(ct);

                _log.LogInformation(
                    "Cleanup OK. cutoffLocal(D-3)={CommentsCutoffLocal}, cutoffUtc={CommentsCutoffUtc:o} | " +
                    "comments={Comments} likes={Likes} | sessions={Sessions} resets={Resets} debugTokens={Tokens} events={Events} zeroTotals={ZeroTotals} inactiveMatches={Matches} (cutoff D-7 local={InactiveLocal}, utc={InactiveUtc:o})",
                    commentsCutoffLocal.ToString("yyyy-MM-dd HH:mm"), commentsCutoffUtc,
                    delComments, delCommentLikes,
                    delSessions, delResets, delTokens, delEvents, delZeroTotals, delMatches,
                    inactiveMatchesCutoffLocal.ToString("yyyy-MM-dd HH:mm"), inactiveMatchesCutoffUtc
                );
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "OldDataCleanupJob failed.");
            }
        }
    }
}

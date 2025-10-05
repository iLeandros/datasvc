using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Likes;

[ApiController]
[Route("v1/likes")]
public sealed class LikesController : ControllerBase
{
    private readonly string? _connString;
    private readonly ILogger<LikesController> _log;

    public LikesController(IConfiguration cfg, ILogger<LikesController> log)
    {
        _connString = cfg.GetConnectionString("Default");
        _log = log;
    }

    // ===== DTOs =====
    public sealed record VoteRequest(string Href, sbyte Vote, DateTime? MatchUtc); // Vote in {-1,0,+1}, optional UTC kick-off
    public sealed class LikeTotalsDto
    {
        public string Href { get; set; } = "";
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public int Score { get; set; }
        public sbyte? UserVote { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? MatchUtc { get; set; }
    }

    // ===== Helpers =====
    private ulong GetRequiredUserId()
    {
        var uid = User.FindFirstValue("uid");
        if (string.IsNullOrWhiteSpace(uid)) throw new UnauthorizedAccessException("Missing uid claim.");
        return ulong.Parse(uid);
    }

    private static byte[] Sha256(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s ?? string.Empty));

    private MySqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connString))
            throw new InvalidOperationException("Missing ConnectionStrings:Default");
        return new MySqlConnection(_connString);
    }

    private static DateTime? ForceUtc(DateTime? dt)
    {
        if (dt is null) return null;
        if (dt.Value.Kind == DateTimeKind.Utc) return dt;
        if (dt.Value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
        return dt.Value.ToUniversalTime();
    }

    // =========================================
    // POST /v1/likes  { href, vote: -1|0|+1, matchUtc?: ISO-UTC }
    // Idempotent; stores match_utc (if provided) so we can prune later.
    // =========================================
    
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Href))
            return BadRequest(new { error = "href is required" });
        if (req.Vote is < -1 or > 1)
            return BadRequest(new { error = "vote must be -1, 0, or +1" });

        var href = req.Href.Trim();
        var hrefHash = Sha256(href);
        var newVote = req.Vote;
        var userId = GetRequiredUserId();
        var matchUtc = ForceUtc(req.MatchUtc);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 1) Ensure match row exists (and store match_utc if provided)
            ulong matchId = await conn.ExecuteScalarAsync<ulong?>(@"
                SELECT match_id FROM matches WHERE href_hash=@hrefHash LIMIT 1;",
                new { hrefHash }, tx) ?? 0UL;

            if (matchId == 0UL)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO matches (href_hash, href, match_utc)
                    VALUES (@hrefHash, @href, @matchUtc)
                    ON DUPLICATE KEY UPDATE href=VALUES(href), match_utc=COALESCE(VALUES(match_utc), match_utc);",
                    new { hrefHash, href, matchUtc }, tx);

                matchId = await conn.ExecuteScalarAsync<ulong>(@"
                    SELECT match_id FROM matches WHERE href_hash=@hrefHash LIMIT 1;",
                    new { hrefHash }, tx);

                await conn.ExecuteAsync("INSERT IGNORE INTO match_vote_totals (match_id) VALUES (@mid);",
                    new { mid = matchId }, tx);
            }
            else if (matchUtc is not null)
            {
                // backfill or correct match_utc if client now knows it
                await conn.ExecuteAsync(@"
                    UPDATE matches
                       SET match_utc = @matchUtc
                     WHERE match_id  = @mid
                       AND (match_utc IS NULL OR match_utc <> @matchUtc);",
                    new { matchUtc, mid = matchId }, tx);
            }

            // 2) Previous vote for delta calc (treat null as 0)
            sbyte prev = await conn.ExecuteScalarAsync<sbyte?>(@"
                SELECT vote FROM user_match_votes WHERE user_id=@uid AND match_id=@mid;",
                new { uid = userId, mid = matchId }, tx) ?? (sbyte)0;

            if (prev == newVote)
            {
                var unchanged = await conn.QueryFirstAsync<(int Up, int Down, int Score, DateTime Updated, DateTime? MUtc)>(@"
                    SELECT t.upvotes, t.downvotes, t.score, t.updated_at, m.match_utc
                      FROM match_vote_totals t
                      JOIN matches m ON m.match_id=t.match_id
                     WHERE t.match_id=@mid;",
                    new { mid = matchId }, tx);

                await tx.CommitAsync(ct);

                return Ok(new LikeTotalsDto
                {
                    Href = href,
                    Upvotes = unchanged.Up,
                    Downvotes = unchanged.Down,
                    Score = unchanged.Score,
                    UserVote = prev,
                    UpdatedAtUtc = DateTime.SpecifyKind(unchanged.Updated, DateTimeKind.Utc),
                    MatchUtc = unchanged.MUtc
                });
            }

            // 3) Upsert user vote
            await conn.ExecuteAsync(@"
                INSERT INTO user_match_votes (user_id, match_id, vote)
                VALUES (@uid, @mid, @vote)
                ON DUPLICATE KEY UPDATE vote = VALUES(vote);",
                new { uid = userId, mid = matchId, vote = newVote }, tx);

            // 4) Apply deltas
            int upDelta = 0, downDelta = 0, scoreDelta = newVote - prev;
            if (prev == 1) upDelta--;
            if (prev == -1) downDelta--;
            if (newVote == 1) upDelta++;
            if (newVote == -1) downDelta++;

            await conn.ExecuteAsync(@"
                UPDATE match_vote_totals
                   SET upvotes   = upvotes   + @u,
                       downvotes = downvotes + @d,
                       score     = score     + @s
                 WHERE match_id  = @mid;",
                new { u = upDelta, d = downDelta, s = scoreDelta, mid = matchId }, tx);

            var row = await conn.QueryFirstAsync<(int Up, int Down, int Score, DateTime Updated, DateTime? MUtc)>(@"
                SELECT t.upvotes, t.downvotes, t.score, t.updated_at, m.match_utc
                  FROM match_vote_totals t
                  JOIN matches m ON m.match_id=t.match_id
                 WHERE t.match_id=@mid;",
                new { mid = matchId }, tx);

            await tx.CommitAsync(ct);

            return Ok(new LikeTotalsDto
            {
                Href = href,
                Upvotes = row.Up,
                Downvotes = row.Down,
                Score = row.Score,
                UserVote = newVote,
                UpdatedAtUtc = DateTime.SpecifyKind(row.Updated, DateTimeKind.Utc),
                MatchUtc = row.MUtc
            });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            _log.LogError(ex, "Vote failed for href {Href}", href);
            return Problem("Vote failed.");
        }
    }
    
    // =========================================
    // GET /v1/likes?href=...
    // Returns totals and matchUtc (if known).
    // =========================================
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetTotals([FromQuery] string href, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(href))
            return BadRequest(new { error = "href is required" });

        href = href.Trim();
        var hrefHash = Sha256(href);

        ulong? userId = null;
        var uidStr = User.FindFirstValue("uid");
        if (ulong.TryParse(uidStr, out var uidParsed)) userId = uidParsed;

        await using var conn = Open();
        await conn.OpenAsync(ct);

        var rec = await conn.QueryFirstOrDefaultAsync<(ulong Mid, int Up, int Down, int Score, DateTime Updated, DateTime? MUtc)>(@"
            SELECT m.match_id, COALESCE(t.upvotes,0), COALESCE(t.downvotes,0), COALESCE(t.score,0),
                   COALESCE(t.updated_at, UTC_TIMESTAMP()), m.match_utc
              FROM matches m
              LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
             WHERE m.href_hash = @hrefHash
             LIMIT 1;", new { hrefHash });

        if (rec.Mid == 0UL)
        {
            return Ok(new LikeTotalsDto
            {
                Href = href,
                Upvotes = 0,
                Downvotes = 0,
                Score = 0,
                UserVote = null,
                UpdatedAtUtc = DateTime.UtcNow,
                MatchUtc = null
            });
        }

        sbyte? userVote = null;
        if (userId is not null)
        {
            userVote = await conn.ExecuteScalarAsync<sbyte?>(@"
                SELECT vote FROM user_match_votes WHERE user_id=@uid AND match_id=@mid;",
                new { uid = userId.Value, mid = rec.Mid });
        }

        return Ok(new LikeTotalsDto
        {
            Href = href,
            Upvotes = rec.Up,
            Downvotes = rec.Down,
            Score = rec.Score,
            UserVote = userVote,
            UpdatedAtUtc = DateTime.SpecifyKind(rec.Updated, DateTimeKind.Utc),
            MatchUtc = rec.MUtc
        });
    }
}

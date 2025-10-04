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
    public sealed record VoteRequest(string Href, sbyte Vote); // -1, 0, +1
    public sealed class LikeTotalsDto
    {
        public string Href { get; set; } = "";
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public int Score { get; set; }          // Up - Down
        public sbyte? UserVote { get; set; }    // caller's current vote if authorized, else null
        public DateTime UpdatedAtUtc { get; set; }
    }

    // ===== Helpers =====
    private ulong GetRequiredUserId()
    {
        var uid = User.FindFirstValue("uid"); // set by SessionAuthHandler
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

    // =========================================
    // POST /v1/likes  { href, vote: -1|0|+1 }
    // Idempotent; applies the delta to running totals.
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

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 1) Ensure match row exists
            ulong matchId = await conn.ExecuteScalarAsync<ulong?>(@"
                SELECT match_id FROM matches WHERE href_hash = @hrefHash LIMIT 1;",
                new { hrefHash }, tx) ?? 0UL;

            if (matchId == 0UL)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO matches (href_hash, href)
                    VALUES (@hrefHash, @href)
                    ON DUPLICATE KEY UPDATE href = VALUES(href);",
                    new { hrefHash, href }, tx);

                matchId = await conn.ExecuteScalarAsync<ulong>(@"
                    SELECT match_id FROM matches WHERE href_hash = @hrefHash LIMIT 1;",
                    new { hrefHash }, tx);

                // Seed totals row
                await conn.ExecuteAsync(@"
                    INSERT IGNORE INTO match_vote_totals (match_id) VALUES (@mid);",
                    new { mid = matchId }, tx);
            }

            // 2) Previous vote for delta calc (treat null as 0)
            sbyte prev = await conn.ExecuteScalarAsync<sbyte?>(@"
                SELECT vote FROM user_match_votes WHERE user_id=@uid AND match_id=@mid;",
                new { uid = userId, mid = matchId }, tx) ?? (sbyte)0;

            if (prev == newVote)
            {
                // Nothing changed — return current totals quickly
                var unchanged = await conn.QueryFirstOrDefaultAsync<(int Up, int Down, int Score, DateTime Updated)>(@"
                    SELECT upvotes, downvotes, score, updated_at
                    FROM match_vote_totals WHERE match_id=@mid;",
                    new { mid = matchId }, tx);

                await tx.CommitAsync(ct);

                return Ok(new LikeTotalsDto
                {
                    Href = href,
                    Upvotes = unchanged.Up,
                    Downvotes = unchanged.Down,
                    Score = unchanged.Score,
                    UserVote = prev,
                    UpdatedAtUtc = DateTime.SpecifyKind(unchanged.Updated, DateTimeKind.Utc)
                });
            }

            // 3) Upsert user vote
            await conn.ExecuteAsync(@"
                INSERT INTO user_match_votes (user_id, match_id, vote)
                VALUES (@uid, @mid, @vote)
                ON DUPLICATE KEY UPDATE vote = VALUES(vote);",
                new { uid = userId, mid = matchId, vote = newVote }, tx);

            // 4) Compute and apply deltas to totals
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

            // 5) Read back totals to return
            var row = await conn.QueryFirstAsync<(int Up, int Down, int Score, DateTime Updated)>(@"
                SELECT upvotes, downvotes, score, updated_at
                FROM match_vote_totals WHERE match_id=@mid;",
                new { mid = matchId }, tx);

            await tx.CommitAsync(ct);

            return Ok(new LikeTotalsDto
            {
                Href = href,
                Upvotes = row.Up,
                Downvotes = row.Down,
                Score = row.Score,
                UserVote = newVote,
                UpdatedAtUtc = DateTime.SpecifyKind(row.Updated, DateTimeKind.Utc)
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
    // Returns totals (and the caller's vote if authenticated).
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

        // If the caller is logged in, include their current vote in the response
        var uidStr = User.FindFirstValue("uid");
        if (ulong.TryParse(uidStr, out var uidParsed)) userId = uidParsed;

        await using var conn = Open();
        await conn.OpenAsync(ct);

        // match_id from href
        var matchId = await conn.ExecuteScalarAsync<ulong?>(@"
            SELECT match_id FROM matches WHERE href_hash=@hrefHash LIMIT 1;",
            new { hrefHash });

        if (matchId is null)
        {
            // No votes yet; return zeros (and null user vote)
            return Ok(new LikeTotalsDto
            {
                Href = href,
                Upvotes = 0,
                Downvotes = 0,
                Score = 0,
                UserVote = null,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        var totals = await conn.QueryFirstOrDefaultAsync<(int Up, int Down, int Score, DateTime Updated)>(@"
            SELECT upvotes, downvotes, score, updated_at
            FROM match_vote_totals WHERE match_id=@mid;", new { mid = matchId });

        sbyte? userVote = null;
        if (userId is not null)
        {
            userVote = await conn.ExecuteScalarAsync<sbyte?>(@"
                SELECT vote FROM user_match_votes WHERE user_id=@uid AND match_id=@mid;",
                new { uid = userId.Value, mid = matchId.Value });
        }

        return Ok(new LikeTotalsDto
        {
            Href = href,
            Upvotes = totals.Up,
            Downvotes = totals.Down,
            Score = totals.Score,
            UserVote = userVote,
            UpdatedAtUtc = DateTime.SpecifyKind(totals.Updated, DateTimeKind.Utc)
        });
    }
}

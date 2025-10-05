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
    public sealed class MyLikeDto
    {
        public string Href { get; set; } = "";
        public DateTime? MatchUtc { get; set; }
        public DateTime UserUpdatedAtUtc { get; set; }
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public int Score { get; set; }
    }


    // ===== Helpers =====
    /*
    private ulong GetRequiredUserId()
    {
        var uid = User.FindFirstValue("uid");
        if (string.IsNullOrWhiteSpace(uid)) throw new UnauthorizedAccessException("Missing uid claim.");
        return ulong.Parse(uid);
    }
    */
    [NonAction]
    private async Task<(IActionResult? error, ulong userId)> GetRequiredUserIdAsync(CancellationToken ct)
    {
        // 1) HttpContext.Items (preferred when auth middleware sets it)
        if (HttpContext.Items.TryGetValue("user_id", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out var uidFromItem))
        {
            return (null, uidFromItem);
        }
    
        // 2) Common claims
        string?[] candidates =
        {
            User.FindFirstValue("uid"),
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue("sub"),
        };
        foreach (var s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out var uidFromClaim))
                return (null, uidFromClaim);
        }
    
        // 3) Fallback: Bearer token → debug_tokens lookup
        if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.ToString().Substring("Bearer ".Length).Trim();
            if (!string.IsNullOrEmpty(token))
            {
                const string sql = "SELECT user_id FROM debug_tokens WHERE token = @token LIMIT 1;";
                await using var conn = Open();
                var uid = await conn.ExecuteScalarAsync<ulong?>(new CommandDefinition(sql, new { token }, cancellationToken: ct));
                if (uid.HasValue)
                    return (null, uid.Value);
            }
        }
    
        _log.LogWarning("Missing user id: no Items, no claim, and token not found in debug_tokens.");
        return (Unauthorized(new { error = "Missing or invalid user identity" }), 0UL);
    }

    [NonAction]
    private IActionResult? GetRequiredUserId(out ulong userId)
    {
        userId = 0;
    
        // 1) Prefer the pipeline’s Items slot (how your auth often passes it)
        if (HttpContext.Items.TryGetValue("user_id", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out userId))
        {
            return null; // success
        }
    
        // 2) Fall back to common claim types
        string?[] candidates =
        {
            User.FindFirstValue("uid"),
            User.FindFirstValue(ClaimTypes.NameIdentifier), // "nameidentifier"
            User.FindFirstValue("sub")
        };
    
        foreach (var s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out userId))
                return null; // success
        }
    
        // 3) Still nothing → 401
        _log.LogWarning("Missing user id (no HttpContext.Items[\"user_id\"] and no uid/sub claim).");
        return Unauthorized(new { error = "Missing or invalid user ID" });
    }

    [NonAction]
    private static (string primary, string? alt) CanonicalHrefCandidates(string href)
    {
        // as-is
        var primary = href.Trim();
    
        // if the string we received contains spaces (likely decoded '+'),
        // also try a variant where spaces are turned back into '+'
        string? alt = null;
        if (primary.IndexOf(' ') >= 0)
            alt = primary.Replace(' ', '+');
    
        return (primary, alt);
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
    /*
    [HttpPost("")]
    //[Authorize]
    public async Task<IActionResult> Vote(CancellationToken ct)
    {
        return Ok(new { message = "POST received" });
    }
    
    [HttpPost("")]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Href))
            return BadRequest(new { error = "href is required" });
        if (req.Vote is < -1 or > 1)
            return BadRequest(new { error = "vote must be -1, 0, or +1" });
    
        var authResult = GetRequiredUserId(out var userId);
        if (authResult != null)
            return authResult;
    
        return Ok(new { message = "POST received", href = req.Href, vote = req.Vote, matchUtc = req.MatchUtc, userId });
    }
    */
    // =========================================
    // POST /v1/likes  { href, vote: -1|0|+1, matchUtc?: ISO-UTC }
    // Idempotent; stores match_utc (if provided) so we can prune later.
    // =========================================
    
    [HttpPost("")]
    [Authorize]
    [Consumes("application/json")]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Href))
            return BadRequest(new { error = "href is required" });
        if (req.Vote is < -1 or > 1)
            return BadRequest(new { error = "vote must be -1, 0, or +1" });

        var href = req.Href.Trim();
        var hrefHash = Sha256(href);
        var newVote = req.Vote;
        //var userId = GetRequiredUserId();
        var authResult = GetRequiredUserId(out var userId);
        if (authResult != null)
            return authResult;
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
    // PUBLIC: GET /v1/likes  (anonymous OK; includes UserVote only if user id can be resolved)
    [HttpGet("")]
    [AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] string href, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(href))
            return BadRequest(new { error = "href is required" });
        if (href.Length > 600)
            return BadRequest(new { error = "href too long (max 600 chars)" });
    
        // tolerate '+' ↔ ' ' querystring decoding
        var (h1, h2) = CanonicalHrefCandidates(href);
        var h1Hash = Sha256(h1);
        var h2Hash = h2 is null ? null : Sha256(h2);
    
        const string sql = @"
    SELECT
      m.match_id,
      m.href,
      m.match_utc,
      COALESCE(t.upvotes, 0)   AS Up,
      COALESCE(t.downvotes, 0) AS Down,
      COALESCE(t.score, 0)     AS Score,
      COALESCE(t.updated_at, m.created_at) AS Updated
    FROM matches m
    LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
    WHERE m.href_hash = @h1
       OR (@h2 IS NOT NULL AND m.href_hash = @h2)
    LIMIT 1;";
    
        await using var conn = Open();
        var rec = await conn.QuerySingleOrDefaultAsync(sql, new { h1 = h1Hash, h2 = h2Hash });
    
        if (rec is null)
        {
            return Ok(new LikeTotalsDto
            {
                Href = h1,
                Upvotes = 0,
                Downvotes = 0,
                Score = 0,
                UserVote = null,
                UpdatedAtUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
                MatchUtc = null
            });
        }
    
        // Try to include the caller's vote if we can resolve a user id (no hard auth requirement here)
        sbyte? userVote = null;
        var (err, userId) = await GetRequiredUserIdAsync(ct);
        if (err is null && userId != 0UL)
        {
            const string sqlUserVote = @"SELECT vote FROM user_match_votes WHERE user_id=@user_id AND match_id=@match_id;";
            userVote = await conn.ExecuteScalarAsync<sbyte?>(
                sqlUserVote, new { user_id = userId, match_id = (ulong)rec.match_id });
        }
    
        return Ok(new LikeTotalsDto
        {
            Href = rec.href,
            Upvotes = rec.Up,
            Downvotes = rec.Down,
            Score = rec.Score,
            UserVote = userVote,
            UpdatedAtUtc = DateTime.SpecifyKind(rec.Updated, DateTimeKind.Utc),
            MatchUtc = rec.match_utc is null ? null : DateTime.SpecifyKind(rec.match_utc, DateTimeKind.Utc)
        });
    }
    // AUTHORIZED: GET /v1/likes/my  (requires user id)
    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMyLikes([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (take <= 0 || take > 200) take = 50;
        if (skip < 0) skip = 0;
    
        var (err, userId) = await GetRequiredUserIdAsync(ct);
        if (err is not null) return err;
    
        const string sql = @"
    SELECT
      m.href,
      m.match_utc,
      umv.updated_at              AS user_updated_at,
      COALESCE(t.upvotes, 0)      AS upvotes,
      COALESCE(t.downvotes, 0)    AS downvotes,
      COALESCE(t.score, 0)        AS score
    FROM user_match_votes umv
    JOIN matches m            ON m.match_id = umv.match_id
    LEFT JOIN match_vote_totals t ON t.match_id = umv.match_id
    WHERE umv.user_id = @user_id AND umv.vote = 1
    ORDER BY COALESCE(m.match_utc, umv.updated_at) DESC
    LIMIT @skip, @take;";
    
        await using var conn = Open();
        var rows = (await conn.QueryAsync(sql, new { user_id = userId, skip, take })).ToList();
    
        var items = rows.Select(r => new MyLikeDto
        {
            Href = r.href,
            MatchUtc = r.match_utc is null ? null : DateTime.SpecifyKind((DateTime)r.match_utc, DateTimeKind.Utc),
            UserUpdatedAtUtc = DateTime.SpecifyKind((DateTime)r.user_updated_at, DateTimeKind.Utc),
            Upvotes = (int)r.upvotes,
            Downvotes = (int)r.downvotes,
            Score = (int)r.score
        });
    
        return Ok(new { skip, take, count = rows.Count, items });
    }

}

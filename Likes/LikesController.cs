
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
[Produces("application/json")]
public sealed class LikesController : ControllerBase
{
    private readonly string? _connString;
    private readonly ILogger<LikesController> _log;

    public LikesController(IConfiguration cfg, ILogger<LikesController> log)
    {
        _connString = cfg.GetConnectionString("Default");
        _log = log;
    }

    // ---------- DTOs ----------
    public sealed class VoteRequest
    {
        public string? Href { get; set; }
        public int Vote { get; set; } // -1, 0, +1
        public DateTime? MatchUtc { get; set; }
    }

    public sealed class LikeTotalsDto
    {
        public string Href { get; set; } = "";
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public int Score { get; set; }
        public int? UserVote { get; set; }
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

    // ---------- Helpers ----------

    [NonAction]
    private static byte[] Sha256(string? s) => SHA256.HashData(Encoding.UTF8.GetBytes(s ?? string.Empty));

    [NonAction]
    private MySqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connString))
            throw new InvalidOperationException("Missing ConnectionStrings:Default");
        return new MySqlConnection(_connString);
    }

    [NonAction]
    private static DateTime? ForceUtc(DateTime? dt)
    {
        if (dt is null) return null;
        if (dt.Value.Kind == DateTimeKind.Utc) return dt;
        if (dt.Value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
        return dt.Value.ToUniversalTime();
    }

    /// <summary>
    /// Try to read the authenticated user's id from HttpContext.Items["user_id"] or common claim types.
    /// Returns null on success and sets userId, or an IActionResult (401/400) on failure.
    /// </summary>
    [NonAction]
    private IActionResult? GetRequiredUserId(out ulong userId)
    {
        userId = 0;

        // 1) Pipeline often stashes the id here
        if (HttpContext.Items.TryGetValue("user_id", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out userId))
        {
            return null; // success
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
            if (!string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out userId))
                return null; // success
        }

        _log.LogWarning("Missing user id (no HttpContext.Items[\"user_id\"] and no uid/sub claim).");
        return Unauthorized(new { error = "Missing or invalid user ID" });
    }

    // ---------- Endpoints ----------

    /// <summary>
    /// Idempotent vote endpoint.
    /// </summary>
    [HttpPost("")]
    [Authorize]
    [Consumes("application/json")]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Href))
            return BadRequest(new { error = "href is required" });
        if (req.Vote is < -1 or > 1)
            return BadRequest(new { error = "vote must be -1, 0, or +1" });
        if (req.Href!.Length > 600)
            return BadRequest(new { error = "href too long (max 600 chars)" });

        var auth = GetRequiredUserId(out var userId);
        if (auth is not null) return auth;

        var href = req.Href!.Trim();
        var hrefHash = Sha256(href);
        var matchUtc = ForceUtc(req.MatchUtc);

        const string sqlEnsureMatch = @"
INSERT INTO matches (match_id, href_hash, href, match_utc, created_at)
VALUES (/* match_id */ 0, @href_hash, @href, @match_utc, NOW(3))
ON DUPLICATE KEY UPDATE match_id = match_id;
SELECT match_id FROM matches WHERE href_hash = @href_hash;";

        const string sqlSelectPrev = @"
SELECT vote FROM user_match_votes WHERE user_id=@user_id AND match_id=@match_id;";

        const string sqlUpsertVote = @"
INSERT INTO user_match_votes (user_id, match_id, vote, updated_at)
VALUES (@user_id, @match_id, @vote, NOW(3))
ON DUPLICATE KEY UPDATE vote=VALUES(vote), updated_at=NOW(3);";

        const string sqlEnsureTotals = @"
INSERT IGNORE INTO match_vote_totals (match_id, upvotes, downvotes, score, updated_at)
VALUES (@match_id, 0, 0, 0, NOW(3));";

        const string sqlUpdateTotals = @"
UPDATE match_vote_totals
SET upvotes  = upvotes  + @up_delta,
    downvotes= downvotes+ @down_delta,
    score    = score    + @score_delta,
    updated_at = NOW(3)
WHERE match_id=@match_id;";

        const string sqlReadTotals = @"
SELECT m.href, m.match_utc, t.upvotes, t.downvotes, t.score, t.updated_at
FROM matches m
LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
WHERE m.match_id=@match_id;";

        using var conn = Open();
        await conn.OpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        // 1) Ensure match row exists (by hash/unique)
        var matchId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            sqlEnsureMatch, new { href_hash = hrefHash, href, match_utc = matchUtc }, tx, cancellationToken: ct));

        // 2) Diff against previous vote
        var prevVote = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            sqlSelectPrev, new { user_id = userId, match_id = matchId }, tx, cancellationToken: ct));

        // If prev == req.Vote, it's idempotent; still update timestamp but deltas are zero
        var newVote = req.Vote;
        int upDelta = 0, downDelta = 0, scoreDelta = 0;

        int old = prevVote ?? 0;
        int _new = newVote;

        // Compute deltas going from old -> new
        if (old != _new)
        {
            // Remove old
            if (old == 1) { upDelta -= 1; scoreDelta -= 1; }
            else if (old == -1) { downDelta -= 1; scoreDelta += 1; }

            // Add new
            if (_new == 1) { upDelta += 1; scoreDelta += 1; }
            else if (_new == -1) { downDelta += 1; scoreDelta -= 1; }
        }

        // 3) Upsert user's vote
        await conn.ExecuteAsync(new CommandDefinition(
            sqlUpsertVote, new { user_id = userId, match_id = matchId, vote = newVote }, tx, cancellationToken: ct));

        // 4) Ensure totals exist and apply deltas
        await conn.ExecuteAsync(new CommandDefinition(sqlEnsureTotals, new { match_id = matchId }, tx, cancellationToken: ct));
        if (upDelta != 0 || downDelta != 0 || scoreDelta != 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                sqlUpdateTotals, new { match_id = matchId, up_delta = upDelta, down_delta = downDelta, score_delta = scoreDelta }, tx, cancellationToken: ct));
        }

        // 5) Read back
        var row = await conn.QuerySingleAsync(new CommandDefinition(
            sqlReadTotals, new { match_id = matchId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return Ok(new LikeTotalsDto
        {
            Href = row.href,
            Upvotes = (int)(row.upvotes ?? 0),
            Downvotes = (int)(row.downvotes ?? 0),
            Score = (int)(row.score ?? 0),
            UserVote = newVote,
            UpdatedAtUtc = DateTime.SpecifyKind((DateTime)row.updated_at, DateTimeKind.Utc),
            MatchUtc = row.match_utc is null ? null : DateTime.SpecifyKind((DateTime)row.match_utc, DateTimeKind.Utc)
        });
    }

    /// <summary>
    /// Get totals (anonymous OK), and the caller's vote if authenticated.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Get([FromQuery] string href, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(href))
            return BadRequest(new { error = "href is required" });
        if (href.Length > 600)
            return BadRequest(new { error = "href too long (max 600 chars)" });

        var hrefHash = Sha256(href.Trim());

        const string sql = @"
SELECT
  m.match_id,
  m.href,
  m.match_utc,
  COALESCE(t.upvotes, 0) AS Up,
  COALESCE(t.downvotes, 0) AS Down,
  COALESCE(t.score, 0) AS Score,
  COALESCE(t.updated_at, m.created_at) AS Updated
FROM matches m
LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
WHERE m.href_hash = @href_hash;";

        using var conn = Open();
        var rec = await conn.QuerySingleOrDefaultAsync(sql, new { href_hash = hrefHash });

        if (rec is null)
        {
            // Not seen before
            return Ok(new LikeTotalsDto
            {
                Href = href,
                Upvotes = 0,
                Downvotes = 0,
                Score = 0,
                UserVote = null,
                UpdatedAtUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
                MatchUtc = null
            });
        }

        int? userVote = null;
        if (User?.Identity?.IsAuthenticated == true)
        {
            if (GetRequiredUserId(out var userId) is null)
            {
                const string sqlUserVote = @"SELECT vote FROM user_match_votes WHERE user_id=@user_id AND match_id=@match_id;";
                userVote = await conn.ExecuteScalarAsync<int?>(sqlUserVote, new { user_id = userId, match_id = (ulong)rec.match_id });
            }
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

    /// <summary>
    /// NEW: Return all matches the current user has liked (vote = +1).
    /// Optional paging via skip/take. Sorted by MatchUtc desc (fallback to user vote updated_at).
    /// </summary>
    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMyLikes([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (take <= 0 || take > 200) take = 50;

        var auth = GetRequiredUserId(out var userId);
        if (auth is not null) return auth;

        const string sql = @"
SELECT
  m.href,
  m.match_utc,
  umv.updated_at    AS user_updated_at,
  COALESCE(t.upvotes, 0)   AS upvotes,
  COALESCE(t.downvotes, 0) AS downvotes,
  COALESCE(t.score, 0)     AS score
FROM user_match_votes umv
JOIN matches m       ON m.match_id = umv.match_id
LEFT JOIN match_vote_totals t ON t.match_id = umv.match_id
WHERE umv.user_id = @user_id AND umv.vote = 1
ORDER BY COALESCE(m.match_utc, umv.updated_at) DESC
LIMIT @take OFFSET @skip;";

        using var conn = Open();
        var rows = (await conn.QueryAsync(sql, new { user_id = userId, take, skip })).ToList();

        var list = rows.Select(r => new MyLikeDto
        {
            Href = r.href,
            MatchUtc = r.match_utc is null ? null : DateTime.SpecifyKind((DateTime)r.match_utc, DateTimeKind.Utc),
            UserUpdatedAtUtc = DateTime.SpecifyKind((DateTime)r.user_updated_at, DateTimeKind.Utc),
            Upvotes = r.upvotes,
            Downvotes = r.downvotes,
            Score = r.score
        });

        return Ok(new
        {
            skip,
            take,
            count = rows.Count,
            items = list
        });
    }
}

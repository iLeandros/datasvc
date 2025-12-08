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
    public sealed record VoteRequest(string Href, string? Title, sbyte Vote, DateTime? MatchUtc); // Vote in {-1,0,+1}, optional UTC kick-off
    public sealed class LikeTotalsDto
    {
        public string Href { get; set; } = "";
        public string? Title { get; set; }
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
        public string? Title { get; set; }
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
    private static (string primary, string? altPlus, string? altPct20) CanonicalHrefCandidates(string href)
    {
        var primary = href.Trim();                  // as received (already URL-decoded by ASP.NET)
        string? altPlus = null, altPct20 = null;
    
        if (primary.IndexOf(' ') >= 0)
        {
            altPlus  = primary.Replace(' ', '+');   // spaces → '+'
            altPct20 = primary.Replace(" ", "%20"); // spaces → '%20'
        }
    
        return (primary, altPlus, altPct20);
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
        if (href.Length > 600)
            return BadRequest(new { error = "href too long (max 600 chars)" });

        var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim();
    
        var newVote = (sbyte)req.Vote;
    
        // Prefer the synchronous helper you already have
        var authResult = GetRequiredUserId(out var userId);
        if (authResult != null)
            return authResult;
    
        var matchUtc = ForceUtc(req.MatchUtc);
    
        // Build canonical candidates exactly like GET
        var (h1, h2, h3) = CanonicalHrefCandidates(href);
        var h1Hash = Sha256(h1);
        var h2Hash = h2 is null ? null : Sha256(h2);
        var h3Hash = h3 is null ? null : Sha256(h3);
    
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
    
        try
        {
            // 1) Find or create a match_id
            ulong matchId = await conn.ExecuteScalarAsync<ulong?>(@"
                SELECT match_id
                  FROM matches
                 WHERE href_hash = @h1Hash
                    OR (@h2Hash IS NOT NULL AND href_hash = @h2Hash)
                    OR (@h3Hash IS NOT NULL AND href_hash = @h3Hash)
                 LIMIT 1;",
                new { h1Hash, h2Hash, h3Hash }, tx) ?? 0UL;
    
            if (matchId == 0UL)
            {
                // Insert a single canonical row (store h1)
                await conn.ExecuteAsync(@"
                    INSERT INTO matches (href_hash, href, title, match_utc)
                    VALUES (@h1Hash, @h1, @matchUtc)
                    ON DUPLICATE KEY UPDATE
                        href = VALUES(href),
                        match_utc = COALESCE(VALUES(match_utc), match_utc);",
                    new { h1Hash, h1, title, matchUtc }, tx);
    
                matchId = await conn.ExecuteScalarAsync<ulong>(
                    "SELECT match_id FROM matches WHERE href_hash=@h1Hash LIMIT 1;",
                    new { h1Hash }, tx);
    
                // Ensure a totals row exists for the new match
                await conn.ExecuteAsync(
                    "INSERT IGNORE INTO match_vote_totals (match_id) VALUES (@mid);",
                    new { mid = matchId }, tx);
            }
            else
            {
                // Backfill/correct match_utc if caller knows it now
                if (matchUtc is not null)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE matches
                           SET match_utc = @matchUtc
                         WHERE match_id  = @mid
                           AND (match_utc IS NULL OR match_utc <> @matchUtc);",
                        new { matchUtc, mid = matchId }, tx);
                }
                if (!string.IsNullOrWhiteSpace(title))
                {
                    await conn.ExecuteAsync(@"
                        UPDATE matches
                           SET title = @title
                         WHERE match_id = @mid
                           AND (title IS NULL OR title = '');",
                        new { title, mid = matchId }, tx);
                }
            }
    
            // Always ensure totals row exists (idempotent)
            await conn.ExecuteAsync(
                "INSERT IGNORE INTO match_vote_totals (match_id) VALUES (@mid);",
                new { mid = matchId }, tx);
    
            // 2) Previous vote (treat null as 0; clamp defensively)
            int prevRaw = await conn.ExecuteScalarAsync<int?>(@"
                SELECT vote FROM user_match_votes WHERE user_id=@uid AND match_id=@mid;",
                new { uid = userId, mid = matchId }, tx) ?? 0;
            sbyte prev = (sbyte)Math.Clamp(prevRaw, -1, 1);
    
            // 3) If unchanged, just read and return totals (LEFT JOIN so we never throw)
            if (prev == newVote)
            {
                var unchanged = await conn.QueryFirstAsync<(int Up, int Down, int Score, DateTime Updated, DateTime? MUtc, string? Title)>(@"
                    SELECT
                      COALESCE(t.upvotes, 0)                    AS upvotes,
                      COALESCE(t.downvotes, 0)                  AS downvotes,
                      COALESCE(t.score, 0)                      AS score,
                      COALESCE(t.updated_at, m.created_at)      AS updated_at,
                      m.match_utc                               AS match_utc,
                      m.title                                   AS title
                    FROM matches m
                    LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
                    WHERE m.match_id = @mid;",
                    new { mid = matchId }, tx);

                await tx.CommitAsync(ct);
    
                return Ok(new LikeTotalsDto
                {
                    Href = href,
                    Title = unchanged.Title,                    // NEW
                    Upvotes = unchanged.Up,
                    Downvotes = unchanged.Down,
                    Score = unchanged.Score,
                    UserVote = prev,
                    UpdatedAtUtc = DateTime.SpecifyKind(unchanged.Updated, DateTimeKind.Utc),
                    MatchUtc = unchanged.MUtc
                });
            }
    
            // 4) Upsert the user's vote
            await conn.ExecuteAsync(@"
                INSERT INTO user_match_votes (user_id, match_id, vote)
                VALUES (@uid, @mid, @vote)
                ON DUPLICATE KEY UPDATE vote = VALUES(vote);",
                new { uid = userId, mid = matchId, vote = newVote }, tx);
    
            // 5) Apply deltas to totals
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
    
            // 6) Return fresh totals (LEFT JOIN + COALESCE)
            var row = await conn.QueryFirstAsync<(int Up, int Down, int Score, DateTime Updated, DateTime? MUtc, string? Title)>(@"
                SELECT
                  COALESCE(t.upvotes, 0)                    AS upvotes,
                  COALESCE(t.downvotes, 0)                  AS downvotes,
                  COALESCE(t.score, 0)                      AS score,
                  COALESCE(t.updated_at, m.created_at)      AS updated_at,
                  m.match_utc                               AS match_utc,
                  m.title                                   AS title
                FROM matches m
                LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
                WHERE m.match_id = @mid;",
                new { mid = matchId }, tx);
    
            await tx.CommitAsync(ct);
    
            return Ok(new LikeTotalsDto
            {
                Href = href,
                Title = row.Title,                          // NEW
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

    // PUBLIC: GET /v1/likes/matches
    // Lists matches + aggregated votes, optionally including the caller's vote.
    // Query:
    //   skip (default 0), take (default 50, max 200)
    //   withVotesOnly (default true) → only rows where there is at least 1 up/down vote
    //   sort = "recent" | "top" (default "recent")
    [HttpGet("matches")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAllMatches(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] bool withVotesOnly = true,
        [FromQuery] string? sort = "recent",
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 200) take = 50;
        if (skip < 0) skip = 0;
    
        // Try to identify user (anonymous OK). We'll only use the id if we have it.
        ulong? userIdForJoin = null;
        var (err, uid) = await GetRequiredUserIdAsync(ct); // same helper you use in GET /v1/likes
        if (err is null && uid != 0UL)
            userIdForJoin = uid; // include caller's vote per match if available  :contentReference[oaicite:1]{index=1}
    
        // Safe ORDER BY choices
        var orderBy = (sort ?? "recent").Trim().ToLowerInvariant() switch
        {
            "top"    => "COALESCE(t.score,0) DESC, COALESCE(m.match_utc, COALESCE(t.updated_at, m.created_at)) DESC",
            _        => "COALESCE(m.match_utc, COALESCE(t.updated_at, m.created_at)) DESC"
        };
    
        var where = withVotesOnly
            ? "WHERE (COALESCE(t.upvotes,0) + COALESCE(t.downvotes,0)) > 0"
            : "";
    
        var sql = $@"
                    SELECT
                      m.href,
                      m.title,
                      m.match_utc,
                      COALESCE(t.upvotes,   0)    AS upvotes,
                      COALESCE(t.downvotes, 0)    AS downvotes,
                      COALESCE(t.score,     0)    AS score,
                      COALESCE(t.updated_at, m.created_at) AS updated_at,
                      umv.vote AS user_vote
                    FROM matches m
                    LEFT JOIN match_vote_totals t
                           ON t.match_id = m.match_id
                    LEFT JOIN user_match_votes umv
                           ON umv.match_id = m.match_id
                          AND umv.user_id  = @user_id
                    {where}
                    ORDER BY {orderBy}
                    LIMIT @skip, @take;";
    
        await using var conn = Open(); // your existing helper  :contentReference[oaicite:2]{index=2}
        var rows = (await conn.QueryAsync(sql, new
        {
            user_id = userIdForJoin, // null is fine → no row in LEFT JOIN
            skip,
            take
        })).ToList();
    
        var items = rows.Select(r => new LikeTotalsDto
        {
            Href = r.href,
            Title = r.title,                             // NEW
            Upvotes = (int)r.upvotes,
            Downvotes = (int)r.downvotes,
            Score = (int)r.score,
            UserVote = r.user_vote is null ? (sbyte?)null : (sbyte)r.user_vote,
            UpdatedAtUtc = DateTime.SpecifyKind((DateTime)r.updated_at, DateTimeKind.Utc),
            MatchUtc = r.match_utc is null ? null : DateTime.SpecifyKind((DateTime)r.match_utc, DateTimeKind.Utc)
        });
    
        return Ok(new { skip, take, count = rows.Count, items });
    }

    // PUBLIC: GET /v1/likes/matches/on/{dateUtc}
    // Example: GET /v1/likes/matches/on/2025-10-07?skip=0&take=200&sort=time
    // Notes: {dateUtc} must be YYYY-MM-DD (interpreted as a UTC calendar day)
    [HttpGet("matches/on/{dateUtc}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMatchesOnDate(
        [FromRoute] string dateUtc,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 200,
        [FromQuery] string? sort = "time",
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 500) take = 200;
        if (skip < 0) skip = 0;
    
        // Parse YYYY-MM-DD as a UTC day
        if (!System.Text.RegularExpressions.Regex.IsMatch(dateUtc ?? "", @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest(new { error = "dateUtc must be YYYY-MM-DD" });
    
        var startUtc = DateTime.SpecifyKind(
            DateTime.ParseExact(dateUtc, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);
    
        // Try to include the caller's vote if we can resolve a user id (anonymous OK)
        ulong? userIdForJoin = null;
        var (err, uid) = await GetRequiredUserIdAsync(ct); // same helper as your GET /v1/likes
        if (err is null && uid != 0UL)
            userIdForJoin = uid;                                                       // include UserVote when available
    
        // Sorting options: by kickoff time or by score
        var orderBy = (sort ?? "time").Trim().ToLowerInvariant() switch
        {
            "top"  => "COALESCE(t.score,0) DESC, COALESCE(m.match_utc, COALESCE(t.updated_at, m.created_at)) DESC",
            _      => "COALESCE(m.match_utc, COALESCE(t.updated_at, m.created_at)) ASC"
        };
    
        var sql = $@"
                    SELECT
                      m.href,
                      m.title,
                      m.match_utc,
                      COALESCE(t.upvotes,   0) AS upvotes,
                      COALESCE(t.downvotes, 0) AS downvotes,
                      COALESCE(t.score,     0) AS score,
                      COALESCE(t.updated_at, m.created_at) AS updated_at,
                      umv.vote AS user_vote
                    FROM matches m
                    LEFT JOIN match_vote_totals t
                           ON t.match_id = m.match_id
                    LEFT JOIN user_match_votes umv
                           ON umv.match_id = m.match_id
                          AND umv.user_id  = @user_id
                    WHERE m.match_utc >= @startUtc AND m.match_utc < @endUtc
                    ORDER BY {orderBy}
                    LIMIT @skip, @take;";
    
        await using var conn = Open();
        var rows = (await conn.QueryAsync(sql, new
        {
            user_id = userIdForJoin, // null → no row from LEFT JOIN (fine)
            startUtc,
            endUtc,
            skip,
            take
        })).ToList();
    
        var items = rows.Select(r => new LikeTotalsDto
        {
            Href = r.href,
            Title = r.title,                             // NEW
            Upvotes = (int)r.upvotes,
            Downvotes = (int)r.downvotes,
            Score = (int)r.score,
            UserVote = r.user_vote is null ? (sbyte?)null : (sbyte)r.user_vote,
            UpdatedAtUtc = DateTime.SpecifyKind((DateTime)r.updated_at, DateTimeKind.Utc),
            MatchUtc = r.match_utc is null ? null : DateTime.SpecifyKind((DateTime)r.match_utc, DateTimeKind.Utc)
        });
    
        return Ok(new { dateUtc, skip, take, count = rows.Count, items });
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
        var (h1, h2, h3) = CanonicalHrefCandidates(href);
        var h1Hash = Sha256(h1);
        var h2Hash = h2 is null ? null : Sha256(h2);
        var h3Hash = h3 is null ? null : Sha256(h3);
    
        const string sql = @"
            SELECT m.match_id, m.href, m.title, m.match_utc,
                   COALESCE(t.upvotes,0) Up, COALESCE(t.downvotes,0) Down,
                   COALESCE(t.score,0) Score, COALESCE(t.updated_at, m.created_at) Updated
            FROM matches m
            LEFT JOIN match_vote_totals t ON t.match_id = m.match_id
            WHERE m.href_hash = @h1
               OR (@h2 IS NOT NULL AND m.href_hash = @h2)
               OR (@h3 IS NOT NULL AND m.href_hash = @h3)
            LIMIT 1;";
    
        await using var conn = Open();
        var rec = await conn.QuerySingleOrDefaultAsync(sql, new { h1 = h1Hash, h2 = h2Hash, h3 = h3Hash });
    
        if (rec is null)
        {
            return Ok(new LikeTotalsDto
            {
                Href = h1,
                Title = "",                           // NEW
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
            Title = rec.title,                           // NEW
            Upvotes = (int)rec.Up,
            Downvotes = (int)rec.Down,
            Score = (int)rec.Score,
            UserVote = userVote,
            UpdatedAtUtc = DateTime.SpecifyKind((DateTime)rec.Updated, DateTimeKind.Utc),
            MatchUtc = rec.match_utc is null ? null : DateTime.SpecifyKind((DateTime)rec.match_utc, DateTimeKind.Utc)
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
              m.title,
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
            Title = r.title,                           // NEW
            MatchUtc = r.match_utc is null ? null : DateTime.SpecifyKind((DateTime)r.match_utc, DateTimeKind.Utc),
            UserUpdatedAtUtc = DateTime.SpecifyKind((DateTime)r.user_updated_at, DateTimeKind.Utc),
            Upvotes = (int)r.upvotes,
            Downvotes = (int)r.downvotes,
            Score = (int)r.score
        });
    
        return Ok(new { skip, take, count = rows.Count, items });
    }

}

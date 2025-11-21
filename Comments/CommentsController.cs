using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Comments;

[ApiController]
[Route("v1/comments")]
public sealed class CommentsController : ControllerBase
{
    private readonly string? _connString;
    private readonly ILogger<CommentsController> _log;

    public CommentsController(IConfiguration cfg, ILogger<CommentsController> log)
    {
        _connString = cfg.GetConnectionString("Default");
        _log = log;
    }

    // ===== DTOs =====
    public sealed record PostCommentRequest(string Href, string Text, DateTime? MatchUtc);
    public sealed record EditCommentRequest(string Text);

    public sealed class CommentDto
    {
        public ulong CommentId { get; set; }
        public ulong MatchId { get; set; }
        public ulong UserId { get; set; }
        public string Href { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public bool IsDeleted { get; set; }

        // NEW
        public string? DisplayName { get; set; }
        public string? AvatarUrl  { get; set; }
    }

    // ===== Helpers (mirrors LikesController) =====
    [NonAction]
    private MySqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connString))
            throw new InvalidOperationException("Missing ConnectionStrings:Default");
        return new MySqlConnection(_connString);
    }

    [NonAction]
    private static byte[] Sha256(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s ?? string.Empty));

    [NonAction]
    private static (string primary, string? altPlus, string? altPct20) CanonicalHrefCandidates(string href)
    {
        var primary = href.Trim();
        string? altPlus = null, altPct20 = null;
        if (primary.IndexOf(' ') >= 0)
        {
            altPlus  = primary.Replace(' ', '+');
            altPct20 = primary.Replace(" ", "%20");
        }
        return (primary, altPlus, altPct20);
    }

    [NonAction]
    private IActionResult? GetRequiredUserId(out ulong userId)
    {
        userId = 0;
        if (HttpContext.Items.TryGetValue("user_id", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out userId))
        {
            return null; // success
        }

        string?[] candidates =
        {
            User.FindFirstValue("uid"),
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue("sub")
        };
        foreach (var s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out userId))
                return null; // success
        }

        _log.LogWarning("Missing user id (no HttpContext.Items[\"user_id\"] and no uid/sub claim).");
        return Unauthorized(new { error = "Missing or invalid user ID" });
    }

    [NonAction]
    private static DateTime? ForceUtc(DateTime? dt)
    {
        if (dt is null) return null;
        if (dt.Value.Kind == DateTimeKind.Utc) return dt;
        if (dt.Value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
        return dt.Value.ToUniversalTime();
    }

    // ===== Endpoints =====

    // POST /v1/comments  { href, text, matchUtc? }
    // Creates a new comment for the (href→match) and returns it.
    [HttpPost]
    [Authorize]
    [Consumes("application/json")]
    public async Task<IActionResult> Post([FromBody] PostCommentRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Href))
            return BadRequest(new { error = "href is required" });
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required" });
        if (req.Text.Length > 1000)
            return BadRequest(new { error = "text length must be ≤ 1000" });

        var authResult = GetRequiredUserId(out var userId);
        if (authResult is not null) return authResult;

        var href = req.Href.Trim();
        var matchUtc = ForceUtc(req.MatchUtc);
        var (h1, h2, h3) = CanonicalHrefCandidates(href);
        var h1Hash = Sha256(h1);
        var h2Hash = h2 is null ? null : Sha256(h2);
        var h3Hash = h3 is null ? null : Sha256(h3);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1) Lookup or create the match row (same policy as Likes)
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
            await conn.ExecuteAsync(@"
                INSERT INTO matches (href_hash, href, match_utc)
                VALUES (@h1Hash, @h1, @matchUtc)
                ON DUPLICATE KEY UPDATE href = VALUES(href), match_utc = COALESCE(VALUES(match_utc), match_utc);",
                new { h1Hash, h1, matchUtc }, tx);

            matchId = await conn.ExecuteScalarAsync<ulong>(
                "SELECT match_id FROM matches WHERE href_hash=@h1Hash LIMIT 1;",
                new { h1Hash }, tx);
        }
        else if (matchUtc is not null)
        {
            await conn.ExecuteAsync(@"
                UPDATE matches
                   SET match_utc = @matchUtc
                 WHERE match_id  = @mid
                   AND (match_utc IS NULL OR match_utc <> @matchUtc);",
                new { matchUtc, mid = matchId }, tx);
        }

        // 2) Insert the comment
        const string insertSql = @"
            INSERT INTO comments (match_id, user_id, text)
            VALUES (@mid, @uid, @text);
            SELECT LAST_INSERT_ID();";

        var commentId = await conn.ExecuteScalarAsync<ulong>(insertSql, new
        {
            mid = matchId,
            uid = userId,
            text = req.Text.Trim()
        }, tx);

        // 3) Return the freshly created comment
        var created = await conn.QuerySingleAsync<CommentDto>(@"
            SELECT c.comment_id AS CommentId,
                   c.match_id   AS MatchId,
                   c.user_id    AS UserId,
                   m.href       AS Href,
                   c.text       AS Text,
                   c.created_at AS CreatedAtUtc,
                   c.updated_at AS UpdatedAtUtc,
                   c.is_deleted AS IsDeleted,
                   up.display_name AS DisplayName,
                   up.avatar_url   AS AvatarUrl
              FROM comments c
              JOIN matches  m  ON m.match_id = c.match_id
         LEFT JOIN user_profile up ON up.user_id = c.user_id
             WHERE c.comment_id = @cid;",
            new { cid = commentId }, tx);

        await tx.CommitAsync(ct);
        return Created($"/v1/comments/{commentId}", created);
    }

    // GET /v1/comments?href=...&limit=20&beforeId=12345
    // Lists newest-first comments for a given href using keyset pagination.
    [HttpGet]
    [Produces("application/json")]
    public async Task<IActionResult> List([FromQuery] string href, [FromQuery] int? limit, [FromQuery] ulong? beforeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(href))
            return BadRequest(new { error = "href is required" });

        var (h1, h2, h3) = CanonicalHrefCandidates(href.Trim());
        var h1Hash = Sha256(h1);
        var h2Hash = h2 is null ? null : Sha256(h2);
        var h3Hash = h3 is null ? null : Sha256(h3);

        const string findMatchSql = @"
            SELECT match_id FROM matches
             WHERE href_hash = @h1Hash
                OR (@h2Hash IS NOT NULL AND href_hash = @h2Hash)
                OR (@h3Hash IS NOT NULL AND href_hash = @h3Hash)
             LIMIT 1;";

        await using var conn = Open();
        var matchId = await conn.ExecuteScalarAsync<ulong?>(findMatchSql, new { h1Hash, h2Hash, h3Hash });
        if (!matchId.HasValue)
            return Ok(new { items = Array.Empty<CommentDto>(), nextBeforeId = (ulong?)null });

        int take = Math.Clamp(limit ?? 20, 1, 100);

        var rows = await conn.QueryAsync<CommentDto>(@"
            SELECT c.comment_id AS CommentId,
                   c.match_id   AS MatchId,
                   c.user_id    AS UserId,
                   m.href       AS Href,
                   c.text       AS Text,
                   c.created_at AS CreatedAtUtc,
                   c.updated_at AS UpdatedAtUtc,
                   c.is_deleted AS IsDeleted,
                   up.display_name AS DisplayName,
                   up.avatar_url   AS AvatarUrl
              FROM comments c
              JOIN matches  m  ON m.match_id = c.match_id
         LEFT JOIN user_profile up ON up.user_id = c.user_id
             WHERE c.match_id = @mid
               AND (@beforeId IS NULL OR c.comment_id < @beforeId)
          ORDER BY c.comment_id DESC
             LIMIT @take;",
            new { mid = matchId.Value, beforeId, take });

        ulong? next = rows.Any() ? rows.Last().CommentId : null;
        return Ok(new { items = rows, nextBeforeId = next });
    }

    // PUT /v1/comments/{id}
    // Only the author can edit. Returns the updated comment.
    //[HttpPut("{id:ulong}")]
    [HttpPut("{id}")]
    [Authorize]
    [Consumes("application/json")]
    public async Task<IActionResult> Edit([FromRoute] ulong id, [FromBody] EditCommentRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required" });
        if (req.Text.Length > 1000)
            return BadRequest(new { error = "text length must be ≤ 1000" });

        var authResult = GetRequiredUserId(out var userId);
        if (authResult is not null) return authResult;

        await using var conn = Open();
        var affected = await conn.ExecuteAsync(@"
            UPDATE comments
               SET text = @text
             WHERE comment_id = @id AND user_id = @uid AND is_deleted = 0;",
            new { id, uid = userId, text = req.Text.Trim() });

        if (affected == 0) return NotFound(new { error = "comment not found or not owned by user" });

        var updated = await conn.QuerySingleAsync<CommentDto>(@"
            SELECT c.comment_id AS CommentId,
                   c.match_id   AS MatchId,
                   c.user_id    AS UserId,
                   m.href       AS Href,
                   c.text       AS Text,
                   c.created_at AS CreatedAtUtc,
                   c.updated_at AS UpdatedAtUtc,
                   c.is_deleted AS IsDeleted
              FROM comments c
              JOIN matches  m ON m.match_id = c.match_id
             WHERE c.comment_id = @cid;",
            new { cid = id });

        return Ok(updated);
    }

    // DELETE /v1/comments/{id}
    // Soft-delete. Only the author can delete.
    //[HttpDelete("{id:ulong}")]
    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Delete([FromRoute] ulong id, CancellationToken ct)
    {
        var authResult = GetRequiredUserId(out var userId);
        if (authResult is not null) return authResult;

        await using var conn = Open();
        var affected = await conn.ExecuteAsync(@"
            UPDATE comments
               SET is_deleted = 1
             WHERE comment_id = @id AND user_id = @uid AND is_deleted = 0;",
            new { id, uid = userId });

        if (affected == 0) return NotFound(new { error = "comment not found or not owned by user" });
        return NoContent();
    }
}

using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Users;

[ApiController]
[Route("v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly string? _connString;
    private readonly ILogger<UsersController> _log;

    public UsersController(IConfiguration cfg, ILogger<UsersController> log)
    {
        _connString = cfg.GetConnectionString("Default");
        _log = log;
    }

    // ===== DTOs =====
    public sealed class PublicUserProfileDto
    {
        public ulong   UserId { get; set; }
        public string? Username { get; set; }
        public DateTime JoinedAtUtc { get; set; }
        public string? AvatarUrl { get; set; }
        public string[] Roles { get; set; } = Array.Empty<string>();
        public int FollowersCount { get; set; }
        public int FollowingCount { get; set; }
        public int CommentsCount { get; set; }
        public int CommentLikesReceived { get; set; }
        public int LikesCastUp { get; set; }
        public int LikesCastDown { get; set; }
    }

    public sealed class FollowDto
    {
        public ulong UserId { get; set; }
        public string? Username { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTime FollowedAtUtc { get; set; }
    }

    private MySqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connString))
            throw new InvalidOperationException("Missing ConnectionStrings:Default");
        return new MySqlConnection(_connString);
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();

    private ulong? TryGetUserId()
    {
        if (HttpContext.Items.TryGetValue("user_id", out var raw) &&
            raw is not null &&
            ulong.TryParse(raw.ToString(), out var id)) return id;

        string?[] ids = {
            User.FindFirstValue("uid"),
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue("sub")
        };
        foreach (var c in ids)
            if (!string.IsNullOrWhiteSpace(c) && ulong.TryParse(c, out var id2))
                return id2;

        return null;
    }

    // -------------------------------------------------
    // GET /v1/users/{idOrName}  (anonymous public page)
    // Accepts numeric user id OR display name (case-insensitive)
    // -------------------------------------------------
    [HttpGet("{idOrName}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicProfile([FromRoute] string idOrName, CancellationToken ct)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);

        ulong? userId = null;
        string? norm = null;
        if (ulong.TryParse(idOrName, out var parsed))
            userId = parsed;
        else
            norm = Normalize(idOrName);

        const string sql = @"
            SELECT u.id                      AS UserId,
                   up.display_name           AS Username,
                   u.created_at              AS JoinedAtUtc,
                   up.avatar_url             AS AvatarUrl
              FROM users u
              JOIN user_profile up ON up.user_id = u.id
             WHERE (@uid IS NOT NULL AND u.id = @uid)
                OR (@norm IS NOT NULL AND up.display_name_norm = @norm)
             LIMIT 1;";  //-- users.id / user_profile.* from dump (names match)  -- 

        var row = await conn.QueryFirstOrDefaultAsync<(ulong UserId, string Username, DateTime JoinedAtUtc, string AvatarUrl)>(
            new CommandDefinition(sql, new { uid = (object?)userId ?? DBNull.Value, norm = (object?)norm ?? DBNull.Value }, cancellationToken: ct)
        );
        if (row.UserId == 0) return NotFound();

        // Roles (roles.id + user_roles from dump)
        var roles = (await conn.QueryAsync<string>(@"
            SELECT r.name
              FROM roles r
              JOIN user_roles ur ON ur.role_id = r.id
             WHERE ur.user_id = @uid;", new { uid = row.UserId })).ToArray();  // :contentReference[oaicite:8]{index=8}

        // Social counts
        var counts = await conn.QueryFirstAsync<(int Followers, int Following)>(@"
            SELECT
              (SELECT COUNT(*) FROM user_follows WHERE followee_id = @uid) AS Followers,
              (SELECT COUNT(*) FROM user_follows WHERE follower_id = @uid) AS Following;",
            new { uid = row.UserId });

        // Activity counts (comments / likes / votes)
        var activity = await conn.QueryFirstAsync<(int Comments, int LikesUp, int LikesDown, int LikesOnComments)>(@"
            SELECT
              (SELECT COUNT(*) FROM comments c WHERE c.user_id=@uid AND c.is_deleted=0) AS Comments,               -- comments.is_deleted  :contentReference[oaicite:9]{index=9}
              (SELECT COUNT(*) FROM user_match_votes mv WHERE mv.user_id=@uid AND mv.vote= 1) AS LikesUp,          -- tinyint vote        :contentReference[oaicite:10]{index=10}
              (SELECT COUNT(*) FROM user_match_votes mv WHERE mv.user_id=@uid AND mv.vote=-1) AS LikesDown,
              (SELECT COUNT(*) FROM comment_likes cl
                 JOIN comments c2 ON c2.comment_id = cl.comment_id
               WHERE c2.user_id=@uid) AS LikesOnComments;",                                                       -- comment_likes join   :contentReference[oaicite:11]{index=11}
            new { uid = row.UserId });

        var dto = new PublicUserProfileDto
        {
            UserId = row.UserId,
            Username = row.Username,
            JoinedAtUtc = DateTime.SpecifyKind(row.JoinedAtUtc, DateTimeKind.Utc),
            AvatarUrl = row.AvatarUrl,
            Roles = roles,
            FollowersCount = counts.Followers,
            FollowingCount = counts.Following,
            CommentsCount = activity.Comments,
            CommentLikesReceived = activity.LikesOnComments,
            LikesCastUp = activity.LikesUp,
            LikesCastDown = activity.LikesDown
        };

        return Ok(dto);
    }

    // ----------------------------------------
    // GET /v1/users/{id}/followers  (anonymous)
    // GET /v1/users/{id}/following  (anonymous)
    // ----------------------------------------
    [HttpGet("{id:ulong}/followers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetFollowers([FromRoute] ulong id, [FromQuery] int limit = 25, [FromQuery] ulong? after = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        const string sql = @"
            SELECT f.follower_id                     AS UserId,
                   up.display_name                   AS Username,
                   up.avatar_url                     AS AvatarUrl,
                   f.created_at                      AS FollowedAtUtc
              FROM user_follows f
              JOIN user_profile up ON up.user_id = f.follower_id
             WHERE f.followee_id = @uid
               AND (@after IS NULL OR f.follower_id > @after)
             ORDER BY f.follower_id
             LIMIT @lim;";
        await using var conn = Open();
        var rows = await conn.QueryAsync<FollowDto>(new CommandDefinition(sql, new { uid = id, lim = limit, after }, cancellationToken: ct));
        return Ok(rows);
    }

    [HttpGet("{id:ulong}/following")]
    [AllowAnonymous]
    public async Task<IActionResult> GetFollowing([FromRoute] ulong id, [FromQuery] int limit = 25, [FromQuery] ulong? after = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        const string sql = @"
            SELECT f.followee_id                     AS UserId,
                   up.display_name                   AS Username,
                   up.avatar_url                     AS AvatarUrl,
                   f.created_at                      AS FollowedAtUtc
              FROM user_follows f
              JOIN user_profile up ON up.user_id = f.followee_id
             WHERE f.follower_id = @uid
               AND (@after IS NULL OR f.followee_id > @after)
             ORDER BY f.followee_id
             LIMIT @lim;";
        await using var conn = Open();
        var rows = await conn.QueryAsync<FollowDto>(new CommandDefinition(sql, new { uid = id, lim = limit, after }, cancellationToken: ct));
        return Ok(rows);
    }

    // ----------------------------------------
    // POST /v1/users/{id}/follow   (auth)
    // DELETE /v1/users/{id}/follow (auth)
    // ----------------------------------------
    [HttpPost("{id:ulong}/follow")]
    [Authorize]
    public async Task<IActionResult> Follow([FromRoute] ulong id, CancellationToken ct)
    {
        var me = TryGetUserId();
        if (me is null) return Unauthorized(new { error = "Missing or invalid user ID" });
        if (me.Value == id) return BadRequest(new { error = "You cannot follow yourself." });

        const string sql = @"INSERT IGNORE INTO user_follows (follower_id, followee_id) VALUES (@me, @them);";
        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { me = me.Value, them = id }, cancellationToken: ct));
        return Ok(); // idempotent
    }

    [HttpDelete("{id:ulong}/follow")]
    [Authorize]
    public async Task<IActionResult> Unfollow([FromRoute] ulong id, CancellationToken ct)
    {
        var me = TryGetUserId();
        if (me is null) return Unauthorized(new { error = "Missing or invalid user ID" });

        const string sql = @"DELETE FROM user_follows WHERE follower_id=@me AND followee_id=@them;";
        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { me = me.Value, them = id }, cancellationToken: ct));
        return NoContent();
    }

    // ----------------------------------------
    // GET /v1/users/{id}/activity  (anonymous)
    // recent comments + votes
    // ----------------------------------------
    public sealed class ActivityDto
    {
        public IEnumerable<object> RecentComments { get; set; } = Array.Empty<object>();
        public IEnumerable<object> RecentLikes    { get; set; } = Array.Empty<object>();
    }

    [HttpGet("{id:ulong}/activity")]
    [AllowAnonymous]
    public async Task<IActionResult> GetActivity([FromRoute] ulong id, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        await using var conn = Open();

        var comments = await conn.QueryAsync(new CommandDefinition(@"
            SELECT c.comment_id, c.text, c.created_at AS CreatedAtUtc, m.href
              FROM comments c
              JOIN matches  m ON m.match_id = c.match_id
             WHERE c.user_id=@uid AND c.is_deleted=0
             ORDER BY c.created_at DESC
             LIMIT @lim;", new { uid = id, lim = limit }, cancellationToken: ct));  // comments/matches per dump  

        var likes = await conn.QueryAsync(new CommandDefinition(@"
            SELECT mv.match_id, m.href, mv.updated_at AS UpdatedAtUtc, mv.vote
              FROM user_match_votes mv
              JOIN matches m ON m.match_id = mv.match_id
             WHERE mv.user_id=@uid AND mv.vote <> 0
             ORDER BY mv.updated_at DESC
             LIMIT @lim;", new { uid = id, lim = limit }, cancellationToken: ct));   // user_match_votes.vote tinyint  :contentReference[oaicite:13]{index=13}

        return Ok(new ActivityDto { RecentComments = comments, RecentLikes = likes });
    }
}

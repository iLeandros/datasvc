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
        
        public IReadOnlyList<ulong> FollowersIds { get; set; } = Array.Empty<ulong>();
        public IReadOnlyList<ulong> FollowingIds { get; set; } = Array.Empty<ulong>();
        
        public int CommentsCount { get; set; }
        public int CommentLikesReceived { get; set; }
        public int LikesCastUp { get; set; }
        public int LikesCastDown { get; set; }
    }
    
    public sealed class UserCardDto
    {
        public ulong   UserId      { get; set; }
        public string? Username    { get; set; }
        public DateTime JoinedAtUtc{ get; set; }
        public string? AvatarUrl   { get; set; }
        public string[] Roles      { get; set; } = Array.Empty<string>();
    }

    public sealed class IdsRequest
    {
        public IReadOnlyList<ulong>? Ids { get; set; }
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
    // Add under your other methods
    [NonAction]
    private async Task<(IActionResult? error, ulong userId)> GetRequiredUserIdAsync(CancellationToken ct)
    {
        // 1) HttpContext.Items
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
    
        _log.LogWarning("UsersController: Missing user id: no Items, no claim, and token not found in debug_tokens.");
        return (Unauthorized(new { error = "Missing or invalid user identity" }), 0UL);
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
            SELECT u.id            AS UserId,
                   up.display_name AS Username,
                   u.created_at    AS JoinedAtUtc,
                   up.avatar_url   AS AvatarUrl
              FROM users u
              JOIN user_profile up ON up.user_id = u.id
             WHERE (@uid IS NOT NULL AND u.id = @uid)
                OR (@norm IS NOT NULL AND up.display_name_norm = @norm)
             LIMIT 1;";
    
        var row = await conn.QueryFirstOrDefaultAsync<(ulong UserId, string Username, DateTime JoinedAtUtc, string AvatarUrl)>(
            new CommandDefinition(sql, new { uid = (object?)userId ?? DBNull.Value, norm = (object?)norm ?? DBNull.Value }, cancellationToken: ct)
        );
        if (row.UserId == 0) return NotFound();
    
        // Roles
        var roles = (await conn.QueryAsync<string>(@"
            SELECT r.name
              FROM roles r
              JOIN user_roles ur ON ur.role_id = r.id
             WHERE ur.user_id = @uid;",
            new { uid = row.UserId })).ToArray();
    
        // Followers / Following -> lists of user ids
        var followersIds = (await conn.QueryAsync<ulong>(@"
            SELECT follower_id
              FROM user_follows
             WHERE followee_id = @uid
             ORDER BY follower_id;",
            new { uid = row.UserId })).AsList();
    
        var followingIds = (await conn.QueryAsync<ulong>(@"
            SELECT followee_id
              FROM user_follows
             WHERE follower_id = @uid
             ORDER BY followee_id;",
            new { uid = row.UserId })).AsList();
    
        // Activity counts (unchanged)
        var activity = await conn.QueryFirstAsync<(int Comments, int LikesUp, int LikesDown, int LikesOnComments)>(@"
            SELECT
              (SELECT COUNT(*) FROM comments c WHERE c.user_id=@uid AND c.is_deleted=0) AS Comments,
              (SELECT COUNT(*) FROM user_match_votes mv WHERE mv.user_id=@uid AND mv.vote= 1) AS LikesUp,
              (SELECT COUNT(*) FROM user_match_votes mv WHERE mv.user_id=@uid AND mv.vote=-1) AS LikesDown,
              (SELECT COUNT(*) FROM comment_likes cl
                 JOIN comments c2 ON c2.comment_id = cl.comment_id
               WHERE c2.user_id=@uid) AS LikesOnComments;",
            new { uid = row.UserId });
    
        var dto = new PublicUserProfileDto
        {
            UserId = row.UserId,
            Username = row.Username,
            JoinedAtUtc = DateTime.SpecifyKind(row.JoinedAtUtc, DateTimeKind.Utc),
            AvatarUrl = row.AvatarUrl,
            Roles = roles,
            FollowersIds = followersIds,
            FollowingIds = followingIds,
            CommentsCount = activity.Comments,
            CommentLikesReceived = activity.LikesOnComments,
            LikesCastUp = activity.LikesUp,
            LikesCastDown = activity.LikesDown
        };
    
        return Ok(dto);
    }

    [HttpPost("profiles")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProfilesByIds([FromBody] IdsRequest body, CancellationToken ct)
    {
        if (body?.Ids is null || body.Ids.Count == 0)
            return Ok(Array.Empty<UserCardDto>());
    
        // de-dupe & guard
        var ids = body.Ids.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
            return Ok(Array.Empty<UserCardDto>());
    
        await using var conn = Open();
        await conn.OpenAsync(ct);
    
        // Base profiles
        const string sqlProfiles = @"
            SELECT u.id            AS UserId,
                   up.display_name AS Username,
                   u.created_at    AS JoinedAtUtc,
                   up.avatar_url   AS AvatarUrl
              FROM users u
              JOIN user_profile up ON up.user_id = u.id
             WHERE u.id IN @ids;";
    
        var rows = (await conn.QueryAsync<(ulong UserId, string Username, DateTime JoinedAtUtc, string AvatarUrl)>(
            new CommandDefinition(sqlProfiles, new { ids }, cancellationToken: ct)
        )).ToList();
    
        if (rows.Count == 0) return Ok(Array.Empty<UserCardDto>());
    
        // Roles for these users (single query)
        const string sqlRoles = @"
            SELECT ur.user_id AS UserId, r.name AS RoleName
              FROM user_roles ur
              JOIN roles r ON r.id = ur.role_id
             WHERE ur.user_id IN @ids;";
    
        var roleLookup = new Dictionary<ulong, List<string>>();
        var roleRows = await conn.QueryAsync<(ulong UserId, string RoleName)>(
            new CommandDefinition(sqlRoles, new { ids }, cancellationToken: ct)
        );
        foreach (var (uid, role) in roleRows)
        {
            if (!roleLookup.TryGetValue(uid, out var list))
            {
                list = new List<string>();
                roleLookup[uid] = list;
            }
            list.Add(role);
        }
    
        // Map to DTOs
        var byId = rows.ToDictionary(
            r => r.UserId,
            r => new UserCardDto
            {
                UserId = r.UserId,
                Username = r.Username,
                JoinedAtUtc = DateTime.SpecifyKind(r.JoinedAtUtc, DateTimeKind.Utc),
                AvatarUrl = r.AvatarUrl,
                Roles = roleLookup.TryGetValue(r.UserId, out var roles) ? roles.ToArray() : Array.Empty<string>()
            });
    
        // Preserve original input order; skip IDs that weren’t found
        var ordered = new List<UserCardDto>(ids.Length);
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var dto)) ordered.Add(dto);
    
        return Ok(ordered);
    }

    // ----------------------------------------
    // GET /v1/users/{id}/followers  (anonymous)
    // GET /v1/users/{id}/following  (anonymous)
    // ----------------------------------------
    [HttpGet("{id}/followers")]
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

    [HttpGet("{id}/following")]
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
    [HttpPost("{id}/follow")]
    [Authorize]
    public async Task<IActionResult> Follow([FromRoute] ulong id, CancellationToken ct)
    {
        var (err, me) = await GetRequiredUserIdAsync(ct);
        if (err is not null) return err;
        if (me == id) return BadRequest(new { error = "You cannot follow yourself." });
    
        const string sql = @"INSERT IGNORE INTO user_follows (follower_id, followee_id) VALUES (@me, @them);";
        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { me, them = id }, cancellationToken: ct));
        return Ok(); // idempotent
    }
    
    [HttpDelete("{id}/follow")]
    [Authorize]
    public async Task<IActionResult> Unfollow([FromRoute] ulong id, CancellationToken ct)
    {
        var (err, me) = await GetRequiredUserIdAsync(ct);
        if (err is not null) return err;
    
        const string sql = @"DELETE FROM user_follows WHERE follower_id=@me AND followee_id=@them;";
        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { me, them = id }, cancellationToken: ct));
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

    [HttpGet("{id}/activity")]
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

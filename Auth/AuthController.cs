using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Auth;

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly string? _connString;
    public AuthController(IConfiguration cfg) => _connString = cfg.GetConnectionString("Default");

    // ====== DTOs ======
    public sealed class RegisterRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public sealed class LoginRequest    { public string Email { get; set; } = ""; public string Password { get; set; } = ""; public string? TotpCode { get; set; } }
    public sealed class LoginResponse   { public string? Token { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public UserDto? User { get; set; } public bool MfaRequired { get; set; } public string? Ticket { get; set; } }
    public sealed class UserDto         { public ulong Id { get; set; } public string Email { get; set; } = ""; public string? DisplayName { get; set; } public string[]? Roles { get; set; } }
    private sealed class UserAuthRow    { public ulong UserId { get; set; } public string Email { get; set; } = ""; public string PasswordHash { get; set; } = ""; }
    public sealed class ProfileDto
    {
        public ulong UserId { get; set; }
        public string? DisplayName { get; set; }
        public string? Locale { get; set; }
        public string? Timezone { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTime? Birthdate { get; set; }   // <--- NEW
    }

    public sealed class UpdateProfileRequest
    {
        public string? DisplayName { get; set; }
        public string? Locale { get; set; }
        public string? Timezone { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Birthdate { get; set; }     // ISO "yyyy-MM-dd" (optional)
    }

    // using Google.Apis.Auth;
    // using Dapper;
    // inject: IOptions<AuthOptions> _opts, IDbConnection _db, ILogger<AuthController> _log
    public sealed class GoogleLoginRequest { public string IdToken { get; set; } = ""; }
    
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<IActionResult> Google([FromBody] GoogleLoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken))
            return BadRequest("Missing idToken");
    
        GoogleJsonWebSignature.Payload payload;
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _opts.Value.GoogleWebClientId }
            };
            payload = await GoogleJsonWebSignature.ValidateAsync(req.IdToken, settings);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid Google ID token");
            return Unauthorized("Invalid Google token");
        }
    
        // payload.Sub (subject), payload.Email, payload.Name, payload.Picture
        var provider = "google";
        var subject  = payload.Subject;
        var email    = payload.Email ?? "";
    
        // 1) Identity -> user
        var userId = await _db.QueryFirstOrDefaultAsync<ulong?>(
            "SELECT user_id FROM user_identities WHERE provider=@provider AND subject=@subject",
            new { provider, subject });
    
        if (userId is null)
        {
            // Try match by email
            userId = await _db.QueryFirstOrDefaultAsync<ulong?>(
                @"SELECT ua.user_id FROM user_auth ua 
                  WHERE ua.email_norm = LOWER(@email) LIMIT 1", new { email });
    
            if (userId is null)
            {
                // Create user + auth/profile
                userId = await _db.ExecuteScalarAsync<ulong>("INSERT INTO users (uuid) VALUES (UUID()); SELECT LAST_INSERT_ID();");
                await _db.ExecuteAsync(
                    @"INSERT INTO user_auth (user_id, email, password_hash, email_verified_at)
                      VALUES (@uid, @em, 0x00, UTC_TIMESTAMP())",
                    new { uid = userId, em = email });
                await _db.ExecuteAsync(
                    @"INSERT INTO user_profile (user_id, display_name, avatar_url, locale)
                      VALUES (@uid, @name, @pic, @loc)",
                    new { uid = userId, name = payload.Name, pic = payload.Picture, loc = "en" });
            }
    
            await _db.ExecuteAsync(
                @"INSERT INTO user_identities (user_id, provider, subject, email)
                  VALUES (@uid, @provider, @subject, @em)
                  ON DUPLICATE KEY UPDATE email=VALUES(email)",
                new { uid = userId, provider, subject, em = email });
        }
    
        // 2) Create your normal session (reuse what /login uses)
        var sess = await CreateSessionAsync(userId.Value, ct); // your existing helper
        return Ok(new
        {
            token = sess.Token,
            expiresAt = sess.ExpiresAt,
            user = new
            {
                id = userId.Value,
                email = email,
                displayName = payload.Name,
                roles = new[] { "user" }
            }
        });
    }

    
    // GET profile
    [HttpGet("get")]
    [HttpGet("~/v1/user/profile")] // <--- NEW alias
    [Authorize]
    public async Task<IActionResult> Get()
    {
        if (string.IsNullOrWhiteSpace(_connString)) // <--- NEW guard
            return Problem("Missing ConnectionStrings:Default.");
    
        if (!TryGetUserId(out var uid)) return Unauthorized();
        await using var c = new MySqlConnection(_connString);
    
        await c.ExecuteAsync("INSERT IGNORE INTO user_profile(user_id) VALUES(@uid);", new { uid });
    
        var p = await c.QuerySingleAsync<ProfileDto>(@"
            SELECT user_id AS UserId,
                   display_name AS DisplayName,
                   locale, timezone,
                   avatar_url AS AvatarUrl,
                   birthdate
            FROM user_profile
            WHERE user_id = @uid
            LIMIT 1;", new { uid });
    
        return Ok(p);
    }
    
    // PUT profile
    [HttpPut("put")]
    [HttpPut("~/v1/user/profile")]  // <--- NEW alias
    [Authorize]
    public async Task<IActionResult> Put([FromBody] UpdateProfileRequest req)
    {
        if (string.IsNullOrWhiteSpace(_connString)) // <--- NEW guard
            return Problem("Missing ConnectionStrings:Default.");
    
        if (!TryGetUserId(out var uid)) return Unauthorized();
    
        string? dn  = Trunc(req.DisplayName, 100);
        string? loc = Trunc(req.Locale, 10);
        string? tz  = Trunc(req.Timezone, 50);
        string? av  = Trunc(req.AvatarUrl, 500);
    
        DateTime? bd = null;
        if (!string.IsNullOrWhiteSpace(req.Birthdate))
        {
            if (!DateTime.TryParse(req.Birthdate, out var tmp))
                return BadRequest("birthdate must be yyyy-MM-dd");
            bd = tmp.Date;
        }
    
        await using var c = new MySqlConnection(_connString);
        await c.ExecuteAsync("INSERT IGNORE INTO user_profile(user_id) VALUES(@uid);", new { uid });
    
        await c.ExecuteAsync(@"
            UPDATE user_profile
            SET display_name = @dn,
                locale       = NULLIF(@loc,''),
                timezone     = NULLIF(@tz,''),
                avatar_url   = NULLIF(@av,''),
                birthdate    = @bd
            WHERE user_id = @uid;",
            new { uid, dn, loc, tz, av, bd });
    
        var p = await c.QuerySingleAsync<ProfileDto>(@"
            SELECT user_id AS UserId,
                   display_name AS DisplayName,
                   locale, timezone,
                   avatar_url AS AvatarUrl,
                   birthdate
            FROM user_profile
            WHERE user_id = @uid
            LIMIT 1;", new { uid });
    
        return Ok(p);
    }

    // ====== REGISTER ======
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");

        var email = (req.Email ?? "").Trim();
        var password = req.Password ?? "";

        if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
            return BadRequest(new { error = "Invalid email." });
        if (password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters." });

        try
        {
            await using var conn = new MySqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var exists = await conn.ExecuteScalarAsync<int>(@"
                SELECT 1
                FROM user_auth
                WHERE email = @em OR email_norm = LOWER(TRIM(@em))
                LIMIT 1;", new { em = email }, tx);

            if (exists == 1)
                return Conflict(new { error = "Email already registered." });

            await conn.ExecuteAsync("INSERT INTO users (uuid) VALUES (UUID_TO_BIN(UUID()));", transaction: tx);
            var userId = await conn.ExecuteScalarAsync<ulong>("SELECT LAST_INSERT_ID();", transaction: tx);

            var hashStr = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
            var hashBytes = System.Text.Encoding.UTF8.GetBytes(hashStr);

            await conn.ExecuteAsync(@"
                INSERT INTO user_auth (user_id, email, password_hash, email_verified_at, last_login_at)
                VALUES (@uid, @email, @hash, NULL, NULL);",
                new { uid = userId, email, hash = hashBytes }, tx);

            await conn.ExecuteAsync("INSERT INTO user_profile (user_id) VALUES (@uid);", new { uid = userId }, tx);

            var roleId = await EnsureRole(conn, tx, "user");
            await conn.ExecuteAsync("INSERT IGNORE INTO user_roles (user_id, role_id) VALUES (@uid, @rid);",
                new { uid = userId, rid = roleId }, tx);

            var (token, tokenHash) = MakeToken();
            var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
            await conn.ExecuteAsync(@"
                INSERT INTO sessions (id, user_id, created_at, expires_at, ip_address, user_agent)
                VALUES (@id, @uid, CURRENT_TIMESTAMP(3), @exp, @ip, @ua);",
                new
                {
                    id = tokenHash,
                    uid = userId,
                    exp = expiresAt.UtcDateTime,
                    ip = GetClientIpBinary(HttpContext),
                    ua = Request.Headers.UserAgent.ToString() is var ua && ua.Length > 255 ? ua[..255] : ua
                }, tx);

            await tx.CommitAsync(ct);

            var user = await LoadUserDto(conn, userId, ct);
            return Ok(new LoginResponse { Token = token, ExpiresAt = expiresAt, User = user, MfaRequired = false });
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            return Conflict(new { error = "Email already registered." });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return Problem("Registration failed.");
        }
    }

    // ====== LOGIN ======
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_connString))
                return Problem("Missing ConnectionStrings:Default.");

            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Unauthorized(new { error = "Invalid email or password." });

            await using var conn = new MySqlConnection(_connString);
            await conn.OpenAsync(ct);

            var auth = await conn.QuerySingleOrDefaultAsync<UserAuthRow>(@"
                SELECT
                    ua.user_id AS UserId,
                    ua.email   AS Email,
                    CAST(ua.password_hash AS CHAR(100) CHARACTER SET utf8mb4) AS PasswordHash
                FROM user_auth ua
                JOIN users u ON u.id = ua.user_id
                WHERE ua.email_norm = LOWER(TRIM(@email)) OR ua.email = @email
                LIMIT 1;", new { email = req.Email });

            if (auth is null || string.IsNullOrWhiteSpace(auth.PasswordHash))
                return Unauthorized(new { error = "Invalid email or password." });

            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(req.Password, auth.PasswordHash); }
            catch { return Unauthorized(new { error = "Invalid email or password." }); }
            if (!ok) return Unauthorized(new { error = "Invalid email or password." });

            var (token, tokenHash) = MakeToken();
            var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

            await conn.ExecuteAsync(@"
                INSERT INTO sessions (id, user_id, created_at, expires_at, ip_address, user_agent)
                VALUES (@id, @uid, CURRENT_TIMESTAMP(3), @exp, @ip, @ua);",
                new
                {
                    id = tokenHash,
                    uid = auth.UserId,
                    exp = expiresAt.UtcDateTime,
                    ip = GetClientIpBinary(HttpContext),
                    ua = Request.Headers.UserAgent.ToString() is var ua && ua.Length > 255 ? ua[..255] : ua
                });

            var user = await LoadUserDto(conn, auth.UserId, ct);

            return Ok(new LoginResponse { Token = token, ExpiresAt = expiresAt, User = user, MfaRequired = false });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return Problem("Login failed: " + ex.Message);
        }
    }

    // ====== ME ======
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        await using var conn = new MySqlConnection(_connString);
        var user = await LoadUserDto(conn, userId, ct);
        return Ok(new { user });
    }

    // ====== LOGOUT ======
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = GetBearerToken(Request);
        if (string.IsNullOrEmpty(token))
            return NoContent();

        var (_, hash) = MakeToken(token); // hash existing token
        await using var conn = new MySqlConnection(_connString);
        await conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id;", new { id = hash });

        return NoContent();
    }

    // ====== helpers ======
    // ---------- helpers ----------
    private static string? Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
    private static async Task<uint> EnsureRole(MySqlConnection conn, MySqlTransaction tx, string name)
    {
        var id = await conn.ExecuteScalarAsync<uint>("SELECT id FROM roles WHERE name = @n LIMIT 1;", new { n = name }, tx);
        if (id != 0) return id;
        await conn.ExecuteAsync("INSERT INTO roles (name) VALUES (@n);", new { n = name }, tx);
        return await conn.ExecuteScalarAsync<uint>("SELECT LAST_INSERT_ID();", transaction: tx);
    }

    private static (string token, byte[] hash) MakeToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = SHA256.HashData(Convert.FromHexString(token));
        return (token, hash);
    }
    private static (string token, byte[] hash) MakeToken(string existingToken)
        => (existingToken, SHA256.HashData(Convert.FromHexString(existingToken)));

    private static string? GetBearerToken(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = auth.Substring("Bearer ".Length).Trim();
        return token.Length == 64 ? token : null;
    }

    private static byte[]? GetClientIpBinary(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.MapToIPv6().GetAddressBytes();

    // FIXED: instance-based; claims first, then Items["user_id"]
    private bool TryGetUserId(out ulong userId)
    {
        userId = 0;

        var claim =
            User?.FindFirst("uid")?.Value ??
            User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            User?.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(claim) && ulong.TryParse(claim, out userId))
            return true;

        if (HttpContext?.Items.TryGetValue("user_id", out var v) == true)
        {
            if (v is ulong ul) { userId = ul; return true; }
            if (ulong.TryParse(v?.ToString(), out userId)) return true;
        }

        return false;
    }

    private static async Task<UserDto> LoadUserDto(IDbConnection conn, ulong userId, CancellationToken ct)
    {
        var user = await conn.QuerySingleAsync<UserDto>(@"
            SELECT u.id AS Id,
                   ua.email AS Email,
                   up.display_name AS DisplayName,
                   up.avatar_url  AS AvatarUrl   -- <--- add this if you want it in 'me'
            FROM users u
            LEFT JOIN user_auth    ua ON ua.user_id = u.id
            LEFT JOIN user_profile up ON up.user_id = u.id
            WHERE u.id = @uid
            LIMIT 1;", new { uid = userId });
    
        var roles = (await conn.QueryAsync<string>(@"
            SELECT r.name FROM user_roles ur
            JOIN roles r ON r.id = ur.role_id
            WHERE ur.user_id = @uid;", new { uid = userId })).ToArray();
    
        user.Roles = roles;
        return user;
    }

}

using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataSvc.Auth;

public sealed class AuthOptions
{
    public string GoogleWebClientId { get; set; } = "";
}

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly string? _connString;
    private readonly IOptions<AuthOptions> _opts;
    private readonly ILogger<AuthController> _log;
    public AuthController(IConfiguration cfg, IOptions<AuthOptions> opts, ILogger<AuthController> log)
    {
        _connString = cfg.GetConnectionString("Default");
        _opts = opts;
        _log = log;
    }

    // ====== DTOs ======
    public sealed class ForgotPasswordRequest { public string Email { get; set; } = ""; }
    public sealed class ResetPasswordRequest  { public string Token { get; set; } = ""; public string NewPassword { get; set; } = ""; }
    public sealed class DeleteAccountRequest  { public string? Password { get; set; } } // optional for social-only users
    
    public sealed class RegisterRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public sealed class LoginRequest    { public string Email { get; set; } = ""; public string Password { get; set; } = ""; public string? TotpCode { get; set; } }
    public sealed class LoginResponse   { public string? Token { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public UserDto? User { get; set; } public bool MfaRequired { get; set; } public string? Ticket { get; set; } }
    public sealed class UserDto
    {
        public ulong   Id           { get; set; }
        public string  Email        { get; set; } = "";
        public string? DisplayName  { get; set; }
        public string? AvatarUrl    { get; set; }   // add
        public string? Locale       { get; set; }   // add
        public string? Timezone     { get; set; }   // add
        public string[]? Roles      { get; set; }
    }
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
    
    public sealed class GoogleLoginRequest
    {
        public string IdToken { get; set; } = "";
        public bool AllowCreate { get; set; } = true; // default keeps current behavior
    }
    // ====== Password reset ======
    public sealed class ResetRequest
    {
        public string Token { get; set; } = "";
        public string NewPassword { get; set; } = "";
    }

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
    
        var provider = "google";
        var subject  = payload.Subject;
        var email    = payload.Email ?? "";
    
        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
    
        // 1) Find identity → user
        var userId = await conn.QueryFirstOrDefaultAsync<ulong?>(
            "SELECT user_id FROM user_identities WHERE provider=@provider AND subject=@subject",
            new { provider, subject });
    
        if (userId is null)
        {
            // Try match by email
            userId = await conn.QueryFirstOrDefaultAsync<ulong?>(
                @"SELECT ua.user_id FROM user_auth ua
                  WHERE ua.email_norm = LOWER(@email) LIMIT 1",
                new { email });
    
            if (userId is null)
            {
                if (!req.AllowCreate)
                {
                    return NotFound("Account not found for this Google identity.");
                }
                // Create user + auth/profile (use UUID_TO_BIN to match your schema)
                await conn.ExecuteAsync("INSERT INTO users (uuid) VALUES (UUID_TO_BIN(UUID()));");
                userId = await conn.ExecuteScalarAsync<ulong>("SELECT LAST_INSERT_ID();");
    
                await conn.ExecuteAsync(
                    @"INSERT INTO user_auth (user_id, email, password_hash, email_verified_at)
                      VALUES (@uid, @em, 0x00, UTC_TIMESTAMP())",
                    new { uid = userId, em = email });
    
                await conn.ExecuteAsync(
                    @"INSERT INTO user_profile (user_id, display_name, avatar_url, locale)
                      VALUES (@uid, @name, @pic, @loc)",
                    new { uid = userId, name = payload.Name, pic = payload.Picture, loc = "en" });
            }
    
            await conn.ExecuteAsync(
                @"INSERT INTO user_identities (user_id, provider, subject, email)
                  VALUES (@uid, @provider, @subject, @em)
                  ON DUPLICATE KEY UPDATE email=VALUES(email)",
                new { uid = userId, provider, subject, em = email });
        }
    
        // 2) Create a normal session and return it
        var sess = await CreateSessionAsync(userId.Value, ct);
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

    //Forgot password
    [HttpPost("forgot")]
    [AllowAnonymous]
    public async Task<IActionResult> Forgot([FromBody] ForgotPasswordRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
    
        var email = (req.Email ?? "").Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
            return NoContent(); // do not reveal
    
        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
    
        var userId = await conn.ExecuteScalarAsync<ulong?>(@"
            SELECT ua.user_id
            FROM user_auth ua
            WHERE ua.email_norm = LOWER(TRIM(@em)) OR ua.email = @em
            LIMIT 1;", new { em = email });
    
        // always behave the same
        if (userId is null) return NoContent();
    
        var (token, tokenHash) = MakeToken();                         // SHA256(UTF8(tokenHex))  :contentReference[oaicite:0]{index=0}
        var expiresAt = DateTime.UtcNow.AddMinutes(60);
    
        // optional: ensure single active reset per user
        await conn.ExecuteAsync(
            "DELETE FROM password_resets WHERE user_id=@uid AND used_at IS NULL;",
            new { uid = userId.Value });
    
        await conn.ExecuteAsync(@"
            INSERT INTO password_resets
                   (id, token_hash, user_id, created_at, expires_at, used_at, ip_address, user_agent)
            VALUES (@id, @id,        @uid,  CURRENT_TIMESTAMP(3), @exp, NULL, @ip,       @ua);",
            new {
                id  = tokenHash,
                uid = userId.Value,
                exp = expiresAt,
                ip  = GetClientIpBinary(HttpContext),
                ua  = Request.Headers.UserAgent.ToString() is var ua && ua.Length > 255 ? ua[..255] : ua
            });
    
    #if DEBUG
        _log.LogInformation("Password reset token for {Email}: {Token}", email, token);
        return Ok(new { devToken = token, expiresAt });               // your current behavior  :contentReference[oaicite:1]{index=1}
    #else
        return NoContent(); // email the link in production
    #endif
    }

    [HttpPost("reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest("Missing body.");
        if (string.IsNullOrWhiteSpace(req.Token) || req.Token.Length != 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(req.Token, "^[0-9a-fA-F]{64}$"))
            return BadRequest("Invalid token.");
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("Password too short.");
    
        var id = SHA256.HashData(Encoding.UTF8.GetBytes(req.Token));
    
        try
        {
            await using var conn = new MySqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
    
            var userId = await conn.QuerySingleOrDefaultAsync<ulong?>(@"
                SELECT user_id
                FROM password_resets
                WHERE id = @id
                  AND used_at IS NULL
                  AND expires_at > UTC_TIMESTAMP(3)
                FOR UPDATE
                LIMIT 1;", new { id }, tx);
    
            if (userId is null)
            {
                await tx.RollbackAsync(ct);
                return NotFound("Reset link is invalid, expired, or already used.");
            }
    
            var hashString = BCrypt.Net.BCrypt.HashPassword(req.NewPassword); // ~60 chars
            var hashBytes  = Encoding.UTF8.GetBytes(hashString);
    
            await conn.ExecuteAsync(@"
                UPDATE user_auth
                   SET password_hash = @hash,
                       updated_at    = UTC_TIMESTAMP(3)
                 WHERE user_id = @uid;",
                new { hash = hashBytes, uid = userId.Value }, tx);
    
            var uaRaw = Request.Headers.UserAgent.ToString();
            var ua255 = uaRaw.Length > 255 ? uaRaw[..255] : uaRaw;
            var ip    = GetClientIpBinary(HttpContext);
    
            await conn.ExecuteAsync(@"
                UPDATE password_resets
                   SET used_at    = UTC_TIMESTAMP(3),
                       ip_address = @ip,
                       user_agent = @ua
                 WHERE id = @id;",
                new { id, ip, ua = ua255 }, tx);
    
            await conn.ExecuteAsync(@"DELETE FROM sessions WHERE user_id=@uid;",
                new { uid = userId.Value }, tx);
    
            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            // DEBUG: surface the exact cause so we can fix it quickly
            return StatusCode(500, ex.Message);
        }
    }


    [HttpGet("reset/validate")]
    [AllowAnonymous]
    public async Task<IActionResult> ValidateReset([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
    
        // must be a 64-hex token
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64) return NotFound();
    
        byte[] id;
        try { id = SHA256.HashData(Encoding.UTF8.GetBytes(token)); }
        catch { return NotFound(); }
    
        await using var conn = new MySqlConnection(_connString);
        var row = await conn.QuerySingleOrDefaultAsync<(DateTime ExpiresAt, DateTime? UsedAt)?>(@"
            SELECT expires_at, used_at
            FROM password_resets
            WHERE id = @id
            LIMIT 1;", new { id });
    
        if (row is null || row.Value.UsedAt is not null || row.Value.ExpiresAt <= DateTime.UtcNow)
            return NotFound();
    
        return NoContent(); // 204 = valid
    }
    /*
    [HttpPost("reset")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetRequest req, CancellationToken ct)
    {
        Console.WriteLine("ResetPassword: entered");
    
        if (req is null)
            return BadRequest("Missing body.");
    
        // 1) Basic validation
        if (string.IsNullOrWhiteSpace(req.Token) ||
            req.Token.Length != 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(req.Token, "^[0-9a-fA-F]{64}$"))
            return BadRequest("Invalid token.");
    
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("Password too short.");
    
        // 2) Derive the binary id we store (UNHEX(SHA2(token,256)))
        //var tokenHex = req.Token.ToLowerInvariant();
        //var tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenHex); // hash the string as we inserted it
        //var id = System.Security.Cryptography.SHA256.HashData(tokenBytes); // 32 bytes
        var id = SHA256.HashData(Encoding.UTF8.GetBytes(req.Token)); // 32 bytes
    
        await using var conn = new MySqlConnector.MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
    
        // 3) Look up the reset row: must exist, not used, not expired
        var row = await conn.QuerySingleOrDefaultAsync(
            new CommandDefinition(@"
                SELECT user_id
                FROM password_resets
                WHERE id = @id
                  AND used_at IS NULL
                  AND expires_at > UTC_TIMESTAMP(3)
                LIMIT 1;", new { id }, transaction: tx, cancellationToken: ct));
    
        if (row is null)
        {
            // Unknown/expired/used token
            await tx.RollbackAsync(ct);
            return NotFound(); // or Gone(410) if you prefer
        }
    
        long userId = (long)row.user_id;
        /*
        // 4) Hash the new password (BCrypt)
        var newHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
    
        // 5) Update user password + mark token used
        await conn.ExecuteAsync(
            new CommandDefinition(@"
                UPDATE user_auth
                   SET password_hash = @hash
                 WHERE user_id = @uid;", new { hash = newHash, uid = userId }, tx, cancellationToken: ct));
        //
        // 4) Hash the new password (BCrypt)
        var newHashString = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        var newHashBytes  = Encoding.UTF8.GetBytes(newHashString);
        
        // 5) Update user password + mark token used
        await conn.ExecuteAsync(
            new CommandDefinition(@"
                UPDATE user_auth
                   SET password_hash = @hash
                 WHERE user_id = @uid;",
                new { hash = newHashBytes, uid = userId }, tx, cancellationToken: ct));

        // capture context info (optional)
        byte[]? ipBytes = null;
        if (HttpContext.Connection.RemoteIpAddress is not null)
            ipBytes = HttpContext.Connection.RemoteIpAddress.MapToIPv6().GetAddressBytes();
        var ua = HttpContext.Request.Headers["User-Agent"].ToString();
    
        await conn.ExecuteAsync(
            new CommandDefinition(@"
                UPDATE password_resets
                   SET used_at = UTC_TIMESTAMP(3),
                       ip_address = @ip,
                       user_agent = @ua
                 WHERE id = @id;", new { id, ip = ipBytes, ua }, tx, cancellationToken: ct));
    
        await tx.CommitAsync(ct);
    
        Console.WriteLine("ResetPassword: success -> 204");
        return NoContent();
    }
    */


    /*
    [HttpPost("reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
    
        if (string.IsNullOrWhiteSpace(req.Token) || req.Token.Length != 64)
            return BadRequest("Invalid token.");
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("Password must be at least 8 characters.");
    
        byte[] tokenHash;
        try { tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(req.Token)); }
        catch { return BadRequest("Invalid token format."); }
    
        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
    
        // 1) Load and validate the reset row
        var r = await conn.QuerySingleOrDefaultAsync<(ulong UserId, DateTime ExpiresAt, DateTime? UsedAt)?>(@"
            SELECT user_id, expires_at, used_at
            FROM password_resets
            WHERE id = @id
            FOR UPDATE
            LIMIT 1;", new { id = tokenHash }, tx);
    
        if (r is null || r.Value.UsedAt is not null || r.Value.ExpiresAt <= DateTime.UtcNow)
            return NotFound();
    
        // 2) Update password hash for that user (BCrypt hash stored as bytes like your login code)
        var hashStr   = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, workFactor: 12);
        var hashBytes = System.Text.Encoding.UTF8.GetBytes(hashStr);
    
        await conn.ExecuteAsync(@"
            UPDATE user_auth
            SET password_hash = @hash
            WHERE user_id = @uid;", new { uid = r.Value.UserId, hash = hashBytes }, tx);
    
        // 3) Mark token as used and purge any other outstanding reset tokens for that user
        await conn.ExecuteAsync(@"
            UPDATE password_resets
            SET used_at = UTC_TIMESTAMP(3)
            WHERE id = @id;", new { id = tokenHash }, tx);
    
        await conn.ExecuteAsync(@"
            DELETE FROM password_resets
            WHERE user_id = @uid AND id <> @id;", new { uid = r.Value.UserId, id = tokenHash }, tx);
    
        await tx.CommitAsync(ct);
    
        return NoContent();
    }
    */
    
    // ====== DELETE ACCOUNT ======
    [HttpDelete("account")]
    [Authorize]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var uid)) return Unauthorized(); // :contentReference[oaicite:7]{index=7}
    
        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
    
        // If user has a password, verify if provided (social-only accounts may have 0x00 or empty hash)
        var hash = await conn.ExecuteScalarAsync<string?>(@"
            SELECT CAST(password_hash AS CHAR(100) CHARACTER SET utf8mb4)
            FROM user_auth WHERE user_id = @uid LIMIT 1;",
            new { uid }, tx);
        
        if (!string.IsNullOrWhiteSpace(hash))
        {
            // password present -> require match
            if (string.IsNullOrWhiteSpace(req.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, hash))
            {
                await tx.RollbackAsync(ct);
                return Unauthorized(new { error = "Password required to delete account." });
            }
        }

        // Delete child rows first to satisfy FKs; mirrors tables you already use (sessions, identities, roles, profile, auth)
        await conn.ExecuteAsync("DELETE FROM sessions         WHERE user_id = @uid;", new { uid }, tx);
        await conn.ExecuteAsync("DELETE FROM user_identities  WHERE user_id = @uid;", new { uid }, tx);
        await conn.ExecuteAsync("DELETE FROM user_roles       WHERE user_id = @uid;", new { uid }, tx);
        await conn.ExecuteAsync("DELETE FROM user_profile     WHERE user_id = @uid;", new { uid }, tx);
        await conn.ExecuteAsync("DELETE FROM user_auth        WHERE user_id = @uid;", new { uid }, tx);
        await conn.ExecuteAsync("DELETE FROM password_resets  WHERE user_id = @uid;", new { uid }, tx);
    
        // Finally the user row
        await conn.ExecuteAsync("DELETE FROM users WHERE id = @uid;", new { uid }, tx);
    
        await tx.CommitAsync(ct);
    
        // best-effort: also remove current bearer session if sent
        var tok = GetBearerToken(Request);
        if (!string.IsNullOrEmpty(tok))
        {
            var (_, h) = MakeToken(tok); // hash existing token  :contentReference[oaicite:8]{index=8}
            await conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id;", new { id = h });
        }
    
        return NoContent();
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
            /*
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
            */
            // Fetch user + hash as string
            var auth = await conn.QuerySingleOrDefaultAsync<UserAuthRow>(@"
                SELECT
                    ua.user_id AS UserId,
                    ua.email   AS Email,
                    CAST(ua.password_hash AS CHAR(100) CHARACTER SET utf8mb4) AS PasswordHash
                FROM user_auth ua
                JOIN users u ON u.id = ua.user_id
                WHERE ua.email_norm = LOWER(TRIM(@email)) OR ua.email = @email
                LIMIT 1;",
                new { email = req.Email });
            
            if (auth is null || string.IsNullOrWhiteSpace(auth.PasswordHash))
                return Unauthorized(new { error = "Invalid email or password." }); // also covers social-only accounts
            
            // Verify BCrypt
            if (!BCrypt.Net.BCrypt.Verify(req.Password, auth.PasswordHash))
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
        await conn.OpenAsync(ct); // <-- IMPORTANT
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
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id;", new { id = hash });

        return NoContent();
    }

    // ====== helpers ======
    // ---------- helpers ----------
    private async Task<(string Token, DateTimeOffset ExpiresAt)> CreateSessionAsync(ulong userId, CancellationToken ct)
    {
        var (token, tokenHash) = MakeToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
    
        await using var conn = new MySqlConnection(_connString);
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
            });
    
        return (token, expiresAt);
    }

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
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); // keep as-is (uppercase is fine)
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return (token, hash);
    }
    private static (string token, byte[] hash) MakeToken(string existingToken)
        => (existingToken, SHA256.HashData(Encoding.UTF8.GetBytes(existingToken)));

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
    // In AuthController (inside the class)
    private bool TryGetUserId(out ulong userId)
    {
        userId = 0;
    
        // 1) Prefer claims (works with NameIdentifier/uid/sub)
        var claim =
            User?.FindFirst("uid")?.Value ??
            User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            User?.FindFirst("sub")?.Value;
    
        if (!string.IsNullOrEmpty(claim) && ulong.TryParse(claim, out userId))
            return true;
    
        // 2) Then HttpContext.Items: support both snake_case and camelCase
        if (HttpContext?.Items.TryGetValue("user_id", out var v) == true ||
            HttpContext?.Items.TryGetValue("userId",  out v) == true) // <— added
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
                   up.avatar_url   AS AvatarUrl,
                   up.locale       AS Locale,
                   up.timezone     AS Timezone
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

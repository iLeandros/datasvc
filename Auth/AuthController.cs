using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Auth;

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly string _connString;
    public AuthController(IConfiguration cfg) => _connString = cfg.GetConnectionString("Default")!;
    private sealed class UserAuthRow
    {
        public ulong UserId { get; set; }
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = ""; // <-- string, not byte[]
    }

    // POST /v1/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Unauthorized(new { error = "Invalid email or password." });

        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);

        /*
        // 1) Load auth row (join to users just to get id & sanity)
        var auth = await conn.QuerySingleOrDefaultAsync<UserAuthRow>(@"
            SELECT ua.user_id AS UserId, ua.email AS Email, ua.password_hash AS PasswordHash
            FROM user_auth ua
            JOIN users u ON u.id = ua.user_id
            WHERE ua.email_norm = LOWER(TRIM(@email))
            LIMIT 1;", new { email = req.Email });
            // Query (force string)
        */
        var auth = await conn.QuerySingleOrDefaultAsync<UserAuthRow>(@"
            SELECT
            ua.user_id  AS UserId,
            ua.email    AS Email,
            CAST(ua.password_hash AS CHAR(100) CHARACTER SET utf8mb4) AS PasswordHash
            FROM user_auth ua
            JOIN users u ON u.id = ua.user_id
            WHERE ua.email_norm = LOWER(TRIM(@email))
            LIMIT 1;", new { email = req.Email });

        if (auth is null) return Unauthorized(new { error = "Invalid email or password." });

        // 2) Verify password (BCrypt example; replace with Argon2 if you prefer)
        // If you stored raw hash bytes, they may be base64/hex encoded; adjust accordingly.
        //var ok = BCrypt.Net.BCrypt.Verify(req.Password, Encoding.UTF8.GetString(auth.PasswordHash));
        // Verify
        var ok = BCrypt.Net.BCrypt.Verify(req.Password, auth.PasswordHash);
        if (!ok) return Unauthorized(new { error = "Invalid email or password." });

        // (Optional) handle MFA here; if enabled, return mfaRequired + ticket
        // For now we return straight session:
        var (token, tokenHash) = MakeToken();

        var expiresAt = DateTimeOffset.UtcNow.AddDays(30); // adjust to your policy

        // 3) Persist session (SHA-256(token) -> BINARY(32))
        var rows = await conn.ExecuteAsync(@"
            INSERT INTO sessions (id, user_id, created_at, expires_at, ip_address, user_agent)
            VALUES (@id, @uid, CURRENT_TIMESTAMP(3), @exp, @ip, @ua);",
            new
            {
                id = tokenHash, // byte[32]
                uid = auth.UserId,
                exp = expiresAt.UtcDateTime,
                ip = GetClientIpBinary(HttpContext),
                ua = Request.Headers.UserAgent.ToString().Truncate(255)
            });

        if (rows != 1) return StatusCode(500, new { error = "Failed to create session." });

        var user = await LoadUserDto(conn, auth.UserId, ct);

        return Ok(new LoginResponse
        {
            Token = token,                 // raw token to client
            ExpiresAt = expiresAt,
            User = user,
            MfaRequired = false
        });
    }

    // GET /v1/auth/me
    [HttpGet("me")]
    [Authorize] // We’ll add a custom auth handler below; you can also do manual checks
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = HttpContext.Items["userId"] as ulong?;
        if (userId is null) return Unauthorized();

        await using var conn = new MySqlConnection(_connString);
        var user = await LoadUserDto(conn, userId.Value, ct);
        return Ok(user);
    }

    // POST /v1/auth/logout
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = GetBearerToken(Request);
        if (string.IsNullOrEmpty(token)) return NoContent();

        var (_, hash) = MakeToken(token); // hash existing token
        await using var conn = new MySqlConnection(_connString);
        await conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id;", new { id = hash });

        return NoContent();
    }

    // ------------- helpers -------------

    private static (string token, byte[] hash) MakeToken(string? existing = null)
    {
        string token = existing ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Convert.FromHexString(token));
        return (token, hash);
    }

    private static string? GetBearerToken(HttpRequest req)
        => req.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
           ? req.Headers.Authorization.ToString().Substring("Bearer ".Length).Trim()
           : null;

    private static byte[]? GetClientIpBinary(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null ? null : ip.MapToIPv6().GetAddressBytes();
    }

    private static async Task<UserDto> LoadUserDto(IDbConnection conn, ulong userId, CancellationToken ct)
    {
        var user = await conn.QuerySingleAsync<UserDto>(@"
            SELECT u.id AS Id, ua.email AS Email, up.display_name AS DisplayName
            FROM users u
            LEFT JOIN user_auth ua ON ua.user_id = u.id
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

    // DTOs (match the MAUI client)
    public sealed class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; public string? TotpCode { get; set; } }
    public sealed class LoginResponse { public string? Token { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public UserDto? User { get; set; } public bool MfaRequired { get; set; } public string? Ticket { get; set; } }
    public sealed class UserDto { public ulong Id { get; set; } public string Email { get; set; } = ""; public string? DisplayName { get; set; } public string[]? Roles { get; set; } }
    private sealed class UserAuthRow { public ulong UserId { get; set; } public string Email { get; set; } = ""; public byte[] PasswordHash { get; set; } = Array.Empty<byte>(); }
}

static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}

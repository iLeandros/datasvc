using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Auth;

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly string? _connString;
    public AuthController(IConfiguration cfg) => _connString = cfg.GetConnectionString("Default");

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
                WHERE ua.email_norm = LOWER(TRIM(@email))
                LIMIT 1;", new { email = req.Email });

            if (auth is null || string.IsNullOrWhiteSpace(auth.PasswordHash))
                return Unauthorized(new { error = "Invalid email or password." });

            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(req.Password, auth.PasswordHash); }
            catch { return Unauthorized(new { error = "Invalid email or password." }); }

            if (!ok) return Unauthorized(new { error = "Invalid email or password." });

            // Make a session
            var (token, tokenHash) = MakeToken();
            var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

            await conn.ExecuteAsync(@"
                INSERT INTO sessions (id, user_id, created_at, expires_at, ip_address, user_agent)
                VALUES (@id, @uid, CURRENT_TIMESTAMP(3), @exp, @ip, @ua);",
                new
                {
                    id = tokenHash, // SHA-256(token)
                    uid = auth.UserId,
                    exp = expiresAt.UtcDateTime,
                    ip = GetClientIpBinary(HttpContext),
                    ua = Request.Headers.UserAgent.ToString() is var ua && ua.Length > 255 ? ua[..255] : ua
                });

            var user = await LoadUserDto(conn, auth.UserId, ct);

            return Ok(new LoginResponse
            {
                Token = token,
                ExpiresAt = expiresAt,
                User = user,
                MfaRequired = false
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return Problem("Login failed: " + ex.Message);
        }
    }

    // ----- helpers -----
    private static (string token, byte[] hash) MakeToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = SHA256.HashData(Convert.FromHexString(token));
        return (token, hash);
    }

    private static byte[]? GetClientIpBinary(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.MapToIPv6().GetAddressBytes();

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

    // DTOs
    public sealed class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; public string? TotpCode { get; set; } }
    public sealed class LoginResponse { public string? Token { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public UserDto? User { get; set; } public bool MfaRequired { get; set; } public string? Ticket { get; set; } }
    public sealed class UserDto { public ulong Id { get; set; } public string Email { get; set; } = ""; public string? DisplayName { get; set; } public string[]? Roles { get; set; } }
    private sealed class UserAuthRow { public ulong UserId { get; set; } public string Email { get; set; } = ""; public string PasswordHash { get; set; } = ""; }
}

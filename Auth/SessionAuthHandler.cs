// Auth/SessionAuthHandler.cs
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace DataSvc.Auth;

public class SessionAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly string _connString;
    public const string Scheme = "SessionBearer";

    // .NET 8 signature — ISystemClock is removed (deprecated since .NET 7)
    public SessionAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration cfg)
        : base(options, logger, encoder)
        => _connString = cfg.GetConnectionString("Default")!;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) ||
            !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var tokenHex = auth["Bearer ".Length..].Trim();
        if (tokenHex.Length != 64)
            return AuthenticateResult.Fail("Bad token.");

        // Hash the UTF-8 bytes of the hex string — matches MakeToken() in AuthController
        byte[] tokenHash;
        try { tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(tokenHex)); }
        catch { return AuthenticateResult.Fail("Bad token format."); }

        await using var conn = new MySqlConnection(_connString);
        var row = await conn.QuerySingleOrDefaultAsync<(ulong UserId, DateTime ExpiresAt)?>(@"
            SELECT user_id, expires_at
            FROM sessions
            WHERE id = @id
            LIMIT 1;", new { id = tokenHash });

        if (row is null || row.Value.ExpiresAt <= DateTime.UtcNow)
            return AuthenticateResult.Fail("Expired or unknown session.");

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, row.Value.UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme);

        // Store under "userId" — matches UserIdentity.TryGetUserId
        Context.Items["userId"] = row.Value.UserId;
        return AuthenticateResult.Success(ticket);
    }
}

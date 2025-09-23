using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Text.Encodings.Web;

namespace DataSvc.Auth;

public class SessionAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly string _connString;
    public const string Scheme = "SessionBearer";

    public SessionAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, IConfiguration cfg)
        : base(options, logger, encoder, clock) => _connString = cfg.GetConnectionString("Default")!;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        /*
        var tokenHex = auth.Substring("Bearer ".Length).Trim();
        if (tokenHex.Length != 64) return AuthenticateResult.Fail("Bad token.");

        byte[] tokenHash;
        tokenHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenHex));
        try { tokenHash = SHA256.HashData(Convert.FromHexString(tokenHex)); }
        catch { return AuthenticateResult.Fail("Bad token format."); }

        await using var conn = new MySqlConnection(_connString);
        var row = await conn.QuerySingleOrDefaultAsync<(ulong UserId, DateTime ExpiresAt)?>(@"
            SELECT user_id, expires_at
            FROM sessions
            WHERE id = @id
            LIMIT 1;", new { id = tokenHash });
        */
        var tokenHex = auth.Substring("Bearer ".Length).Trim();
        if (tokenHex.Length != 64) return AuthenticateResult.Fail("Bad token.");
        
        byte[] tokenHash;
        // IMPORTANT: hash the UTF-8 of the hex string to match MakeToken()
        try { tokenHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenHex)); }
        catch { return AuthenticateResult.Fail("Bad token format."); }
        
        await using var conn = new MySqlConnector.MySqlConnection(_connString);
        var row = await conn.QuerySingleOrDefaultAsync<(ulong UserId, DateTime ExpiresAt)?>(@"
            SELECT user_id, expires_at
            FROM sessions
            WHERE id = @id
            LIMIT 1;", new { id = tokenHash });
    
        if (row is null || row.Value.ExpiresAt <= DateTime.UtcNow)
            return AuthenticateResult.Fail("Expired or unknown session.");

        // success
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, row.Value.UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme);

        // expose userId for controllers
        Context.Items["userId"] = row.Value.UserId;
        return AuthenticateResult.Success(ticket);
    }
}

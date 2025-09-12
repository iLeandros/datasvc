using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace DataSvc.Auth;

public sealed class SessionAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly string _connString;
    public new const string Scheme = "SessionBearer";

    public SessionAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TimeProvider timeProvider,
        IConfiguration cfg)
        : base(options, logger, encoder, timeProvider)
    {
        _connString = cfg.GetConnectionString("Default") 
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var tokenHex = authHeader["Bearer ".Length..].Trim();
        if (tokenHex.Length != 64) return AuthenticateResult.Fail("Bad token.");

        byte[] tokenHash;
        try { tokenHash = SHA256.HashData(Convert.FromHexString(tokenHex)); }
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

        Context.Items["userId"] = row.Value.UserId;
        return AuthenticateResult.Success(ticket);
    }
}

using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Legal;

[ApiController]
[Route("v1/legal")]
public class LegalController : ControllerBase
{
    private readonly string _conn;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _cfg;

    public LegalController(IConfiguration cfg, IWebHostEnvironment env)
    {
        _cfg = cfg;
        _env = env;
        _conn = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
    }

    // ------------------------
    // New: GET /v1/legal/latest
    // ------------------------
    public sealed class LatestDoc
    {
        public string DocKey { get; set; } = "";
        public int Version { get; set; }
        public string? Url { get; set; }
    }

    [HttpGet("latest")]
    [AllowAnonymous]
    public IActionResult Latest()
    {
        var list = new[]
        {
            new LatestDoc
            {
                DocKey = "terms",
                Version = _cfg.GetValue<int>("Legal:terms:Version", 1),
                Url     = _cfg.GetValue<string>("Legal:terms:Url", null)
            },
            new LatestDoc
            {
                DocKey = "privacy",
                Version = _cfg.GetValue<int>("Legal:privacy:Version", 1),
                Url     = _cfg.GetValue<string>("Legal:privacy:Url", null)
            }
        };
        return Ok(list);
    }

    // ------------------------
    // New: GET /v1/legal/status (auth)
    // ------------------------
    public sealed class StatusDoc
    {
        public string DocKey { get; set; } = "";
        public int RequiredVersion { get; set; }
        public int? AcceptedVersion { get; set; }
    }

    [HttpGet("status")]
    [Authorize]
    public async Task<IActionResult> Status()
    {
        // Resolve your user id (matches what your auth handler sets)
        var userId = HttpContext.Items.TryGetValue("user_id", out var v) ? (ulong)v! : 0UL;
        if (userId == 0) return Unauthorized();

        var required = new[]
        {
            new { DocKey = "terms",   Version = _cfg.GetValue<int>("Legal:terms:Version", 1) },
            new { DocKey = "privacy", Version = _cfg.GetValue<int>("Legal:privacy:Version", 1) }
        };

        await using var c = new MySqlConnection(_conn);
        var acceptedPairs = await c.QueryAsync<(string DocKey, int Version)>(@"
            SELECT doc_key, MAX(version) AS Version
            FROM user_legal_acceptances
            WHERE user_id = @uid
            GROUP BY doc_key;", new { uid = userId });

        var accepted = acceptedPairs.ToDictionary(x => x.DocKey, x => (int?)x.Version, StringComparer.OrdinalIgnoreCase);

        var result = required.Select(r => new StatusDoc
        {
            DocKey = r.DocKey,
            RequiredVersion = r.Version,
            AcceptedVersion = accepted.TryGetValue(r.DocKey, out var v2) ? v2 : null
        });

        return Ok(result);
    }

    // ------------------------
    // Existing: GET /v1/legal/terms  (?lang=en)
    // ------------------------
    [HttpGet("terms")]
    [AllowAnonymous]
    public IActionResult GetTerms([FromQuery] string lang = "en")
        => Ok(ReadDoc("terms", lang));

    // ------------------------
    // Existing: GET /v1/legal/privacy (?lang=en)
    // ------------------------
    [HttpGet("privacy")]
    [AllowAnonymous]
    public IActionResult GetPrivacy([FromQuery] string lang = "en")
        => Ok(ReadDoc("privacy", lang));

    // ------------------------
    // Existing: POST /v1/legal/accept { docKey, version }
    // ------------------------
    [HttpPost("accept")]
    [Authorize]
    public async Task<IActionResult> Accept([FromBody] AcceptRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DocKey))
            return BadRequest(new { error = "docKey required" });

        var userId = HttpContext.Items.TryGetValue("user_id", out var v) ? (ulong)v! : 0UL;
        if (userId == 0) return Unauthorized();

        await using var c = new MySqlConnection(_conn);
        await c.ExecuteAsync(@"
            INSERT IGNORE INTO user_legal_acceptances
                (user_id, doc_key, version, accepted_at, ip_address, app_version)
            VALUES
                (@uid, @k, @v, CURRENT_TIMESTAMP(3), @ip, @ua);",
            new
            {
                uid = userId,
                k   = req.DocKey,
                v   = req.Version,
                ip  = HttpContext.Connection.RemoteIpAddress?.MapToIPv6().GetAddressBytes(),
                ua  = Request.Headers.UserAgent.ToString()
            });

        return Ok(new { ok = true });
    }

    // ---- helpers ----
    private LegalDocResponse ReadDoc(string docKey, string lang)
    {
        // paths & versions configurable, with sensible defaults
        var section = _cfg.GetSection($"Legal:{docKey}");
        var version = section.GetValue<int>("Version", 1);
        var fileRel = section.GetValue<string>("Path", $"Legal/{docKey}_{lang}.html");
        var full = Path.Combine(_env.ContentRootPath, fileRel);

        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException($"Legal doc not found: {fileRel}");

        var html = System.IO.File.ReadAllText(full);
        return new LegalDocResponse
        {
            DocKey = docKey,
            Version = version,
            ContentType = "text/html",
            Content = html
        };
    }

    public sealed class AcceptRequest { public string DocKey { get; set; } = ""; public int Version { get; set; } }
    public sealed class LegalDocResponse { public string DocKey { get; set; } = ""; public int Version { get; set; } public string ContentType { get; set; } = "text/html"; public string Content { get; set; } = ""; }
}

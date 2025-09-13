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

    // GET /v1/legal/terms   (optionally ?lang=en)
    [HttpGet("terms")]
    [AllowAnonymous]
    public IActionResult GetTerms([FromQuery] string lang = "en")
        => Ok(ReadDoc("terms", lang));

    // GET /v1/legal/privacy (optionally ?lang=en)
    [HttpGet("privacy")]
    [AllowAnonymous]
    public IActionResult GetPrivacy([FromQuery] string lang = "en")
        => Ok(ReadDoc("privacy", lang));

    // POST /v1/legal/accept { docKey: "terms" | "privacy", version: 1 }
    [HttpPost("accept")]
    [Authorize]
    public async Task<IActionResult> Accept([FromBody] AcceptRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DocKey)) return BadRequest(new { error = "docKey required" });

        var userId = HttpContext.Items.TryGetValue("user_id", out var v) ? (ulong)v! : 0UL; // or your own helper
        if (userId == 0) return Unauthorized();

        await using var c = new MySqlConnection(_conn);
        await c.ExecuteAsync(@"
            INSERT IGNORE INTO user_legal_acceptances
                (user_id, doc_key, version, ip_address, app_version)
            VALUES
                (@uid, @k, @v, @ip, @ua);",
            new {
                uid = userId,
                k = req.DocKey,
                v = req.Version,
                ip = HttpContext.Connection.RemoteIpAddress?.MapToIPv6().GetAddressBytes(),
                ua = Request.Headers.UserAgent.ToString()
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

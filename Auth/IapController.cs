// IapController.cs
using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace DataSvc.Iap;

[ApiController]
[Route("v1/iap")]
public sealed class IapController : ControllerBase
{
    private readonly string? _connString;
    public IapController(IConfiguration cfg)
    {
        _connString = cfg.GetConnectionString("Default");
    }

    // --- DTOs ---
    public sealed class VerifyReq
    {
        public string ProductId { get; set; } = "";
        public string PurchaseToken { get; set; } = "";
        public string? OrderId { get; set; }
    }
    public sealed class EntitlementDto
    {
        public ulong   User_Id       { get; set; }
        public string  Feature       { get; set; } = "";
        public string  Source_Platform { get; set; } = "";
        public string  Product_Id    { get; set; } = "";
        public DateTime Starts_At    { get; set; }
        public DateTime? Expires_At  { get; set; }
        public string  Status        { get; set; } = "";
    }

    // You already do this in AuthController: copy the same logic to read the uid
    // (claims first, then HttpContext.Items["user_id"]). :contentReference[oaicite:1]{index=1}
    private bool TryGetUserId(out ulong userId)
    {
        userId = 0;
        var claim =
            User?.FindFirst("uid")?.Value ??
            User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
            User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(claim) && ulong.TryParse(claim, out userId))
            return true;

        if (HttpContext?.Items.TryGetValue("user_id", out var v) == true ||
            HttpContext?.Items.TryGetValue("userId", out v) == true)
        {
            if (v is ulong ul) { userId = ul; return true; }
            if (ulong.TryParse(v?.ToString(), out userId)) return true;
        }
        return false;
    }

    // POST /v1/iap/google/verify-consumable
    [HttpPost("google/verify-consumable")]
    [Authorize]
    public async Task<IActionResult> VerifyConsumable([FromBody] VerifyReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ProductId) || string.IsNullOrWhiteSpace(req.PurchaseToken))
            return BadRequest("Missing productId or purchaseToken.");

        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 0) Verify with Google first (purchases.products.get) — call your adapter here.
            // var gp = await GooglePlay.VerifyAsync(req.ProductId, req.PurchaseToken, ct);
            // if (!gp.IsPurchased) return BadRequest("Not purchased");

            // 1) Look up duration for this product
            var durationDays = await conn.ExecuteScalarAsync<int>(
                @"SELECT duration_days FROM products
                  WHERE platform='google' AND product_id=@pid LIMIT 1;",
                new { pid = req.ProductId }, tx);
            if (durationDays <= 0)
                return BadRequest("Unknown product.");

            // 2) Ledger insert/touch (idempotent on unique (platform, purchase_token))
            const string ledgerSql = @"
INSERT INTO purchases (
  user_id, platform, product_id, order_id, purchase_token, state, purchased_at, provider_payload
) VALUES (
  @userId, 'google', @productId, @orderId, @purchaseToken, 'purchased', UTC_TIMESTAMP(3), JSON_OBJECT('src','server')
)
AS new
ON DUPLICATE KEY UPDATE
  last_checked_at = UTC_TIMESTAMP(3),
  state = IF(purchases.state <> new.state, new.state, purchases.state);";

            await conn.ExecuteAsync(ledgerSql, new {
                userId, productId = req.ProductId, orderId = req.OrderId, purchaseToken = req.PurchaseToken
            }, tx);

            // 3) Entitlement stacking (NOW or existing expiry) + durationDays
            const string entSql = @"
INSERT INTO entitlements (
  user_id, feature, source_platform, product_id, starts_at, expires_at, status
)
VALUES (
  @userId, 'vip', 'google', @productId,
  UTC_TIMESTAMP(3),
  DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @days DAY),
  'active'
)
ON DUPLICATE KEY UPDATE
  expires_at = DATE_ADD(
    CASE
      WHEN entitlements.expires_at IS NULL OR entitlements.expires_at < UTC_TIMESTAMP(3)
        THEN UTC_TIMESTAMP(3)
      ELSE entitlements.expires_at
    END,
    INTERVAL @days DAY
  ),
  status = 'active',
  product_id = VALUES(product_id),
  source_platform = VALUES(source_platform);";

            await conn.ExecuteAsync(entSql, new { userId, productId = req.ProductId, days = durationDays }, tx);

            // 4) Consume (server-side) so user can buy again
            // await GooglePlay.ConsumeAsync(req.ProductId, req.PurchaseToken, ct);

            // 5) Return snapshot
            var ent = await conn.QuerySingleAsync<EntitlementDto>(@"
SELECT user_id   AS User_Id,
       feature   AS Feature,
       source_platform AS Source_Platform,
       product_id AS Product_Id,
       starts_at  AS Starts_At,
       expires_at AS Expires_At,
       status     AS Status
FROM entitlements
WHERE user_id = @userId AND feature = 'vip'
LIMIT 1;", new { userId }, tx);

            await tx.CommitAsync(ct);
            return Ok(ent);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // GET /v1/iap/entitlements/me  (client uses this on app start / restore)
    [HttpGet("entitlements/me")]
    [Authorize]
    public async Task<IActionResult> GetMyEntitlement(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        await using var conn = new MySqlConnection(_connString);
        var ent = await conn.QuerySingleOrDefaultAsync<EntitlementDto>(@"
SELECT user_id   AS User_Id,
       feature   AS Feature,
       source_platform AS Source_Platform,
       product_id AS Product_Id,
       starts_at  AS Starts_At,
       expires_at AS Expires_At,
       status     AS Status
FROM entitlements
WHERE user_id = @userId AND feature = 'vip'
LIMIT 1;", new { userId });

        if (ent is null) return NotFound(new { message = "No entitlement" });
        return Ok(ent);
    }
}

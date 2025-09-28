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
        public ulong     UserId         { get; set; }
        public string    Feature        { get; set; } = "";
        public string    SourcePlatform { get; set; } = "";
        public string    ProductId      { get; set; } = "";
        public DateTime  StartsAt       { get; set; }
        public DateTime? ExpiresAt      { get; set; }
        public string    Status         { get; set; } = "";
    }

    // --- DTO for the new route ---
    public sealed class RecordReq
    {
        public string Platform { get; set; } = "";     // "google" | "apple"
        public string ProductId { get; set; } = "";    // alias e.g. "discount.month"
        public string PurchaseToken { get; set; } = "";
        public string? OrderId { get; set; }
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
    /*
    [HttpPost("record-consumable")]
    [Authorize]
    public async Task<IActionResult> RecordConsumable([FromBody] RecordReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
    
        if (!TryGetUserId(out var userId))
            return Unauthorized();
    
        if (string.IsNullOrWhiteSpace(req.Platform) ||
            string.IsNullOrWhiteSpace(req.ProductId) ||
            string.IsNullOrWhiteSpace(req.PurchaseToken))
            return BadRequest("platform, productId, and purchaseToken are required.");
    
        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
    
        try
        {
            // 1) Lookup duration by (platform, alias)
            var days = await conn.ExecuteScalarAsync<int?>(@"
                SELECT duration_days FROM products
                WHERE platform = @platform AND product_id = @pid
                LIMIT 1;", new { platform = req.Platform, pid = req.ProductId }, tx) ?? 0;
    
            if (days <= 0)
                return BadRequest("Unknown product or non-positive duration.");
    
            // 2) Upsert into purchases as CONSUMED (idempotent on unique (platform, purchase_token))
            const string ledgerSql = @"
    INSERT INTO purchases (
      user_id, platform, product_id, order_id, purchase_token, state, purchased_at, acknowledged, last_checked_at, provider_payload
    ) VALUES (
      @userId, @platform, @alias, @orderId, @token, 'consumed', UTC_TIMESTAMP(3), 1, UTC_TIMESTAMP(3), JSON_OBJECT('source','client')
    )
    AS new
    ON DUPLICATE KEY UPDATE
      state = 'consumed',
      acknowledged = 1,
      last_checked_at = UTC_TIMESTAMP(3);";
    
            await conn.ExecuteAsync(ledgerSql, new {
                userId,
                platform = req.Platform,
                alias = req.ProductId,   // alias like "discount.month"
                orderId = req.OrderId,
                token = req.PurchaseToken
            }, tx);
    
            // 3) Stack entitlement for VIP
            const string entSql = @"
    INSERT INTO entitlements (user_id, feature, source_platform, product_id, starts_at, expires_at, status)
    VALUES (@userId, 'vip', @platform, @alias, UTC_TIMESTAMP(3), DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @days DAY), 'active')
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
    
            await conn.ExecuteAsync(entSql, new { userId, platform = req.Platform, alias = req.ProductId, days }, tx);
    
            // 4) Return snapshot
            var ent = await conn.QuerySingleAsync<EntitlementDto>(@"
    SELECT user_id       AS User_Id,
           feature       AS Feature,
           source_platform AS Source_Platform,
           product_id    AS Product_Id,
           starts_at     AS Starts_At,
           expires_at    AS Expires_At,
           status        AS Status
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
    */

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
                                                                    SELECT
                                                                      user_id         AS UserId,
                                                                      feature         AS Feature,
                                                                      source_platform AS SourcePlatform,
                                                                      product_id      AS ProductId,
                                                                      starts_at       AS StartsAt,
                                                                      expires_at      AS ExpiresAt,
                                                                      status          AS Status
                                                                    FROM entitlements
                                                                    WHERE user_id = @userId AND feature = 'vip'
                                                                    LIMIT 1;", 
                                                                    new { userId }, tx);

            await tx.CommitAsync(ct);
            return Ok(ent);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
    
    [HttpPost("google/mark-consumed")]
    [Authorize]
    public async Task<IActionResult> MarkConsumed([FromBody] dynamic body, CancellationToken ct)
    {
        string token = (string)body?.purchaseToken ?? "";
        if (string.IsNullOrWhiteSpace(token)) return BadRequest();
    
        await using var conn = new MySqlConnection(_connString);
        var rows = await conn.ExecuteAsync(@"
            UPDATE purchases
            SET acknowledged = 1,
                state = 'consumed',
                last_checked_at = UTC_TIMESTAMP(3)
            WHERE platform='google' AND purchase_token=@token;", new { token });
        return rows > 0 ? Ok() : NotFound();
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
                                                                        SELECT
                                                                          user_id         AS UserId,
                                                                          feature         AS Feature,
                                                                          source_platform AS SourcePlatform,
                                                                          product_id      AS ProductId,
                                                                          starts_at       AS StartsAt,
                                                                          expires_at      AS ExpiresAt,
                                                                          status          AS Status
                                                                        FROM entitlements
                                                                        WHERE user_id = @userId AND feature = 'vip'
                                                                        LIMIT 1;",
                                                                        new { userId });


        if (ent is null) return NotFound(new { message = "No entitlement" });
        return Ok(ent);
    }
}

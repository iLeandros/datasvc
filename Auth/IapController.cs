// IapController.cs
using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using DataSvc.Google;

namespace DataSvc.Iap;

[ApiController]
[Route("v1/iap")]
public sealed class IapController : ControllerBase
{
    private readonly GooglePlayClient _gp;
    private readonly string? _connString;
    public IapController(IConfiguration cfg)
    {
        _connString = cfg.GetConnectionString("Default");
    }

    // --- DTOs ---
    public sealed class PurchaseDto
    {
        public ulong     Id             { get; set; }
        public ulong     UserId         { get; set; }
        public string    Platform       { get; set; } = "";
        public string    ProductId      { get; set; } = "";
        public string?   OrderId        { get; set; }
        public string    PurchaseToken  { get; set; } = "";
        public string    State          { get; set; } = "";
        public DateTime  PurchasedAt    { get; set; }
        public DateTime? ExpiresAt      { get; set; }
        public bool      Acknowledged   { get; set; }
        public DateTime? LastCheckedAt  { get; set; }
    }
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

    [HttpPost("google/verify-consumable")]
    //[AllowAnonymous]
    //[Authorize]
    public async Task<IActionResult> VerifyConsumable(
        [FromBody] VerifyReq req,
        [FromServices] GooglePlayClient gp,
        CancellationToken ct)
    {
        Console.WriteLine("Malaka Lean");
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
            // 1) Look up product (alias -> store SKU + duration)
            var product = await conn.QuerySingleOrDefaultAsync<(int Days, string Sku)>(@"
                SELECT duration_days AS Days, store_sku AS Sku
                FROM products
                WHERE platform='google' AND product_id=@alias
                LIMIT 1;",
                new { alias = req.ProductId }, tx);
    
            if (product.Days <= 0 || string.IsNullOrWhiteSpace(product.Sku))
                return BadRequest("Unknown product.");
    
            // 2) Idempotency: if already processed, just return entitlement
            var existingState = await conn.ExecuteScalarAsync<string?>(@"
                SELECT state
                FROM purchases
                WHERE platform='google' AND purchase_token=@token
                LIMIT 1;",
                new { token = req.PurchaseToken }, tx);
    
            if (existingState is "consumed" or "granted")
            {
                var ent0 = await conn.QuerySingleAsync<EntitlementDto>(@"
                    SELECT user_id AS UserId, feature AS Feature, source_platform AS SourcePlatform,
                           product_id AS ProductId, starts_at AS StartsAt, expires_at AS ExpiresAt, status AS Status
                    FROM entitlements
                    WHERE user_id=@userId AND feature='vip'
                    LIMIT 1;",
                    new { userId }, tx);
    
                await tx.CommitAsync(ct);
                return Ok(ent0);
            }
    
            // 3) Verify with Google (purchases.products.get)
            var gpPurchase = await gp.GetProductAsync(product.Sku, req.PurchaseToken, ct);
    
            // PurchaseState: 0=Purchased, 1=Canceled, 2=Pending
            if (gpPurchase.PurchaseState != 0)
                return BadRequest($"Not purchased (purchaseState={gpPurchase.PurchaseState}).");
    
            // Optional: if client supplied orderId, require match
            if (!string.IsNullOrWhiteSpace(req.OrderId) &&
                !string.IsNullOrWhiteSpace(gpPurchase.OrderId) &&
                !string.Equals(req.OrderId, gpPurchase.OrderId, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("OrderId mismatch.");
            }
    
            // 4) Write purchase ledger
            const string ledgerSql = @"
                INSERT INTO purchases (
                  user_id, platform, product_id, order_id, purchase_token,
                  state, purchased_at, last_checked_at, provider_payload
                ) VALUES (
                  @userId, 'google', @alias, @orderId, @token,
                  'granted', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3),
                  JSON_OBJECT(
                    'sku', @sku,
                    'gp_orderId', @gpOrderId,
                    'purchaseState', @purchaseState,
                    'ackState', @ackState,
                    'consumptionState', @consumptionState,
                    'purchaseTimeMillis', @purchaseTimeMillis
                  )
                )
                ON DUPLICATE KEY UPDATE
                  last_checked_at = UTC_TIMESTAMP(3);";
    
            await conn.ExecuteAsync(ledgerSql, new
            {
                userId,
                alias = req.ProductId,
                orderId = gpPurchase.OrderId ?? req.OrderId,
                token = req.PurchaseToken,
                sku = product.Sku,
                gpOrderId = gpPurchase.OrderId,
                purchaseState = gpPurchase.PurchaseState,
                ackState = gpPurchase.AcknowledgementState,
                consumptionState = gpPurchase.ConsumptionState,
                purchaseTimeMillis = gpPurchase.PurchaseTimeMillis
            }, tx);
    
            // 5) Stack entitlement once
            const string entSql = @"
                INSERT INTO entitlements (
                  user_id, feature, source_platform, product_id, starts_at, expires_at, status
                )
                VALUES (
                  @userId, 'vip', 'google', @alias,
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
    
            await conn.ExecuteAsync(entSql, new { userId, alias = req.ProductId, days = product.Days }, tx);
    
            // 6) Optional consume server-side (usually not needed if client consumes)
            // try { await gp.ConsumeAsync(product.Sku, req.PurchaseToken, ct); } catch { }
    
            // 7) Return entitlement
            var ent = await conn.QuerySingleAsync<EntitlementDto>(@"
                SELECT user_id AS UserId, feature AS Feature, source_platform AS SourcePlatform,
                       product_id AS ProductId, starts_at AS StartsAt, expires_at AS ExpiresAt, status AS Status
                FROM entitlements
                WHERE user_id=@userId AND feature='vip'
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

    /*
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
    */
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
    
    // GET /v1/iap/purchases/me?limit=100&offset=0
    [HttpGet("purchases/me")]
    [Authorize]
    public async Task<IActionResult> GetMyPurchases([FromQuery] int limit = 100, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var userId))
            return Unauthorized();
    
        // Clamp to sensible bounds
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
    
        await using var conn = new MySqlConnector.MySqlConnection(_connString);
        var rows = await conn.QueryAsync<PurchaseDto>(@"
            SELECT
                id             AS Id,
                user_id        AS UserId,
                platform       AS Platform,
                product_id     AS ProductId,
                order_id       AS OrderId,
                purchase_token AS PurchaseToken,
                state          AS State,
                purchased_at   AS PurchasedAt,
                expires_at     AS ExpiresAt,
                acknowledged   AS Acknowledged,
                last_checked_at AS LastCheckedAt
            FROM purchases
            WHERE user_id = @userId
            ORDER BY purchased_at DESC, id DESC
            LIMIT @limit OFFSET @offset;",
            new { userId, limit, offset });
    
        return Ok(rows);
    }
    /*
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
    */
    
    [HttpGet("entitlements/me")]
    [Authorize]
    public async Task<IActionResult> GetMyEntitlement(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var userId))
            return Unauthorized();
    
        try
        {
            await using var conn = new MySqlConnection(_connString);
            var ent = await conn.QuerySingleOrDefaultAsync<EntitlementDto>(@"
                SELECT user_id AS UserId,
                       feature AS Feature,
                       source_platform AS SourcePlatform,
                       product_id AS ProductId,
                       starts_at AS StartsAt,
                       expires_at AS ExpiresAt,
                       status AS Status
                FROM entitlements
                WHERE user_id=@userId AND feature='vip'
                LIMIT 1;",
                new { userId });
    
            if (ent is null) return NotFound(new { message = "No entitlement" });
            return Ok(ent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex} GetMyEntitlement failed for user");
            return Problem("GetMyEntitlement failed: " + ex.Message);
        }
    }

}

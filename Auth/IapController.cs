// Auth/IapController.cs
using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using DataSvc.Google;
using DataSvc.Infrastructure;

namespace DataSvc.Iap;

[ApiController]
[Route("v1/iap")]
public sealed class IapController : ControllerBase
{
    private readonly GooglePlayClient _gp;
    private readonly string? _connString;

    // FIX C4: GooglePlayClient is now properly injected via constructor
    public IapController(IConfiguration cfg, GooglePlayClient gp)
    {
        _connString = cfg.GetConnectionString("Default");
        _gp = gp;
    }

    // --- DTOs ---
    public sealed class PurchaseDto
    {
        public ulong Id { get; set; }
        public ulong UserId { get; set; }
        public string Platform { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string? OrderId { get; set; }
        public string PurchaseToken { get; set; } = "";
        public string State { get; set; } = "";
        public DateTime PurchasedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool Acknowledged { get; set; }
        public DateTime? LastCheckedAt { get; set; }
    }

    public sealed class VerifyReq
    {
        public string ProductId { get; set; } = "";
        public string PurchaseToken { get; set; } = "";
        public string? OrderId { get; set; }
    }

    public sealed class EntitlementDto
    {
        public ulong UserId { get; set; }
        public string Feature { get; set; } = "";
        public string SourcePlatform { get; set; } = "";
        public string ProductId { get; set; } = "";
        public DateTime StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class RecordReq
    {
        public string Platform { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string PurchaseToken { get; set; } = "";
        public string? OrderId { get; set; }
    }

    // FIX C3 + M1: Uses shared UserIdentity helper — no debug_tokens fallback
    private bool TryGetUserId(out ulong userId)
        => UserIdentity.TryGetUserId(HttpContext, out userId);

    [HttpPost("google/verify-consumable-state")]
    [Authorize]
    public async Task<IActionResult> VerifyConsumableState(
        [FromBody] VerifyReq req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");

        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.ProductId) || string.IsNullOrWhiteSpace(req.PurchaseToken))
            return BadRequest("Missing productId or purchaseToken.");

        await using var conn = new MySqlConnection(_connString);
        await conn.OpenAsync(ct);

        var sku = await conn.ExecuteScalarAsync<string?>(@"
            SELECT store_sku
            FROM products
            WHERE platform='google' AND product_id=@alias
            LIMIT 1;",
            new { alias = req.ProductId });

        if (string.IsNullOrWhiteSpace(sku))
            return BadRequest("Unknown product.");

        try
        {
            var p = await _gp.GetProductAsync(sku, req.PurchaseToken, ct);

            var orderMatch =
                string.IsNullOrWhiteSpace(req.OrderId) ||
                string.IsNullOrWhiteSpace(p.OrderId) ||
                string.Equals(req.OrderId, p.OrderId, StringComparison.OrdinalIgnoreCase);

            return Ok(new
            {
                userId,
                alias = req.ProductId,
                sku,
                purchaseState = p.PurchaseState,
                consumptionState = p.ConsumptionState,
                acknowledgementState = p.AcknowledgementState,
                orderId = p.OrderId,
                orderMatch,
                purchaseTimeMillis = p.PurchaseTimeMillis,
                kind = p.Kind
            });
        }
        catch (global::Google.GoogleApiException ex)
        {
            return StatusCode((int)ex.HttpStatusCode, new
            {
                error = "google_api_error",
                httpStatus = (int)ex.HttpStatusCode,
                message = ex.Message
            });
        }
    }

    [HttpPost("google/verify-consumable")]
    [Authorize]
    public async Task<IActionResult> VerifyConsumable(
        [FromBody] VerifyReq req,
        CancellationToken ct)
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
            var product = await conn.QuerySingleOrDefaultAsync<(int Days, string Sku)>(@"
                SELECT duration_days AS Days, store_sku AS Sku
                FROM products
                WHERE platform='google' AND product_id=@alias
                LIMIT 1;",
                new { alias = req.ProductId }, tx);

            if (product.Days <= 0 || string.IsNullOrWhiteSpace(product.Sku))
                return BadRequest("Unknown product.");

            var existingState = await conn.ExecuteScalarAsync<string?>(@"
                SELECT state
                FROM purchases
                WHERE platform='google' AND purchase_token=@token
                LIMIT 1;",
                new { token = req.PurchaseToken }, tx);

            if (existingState is "consumed" or "granted")
            {
                var ent0 = await conn.QuerySingleOrDefaultAsync<EntitlementDto>(@"
                    SELECT user_id AS UserId, feature AS Feature, source_platform AS SourcePlatform,
                           product_id AS ProductId, starts_at AS StartsAt, expires_at AS ExpiresAt, status AS Status
                    FROM entitlements
                    WHERE user_id=@userId AND feature='vip'
                    LIMIT 1;",
                    new { userId }, tx);

                await tx.CommitAsync(ct);
                return ent0 is null
                    ? Ok(new { message = "Already processed", state = existingState })
                    : Ok(ent0);
            }

            var gpPurchase = await _gp.GetProductAsync(product.Sku, req.PurchaseToken, ct);

            if (gpPurchase.PurchaseState != 0)
                return BadRequest($"Not purchased (purchaseState={gpPurchase.PurchaseState}).");

            if (!string.IsNullOrWhiteSpace(req.OrderId) &&
                !string.IsNullOrWhiteSpace(gpPurchase.OrderId) &&
                !string.Equals(req.OrderId, gpPurchase.OrderId, StringComparison.OrdinalIgnoreCase))
                return BadRequest("OrderId mismatch.");

            await conn.ExecuteAsync(@"
                INSERT INTO purchases (
                    user_id, platform, product_id, order_id, purchase_token,
                    state, purchased_at, last_checked_at, provider_payload
                ) VALUES (
                    @userId, 'google', @alias, @orderId, @token,
                    'granted', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3),
                    JSON_OBJECT('sku', @sku, 'gp_orderId', @gpOrderId,
                                'purchaseState', @purchaseState, 'ackState', @ackState,
                                'consumptionState', @consumptionState,
                                'purchaseTimeMillis', @purchaseTimeMillis)
                )
                ON DUPLICATE KEY UPDATE last_checked_at = UTC_TIMESTAMP(3);",
                new
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

            await conn.ExecuteAsync(@"
                INSERT INTO entitlements (
                    user_id, feature, source_platform, product_id, starts_at, expires_at, status
                ) VALUES (
                    @userId, 'vip', 'google', @alias,
                    UTC_TIMESTAMP(3),
                    DATE_ADD(UTC_TIMESTAMP(3), INTERVAL @days DAY),
                    'active'
                )
                ON DUPLICATE KEY UPDATE
                    expires_at = DATE_ADD(
                        CASE WHEN entitlements.expires_at IS NULL OR entitlements.expires_at < UTC_TIMESTAMP(3)
                             THEN UTC_TIMESTAMP(3) ELSE entitlements.expires_at END,
                        INTERVAL @days DAY
                    ),
                    status = 'active',
                    product_id = VALUES(product_id),
                    source_platform = VALUES(source_platform);",
                new { userId, alias = req.ProductId, days = product.Days }, tx);

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
        catch (global::Google.GoogleApiException ex)
        {
            await tx.RollbackAsync(ct);
            return StatusCode((int)ex.HttpStatusCode, new
            {
                error = "google_api_error",
                httpStatus = (int)ex.HttpStatusCode,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return Problem(title: "verify-consumable failed", detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost("google/mark-consumed")]
    [Authorize]
    public async Task<IActionResult> MarkConsumed([FromBody] RecordReq body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.PurchaseToken)) return BadRequest();

        await using var conn = new MySqlConnection(_connString);
        var rows = await conn.ExecuteAsync(@"
            UPDATE purchases
            SET acknowledged = 1, state = 'consumed', last_checked_at = UTC_TIMESTAMP(3)
            WHERE platform='google' AND purchase_token=@token;",
            new { token = body.PurchaseToken });
        return rows > 0 ? Ok() : NotFound();
    }

    [HttpGet("purchases/me")]
    [Authorize]
    public async Task<IActionResult> GetMyPurchases(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connString))
            return Problem("Missing ConnectionStrings:Default.");
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        await using var conn = new MySqlConnection(_connString);
        var rows = await conn.QueryAsync<PurchaseDto>(@"
            SELECT id AS Id, user_id AS UserId, platform AS Platform,
                   product_id AS ProductId, order_id AS OrderId,
                   purchase_token AS PurchaseToken, state AS State,
                   purchased_at AS PurchasedAt, expires_at AS ExpiresAt,
                   acknowledged AS Acknowledged, last_checked_at AS LastCheckedAt
            FROM purchases
            WHERE user_id = @userId
            ORDER BY purchased_at DESC, id DESC
            LIMIT @limit OFFSET @offset;",
            new { userId, limit, offset });

        return Ok(rows);
    }

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
            SELECT user_id AS UserId, feature AS Feature, source_platform AS SourcePlatform,
                   product_id AS ProductId, starts_at AS StartsAt, expires_at AS ExpiresAt, status AS Status
            FROM entitlements
            WHERE user_id=@userId AND feature='vip'
            LIMIT 1;",
            new { userId });

        if (ent is null) return NotFound(new { message = "No entitlement" });
        return Ok(ent);
    }
}

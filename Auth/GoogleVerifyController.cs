using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using DataSvc.Google;
using Microsoft.AspNetCore.Mvc;

namespace DataSvc.GoogleVerifyController;

public static class Endpoints
{
    public sealed class VerifyReq
    {
        public string ProductId { get; set; } = "";
        public string PurchaseToken { get; set; } = "";
        public string? OrderId { get; set; }
    }
    public static IEndpointRouteBuilder MapGoogleVerifyController(this IEndpointRouteBuilder app)
    {
        [HttpPost("google/ping2")]
        [AllowAnonymous]
        [Consumes("application/json", "application/json; charset=utf-8")]
        [Produces("application/json")]
    
        public async Task<IActionResult> PingGooglePost(
            [FromBody] VerifyReq req,
            [FromServices] GooglePlayClient gp,
            CancellationToken ct)
        {
            Console.WriteLine($"PING ACTION ENTERED | ContentLength={Request.ContentLength}, ContentType={Request.ContentType}");
        
            Request.EnableBuffering();           // ← key line
            Request.Body.Position = 0;           // rewind if already partially read
        
            string rawBody = await new StreamReader(Request.Body).ReadToEndAsync();
        
            // rewind again so downstream code could read it if needed
            Request.Body.Position = 0;
        
            return Ok(new 
            { 
                message = "manual read with EnableBuffering",
                utc = DateTime.UtcNow,
                reportedContentType = Request.ContentType,
                reportedContentLength = Request.ContentLength,
                actualBodyLength = rawBody.Length,
                bodyPreview = rawBody.Length > 200 ? rawBody.Substring(0, 200) + "..." : rawBody
            });
        }
    }
}

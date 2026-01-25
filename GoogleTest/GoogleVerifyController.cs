using System.Text;
using DataSvc.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        var group = app.MapGroup("/google")
                       .WithTags("Google")
                       .AllowAnonymous();

        group.MapPost("/ping2", async (
            HttpContext ctx,
            //VerifyReq req,
            GooglePlayClient gp,
            CancellationToken ct) =>
        {
            Console.WriteLine($"PING2 ENTERED | CL={ctx.Request.ContentLength}, CT={ctx.Request.ContentType}");

            ctx.Request.EnableBuffering();
            ctx.Request.Body.Position = 0;

            string rawBody;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
                rawBody = await reader.ReadToEndAsync(ct);

            ctx.Request.Body.Position = 0;

            return Results.Ok(new
            {
                message = "manual read with EnableBuffering",
                utc = DateTime.UtcNow,
                reportedContentType = ctx.Request.ContentType,
                reportedContentLength = ctx.Request.ContentLength,
                actualBodyLength = rawBody.Length,
                bodyPreview = rawBody.Length > 200 ? rawBody[..200] + "..." : rawBody
            });
        })
        .Accepts<VerifyReq>("application/json"); // makes body/content-type expectations explicit

        return app;
    }
}

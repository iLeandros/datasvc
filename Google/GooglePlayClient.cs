// Google/GooglePlayClient.cs
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;

namespace DataSvc.Google;

public sealed class GooglePlayClient
{
    private readonly AndroidPublisherService _svc;
    private readonly string _packageName = "com.companyname.aiscorespredictor";
    private readonly ILogger<GooglePlayClient> _log;

    // FIX H5: constructor now fails fast — no try/catch swallowing startup errors
    public GooglePlayClient(IConfiguration cfg, ILogger<GooglePlayClient> log)
    {
        _log = log;
        _packageName = cfg["GooglePlay:PackageName"]
            ?? throw new InvalidOperationException(
                "Missing configuration key 'GooglePlay:PackageName'. " +
                "Set it via environment variable GooglePlay__PackageName or appsettings.json.");

        // Uses ADC: GOOGLE_APPLICATION_CREDENTIALS env var pointing to service account JSON,
        // or workload identity when running on GCP/Cloud Run.
        var cred = GoogleCredential.GetApplicationDefault()
            .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);

        _svc = new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "DataSvc-IAP"
        });
    }

    public async Task<SubscriptionPurchase> GetSubscriptionAsync(string sku, string token, CancellationToken ct)
    {
        _log.LogDebug("GP Subscriptions.Get package={Package} sku={Sku}", _packageName, sku);
        var result = await _svc.Purchases.Subscriptions.Get(_packageName, sku, token).ExecuteAsync(ct);
        _log.LogDebug("GP Subscriptions.Get OK orderId={OrderId} expiry={Expiry} paymentState={State}",
            result?.OrderId, result?.ExpiryTimeMillis, result?.PaymentState);
        return result;
    }

    public async Task<ProductPurchase> GetProductAsync(string sku, string token, CancellationToken ct)
    {
        _log.LogDebug("GP Products.Get package={Package} sku={Sku}", _packageName, sku);
        var result = await _svc.Purchases.Products.Get(_packageName, sku, token).ExecuteAsync(ct);
        _log.LogDebug("GP Products.Get OK orderId={OrderId} purchaseState={State}",
            result?.OrderId, result?.PurchaseState);
        return result;
    }

    public Task ConsumeAsync(string sku, string token, CancellationToken ct) =>
        _svc.Purchases.Products.Consume(_packageName, sku, token).ExecuteAsync(ct);
}

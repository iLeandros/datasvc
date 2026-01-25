using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

namespace DataSvc.Google;

public sealed class GooglePlayClient
{
    private readonly AndroidPublisherService _svc;
    private readonly string _packageName = "com.companyname.aiscorespredictor";

    public GooglePlayClient(IConfiguration cfg)
    {
        try
        {
            //_packageName = cfg["GooglePlay:com.companyname.aiscorespredictor"]
            //    ?? throw new InvalidOperationException("Missing GooglePlay:com.companyname.aiscorespredictor");
            _packageName = cfg["GooglePlay:PackageName"]
                ?? throw new InvalidOperationException("Missing GooglePlay:PackageName");
    
            // Uses ADC (service account via GOOGLE_APPLICATION_CREDENTIALS, or workload identity)
            var cred = GoogleCredential.GetApplicationDefault()
                .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
    
            _svc = new AndroidPublisherService(new BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = "DataSvc-IAP"
            });
        }
        catch(Exception ex)
        {
            Console.WriteLine($"GooglePlayClient error: {ex.Message}");
        }
    }
    /*
    public Task<ProductPurchase> GetProductAsync(string sku, string token, CancellationToken ct) =>
        _svc.Purchases.Products.Get(_packageName, sku, token).ExecuteAsync(ct);
    */
    public Task<SubscriptionPurchase> GetSubscriptionAsync(string sku, string token, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"[GP] Purchases.Products.Get Subscription package={_packageName} sku={sku} tokenLen={token?.Length ?? 0}");
            var result = await _svc.Purchases.Subscriptions.Get(_packageName, sku, token).ExecuteAsync(ct);
            Console.WriteLine($"[GP] OK Subscription orderId={result?.OrderId} purchaseState={result?.PurchaseState}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GooglePlayClient error Subscription: {ex.GetType().FullName}: {ex.Message}");
            Console.WriteLine($"GooglePlayClient error Subscription: {ex.ToString()}");
            throw;
        }
    }
    public async Task<ProductPurchase> GetProductAsync(string sku, string token, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"[GP] Purchases.Products.Get package={_packageName} sku={sku} tokenLen={token?.Length ?? 0}");
            var result = await _svc.Purchases.Products.Get(_packageName, sku, token).ExecuteAsync(ct);
            Console.WriteLine($"[GP] OK orderId={result?.OrderId} purchaseState={result?.PurchaseState}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GooglePlayClient error: {ex.GetType().FullName}: {ex.Message}");
            Console.WriteLine($"GooglePlayClient error: {ex.ToString()}");
            throw;
        }
    }

    public Task ConsumeAsync(string sku, string token, CancellationToken ct) =>
        _svc.Purchases.Products.Consume(_packageName, sku, token).ExecuteAsync(ct);
}

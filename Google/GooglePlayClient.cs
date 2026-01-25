using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

namespace DataSvc.Google;

public sealed class GooglePlayClient
{
    private readonly AndroidPublisherService _svc;
    private readonly string _packageName;

    public GooglePlayClient(IConfiguration cfg)
    {
        try
        {
            _packageName = cfg["GooglePlay:com.companyname.aiscorespredictor"]
                ?? throw new InvalidOperationException("Missing GooglePlay:com.companyname.aiscorespredictor");
    
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

    public Task<ProductPurchase> GetProductAsync(string sku, string token, CancellationToken ct) =>
        _svc.Purchases.Products.Get(_packageName, sku, token).ExecuteAsync(ct);

    public Task ConsumeAsync(string sku, string token, CancellationToken ct) =>
        _svc.Purchases.Products.Consume(_packageName, sku, token).ExecuteAsync(ct);
}

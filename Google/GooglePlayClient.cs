using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

public sealed class GooglePlayClient
{
    private readonly AndroidPublisherService _svc;
    private readonly string _packageName;

    public GooglePlayClient(IConfiguration cfg)
    {
        _packageName = cfg["GooglePlay:com.companyname.aiscorespredictor"]
            ?? throw new InvalidOperationException("Missing GooglePlay:com.companyname.aiscorespredictor");

        var cred = GoogleCredential.GetApplicationDefault()
            .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);

        _svc = new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "DataSvc-IAP"
        });
    }

    public async Task<AndroidPublisherService.PurchasesResource.ProductsResource.GetRequest> BuildGet(
        string sku, string token)
    {
        return _svc.Purchases.Products.Get(_packageName, sku, token);
    }

    public async Task<Google.Apis.AndroidPublisher.v3.Data.ProductPurchase> GetProductAsync(
        string sku, string token, CancellationToken ct)
    {
        return await _svc.Purchases.Products.Get(_packageName, sku, token).ExecuteAsync(ct);
    }

    public async Task ConsumeAsync(string sku, string token, CancellationToken ct)
    {
        await _svc.Purchases.Products.Consume(_packageName, sku, token).ExecuteAsync(ct);
    }
}

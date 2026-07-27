namespace PoCiSys.Gateway;

public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    public string BackendBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string? BackendApiKey { get; set; }
    public string? GatewayApiKey { get; set; }
    public bool ConnectionManaged { get; set; }
    public bool PersistentEvidenceEnabled { get; set; }
    public int PersistentReceiptLimit { get; set; } = 5_000;
    public int MaxRequestMegabytes { get; set; } = 32;
    public int RequestTimeoutMinutes { get; set; } = 30;
    public int LiveReceiptLimit { get; set; } = 500;

    public Uri GetValidatedBackendUri()
    {
        if (!Uri.TryCreate(BackendBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Gateway:BackendBaseUrl must be an absolute HTTP or HTTPS URL.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Gateway:BackendBaseUrl cannot contain a query or fragment.");
        return uri;
    }
}

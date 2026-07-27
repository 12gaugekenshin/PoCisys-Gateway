using System.Net;
using PoCiSys.Gateway;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Gateway:ListenUrl"] ?? "http://127.0.0.1:8719");

var options = new GatewayOptions();
builder.Configuration.GetSection(GatewayOptions.Section).Bind(options);
options.GetValidatedBackendUri();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<GatewayUserSettingsStore>();
builder.Services.AddSingleton<BackendTargetStore>();
builder.Services.AddSingleton<ModelBaselineStore>();
builder.Services.AddSingleton<ReceiptStore>();
builder.Services.AddSingleton<GatewayEvidenceLedger>();
builder.Services.AddSingleton<KaspaAnchorPlanner>();
builder.Services.AddHttpClient<GatewayForwarder>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<BackendProbe>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

var app = builder.Build();
app.MapGet("/", () => Results.Content(ChatPage.Html, "text/html; charset=utf-8"));
app.MapGet("/chat", () => Results.Content(ChatPage.Html, "text/html; charset=utf-8"));
app.MapGet("/admin", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));
app.MapGet("/pocisys/api/health", (BackendTargetStore targetStore) => Results.Ok(new
{
    status = "ready",
    schema = "pocisys.gateway-health.v1",
    backendConfigured = !string.IsNullOrWhiteSpace(targetStore.Read().BaseUrl),
    privacy = "ephemeral_metadata_and_hashes",
}));
app.MapGet("/pocisys/api/summary", (ReceiptStore store) => store.Summary());
app.MapGet("/pocisys/api/receipts", (ReceiptStore store, int? limit) => store.Read(limit ?? 100));
app.MapGet("/pocisys/api/baselines", (ModelBaselineStore baselines) => baselines.Read());
app.MapGet("/pocisys/api/evidence/status", (GatewayEvidenceLedger evidence) => evidence.Status());
app.MapGet("/pocisys/api/settings", (GatewayOptions settings, BackendTargetStore targetStore) => Results.Ok(new
{
    backend = new { value = targetStore.Read().BaseUrl, explanation = "Where PoCiSys sends approved AI requests. Prompt and response content passes through but is not saved." },
    liveReceiptLimit = new { value = settings.LiveReceiptLimit, explanation = "Maximum recent request summaries held in memory. Old entries are automatically removed." },
    maxRequestMegabytes = new { value = settings.MaxRequestMegabytes, explanation = "Rejects unusually large requests before they can consume excessive memory or bandwidth." },
    timeoutMinutes = new { value = settings.RequestTimeoutMinutes, explanation = "Stops waiting when an AI service becomes stuck. Streaming responses may run until this limit." },
    gatewayKey = new { enabled = !string.IsNullOrEmpty(settings.GatewayApiKey), explanation = "Optional key that prevents unapproved applications from using this Gateway." },
    privacy = new { value = "Temporary metadata and hashes", explanation = "Prompts and answers are never stored. Live measurements disappear when Gateway restarts." },
}));
app.MapGet("/pocisys/api/preferences", (GatewayUserSettingsStore settings) => settings.Read());
app.MapPut("/pocisys/api/preferences", (GatewayUserSettings updated, GatewayUserSettingsStore settings) =>
    Results.Ok(settings.Save(updated)));
app.MapGet("/pocisys/api/connection", (HttpContext context, BackendTargetStore target, GatewayUserSettingsStore preferences) =>
{
    if (!options.ConnectionManaged && !IsLocalAdministrator(context))
        return Results.Problem("AI connection details are available only on the Gateway machine.", statusCode: 403);
    var current = target.Read();
    var user = preferences.Read();
    return Results.Ok(new
    {
        baseUrl = current.BaseUrl,
        provider = current.Provider,
        defaultModel = user.DefaultModel,
        chatEnabled = user.BuiltInChatEnabled,
        managed = options.ConnectionManaged,
        explanation = options.ConnectionManaged
            ? "The AI service is fixed by this Umbrel deployment. The dashboard can test it but cannot redirect traffic."
            : "This is the AI service PoCiSys will contact. API keys are configured separately and are never returned here.",
    });
});
app.MapGet("/pocisys/api/chat/config", async (
    HttpContext context,
    GatewayUserSettingsStore preferences,
    BackendProbe probe) =>
{
    var user = preferences.Read();
    if (!user.BuiltInChatEnabled)
        return Results.Ok(new
        {
            connected = false,
            provider = "unknown",
            models = Array.Empty<string>(),
            defaultModel = user.DefaultModel,
            chatEnabled = false,
            explanation = "Built-in Chat is disabled in Setup.",
        });
    var result = await probe.Test(context.RequestAborted);
    return Results.Ok(new
    {
        connected = result.Connected,
        provider = result.Provider,
        models = result.Models,
        defaultModel = user.DefaultModel,
        chatEnabled = true,
        explanation = result.Explanation,
    });
});
app.MapPut("/pocisys/api/connection", (
    HttpContext context,
    BackendTargetUpdate update,
    BackendTargetStore target) =>
{
    if (options.ConnectionManaged)
    {
        try
        {
            var current = target.Read();
            var requestedUrl = BackendTargetStore.Validate(update.BaseUrl).AbsoluteUri.TrimEnd('/');
            var requestedProvider = BackendTargetStore.NormalizeProvider(update.Provider);
            return string.Equals(requestedUrl, current.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
                   requestedProvider == current.Provider
                ? Results.Ok(current)
                : Results.Conflict(new { error = "The AI connection is managed by this Umbrel deployment and cannot be changed from the dashboard." });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
    if (!IsLocalAdministrator(context))
        return Results.Problem("AI connection settings can only be changed from the Gateway machine.", statusCode: 403);
    try { return Results.Ok(target.Save(update)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/pocisys/api/connection/test", async (HttpContext context, BackendProbe probe) =>
{
    if (!options.ConnectionManaged && !IsLocalAdministrator(context))
        return Results.Problem("AI connection testing can only be started from the Gateway machine.", statusCode: 403);
    return Results.Ok(await probe.Test(context.RequestAborted));
});
app.MapGet("/pocisys/api/kaspa/status", (GatewayUserSettingsStore preferences) =>
{
    var mode = preferences.Read().KaspaMode;
    return Results.Ok(new
    {
        mode,
        broadcast = false,
        network = "testnet-10 planned",
        explanation = mode == "simulation"
            ? "Simulation builds commitment payload plans without loading a wallet, spending KAS or broadcasting."
            : "Kaspa anchoring is disabled. The current build can create and test commitment payload plans without loading a wallet or broadcasting a transaction.",
    });
});
app.MapPost("/pocisys/api/kaspa/simulate", (ReceiptStore store, KaspaAnchorPlanner planner) =>
{
    var hashes = store.Read(100).Where(item => item.Completed).Select(item => item.ResponseSha256).ToArray();
    return hashes.Length == 0
        ? Results.BadRequest(new { error = "Run at least one AI request before creating an anchor simulation." })
        : Results.Ok(planner.CreateMockPlan(hashes));
});

var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD" };
app.MapMethods("/v1/{**rest}", methods, (HttpContext context, GatewayForwarder forwarder) => forwarder.Forward(context));
app.MapMethods("/api/{**rest}", methods, (HttpContext context, GatewayForwarder forwarder) => forwarder.Forward(context));

static bool IsLocalAdministrator(HttpContext context)
{
    var address = context.Connection.RemoteIpAddress;
    return address is not null && IPAddress.IsLoopback(address);
}

app.Run();

public partial class Program;

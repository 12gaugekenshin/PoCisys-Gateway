using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace PoCiSys.Gateway;

public sealed class GatewayForwarder
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length",
    };

    private readonly HttpClient _client;
    private readonly GatewayOptions _options;
    private readonly ReceiptStore _store;
    private readonly BackendTargetStore _targetStore;

    public GatewayForwarder(
        HttpClient client,
        GatewayOptions options,
        ReceiptStore store,
        BackendTargetStore targetStore)
    {
        _client = client;
        _options = options;
        _store = store;
        _targetStore = targetStore;
    }

    public async Task Forward(HttpContext context)
    {
        if (!IsAuthorized(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid PoCiSys Gateway key." });
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var receiptId = Guid.NewGuid().ToString("N");
        var backend = _targetStore.ReadUri();
        var inspector = new ProviderStreamInspector();
        HashingHttpContent? requestContent = null;
        using var responseHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long responseBytes = 0;
        double? firstByteMs = null;
        double? firstOutputMs = null;
        string? firstOutputSource = null;
        var status = 502;
        string? errorKind = null;
        var completed = false;

        context.Response.Headers["X-PoCiSys-Receipt-Id"] = receiptId;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.RequestTimeoutMinutes, 1, 1440)));
            using var upstream = BuildRequest(context.Request, backend, receiptId, out requestContent);
            using var response = await _client.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            context.Response.StatusCode = status;
            CopyResponseHeaders(response, context.Response);

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                firstByteMs ??= stopwatch.Elapsed.TotalMilliseconds;
                responseHash.AppendData(buffer, 0, read);
                responseBytes += read;
                if (inspector.Feed(buffer.AsSpan(0, read)) && firstOutputMs is null)
                {
                    firstOutputMs = stopwatch.Elapsed.TotalMilliseconds;
                    firstOutputSource = "provider_content";
                }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                await context.Response.Body.FlushAsync(timeout.Token);
            }

            if (inspector.Complete() && firstOutputMs is null)
            {
                firstOutputMs = stopwatch.Elapsed.TotalMilliseconds;
                firstOutputSource = "provider_content";
            }
            if (firstOutputMs is null && firstByteMs.HasValue)
            {
                firstOutputMs = firstByteMs;
                firstOutputSource = "first_response_byte";
            }
            completed = true;
        }
        catch (InvalidDataException exception)
        {
            status = StatusCodes.Status413PayloadTooLarge;
            errorKind = "request_too_large";
            await WriteErrorIfPossible(context, status, exception.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            status = 499;
            errorKind = "client_disconnected";
        }
        catch (OperationCanceledException)
        {
            status = StatusCodes.Status504GatewayTimeout;
            errorKind = "backend_timeout";
            await WriteErrorIfPossible(context, status, "The configured AI backend timed out.");
        }
        catch (HttpRequestException)
        {
            status = StatusCodes.Status502BadGateway;
            errorKind = "backend_unavailable";
            await WriteErrorIfPossible(context, status, "The configured AI backend is unavailable.");
        }
        catch (Exception)
        {
            status = StatusCodes.Status502BadGateway;
            errorKind = "gateway_error";
            await WriteErrorIfPossible(context, status, "PoCiSys could not complete this request.");
        }
        finally
        {
            stopwatch.Stop();
            var responseDigest = Convert.ToHexString(responseHash.GetHashAndReset()).ToLowerInvariant();
            var measuredSeconds = firstOutputMs.HasValue
                ? Math.Max(0.001, (stopwatch.Elapsed.TotalMilliseconds - firstOutputMs.Value) / 1000d)
                : Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            var tokensPerSecond = inspector.ProviderTokensPerSecond ??
                (inspector.OutputTokens is > 0 ? inspector.OutputTokens.Value / measuredSeconds : null);
            var receipt = new GatewayReceipt(
                "pocisys.gateway-exchange.v1",
                receiptId,
                startedAt,
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                backend.GetLeftPart(UriPartial.Authority),
                status,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                firstOutputMs.HasValue ? Math.Round(firstOutputMs.Value, 3) : null,
                firstOutputSource,
                requestContent?.BytesRead ?? 0,
                responseBytes,
                requestContent?.Hash ?? EmptySha256,
                responseDigest,
                inspector.Model,
                inspector.InputTokens,
                inspector.OutputTokens,
                inspector.InputTokens.HasValue || inspector.OutputTokens.HasValue ? "provider_reported" : "unavailable",
                tokensPerSecond.HasValue ? Math.Round(tokensPerSecond.Value, 3) : null,
                completed,
                errorKind)
            {
                SessionHash = HashOptionalSession(context.Request.Headers["X-PoCiSys-Session"].ToString()),
            };
            _store.Add(receipt);
        }
    }

    private HttpRequestMessage BuildRequest(
        HttpRequest request,
        Uri backend,
        string receiptId,
        out HashingHttpContent? content)
    {
        var destination = BuildDestination(backend, request);
        var message = new HttpRequestMessage(new HttpMethod(request.Method), destination);
        content = HasBody(request)
            ? new HashingHttpContent(request.Body, Math.Max(1, _options.MaxRequestMegabytes) * 1024L * 1024L, request.Headers)
            : null;
        if (content is not null)
            message.Content = content;

        foreach (var header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) ||
                header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("X-PoCiSys-Gateway-Key", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("X-PoCiSys-Session", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        message.Headers.TryAddWithoutValidation("X-PoCiSys-Receipt-Id", receiptId);
        if (!string.IsNullOrWhiteSpace(_options.BackendApiKey))
        {
            message.Headers.Remove("Authorization");
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.BackendApiKey);
        }
        return message;
    }

    private static Uri BuildDestination(Uri backend, HttpRequest request)
    {
        var relative = (request.Path.Value ?? string.Empty).TrimStart('/');
        return BackendUri.Append(backend, relative + request.QueryString);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_options.GatewayApiKey))
            return true;
        var supplied = request.Headers["X-PoCiSys-Gateway-Key"].ToString();
        var expectedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_options.GatewayApiKey));
        var suppliedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding") ||
        request.Method is "POST" or "PUT" or "PATCH";

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key))
                destination.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static async Task WriteErrorIfPossible(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
            return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static string EmptySha256 =>
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static string? HashOptionalSession(string value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "PoCiSys-Gateway-Session-v1\0" + value))).ToLowerInvariant()[..24];
}

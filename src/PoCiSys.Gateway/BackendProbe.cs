using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PoCiSys.Gateway;

public sealed class BackendProbe
{
    private readonly HttpClient _client;
    private readonly BackendTargetStore _targetStore;
    private readonly GatewayOptions _options;

    public BackendProbe(HttpClient client, BackendTargetStore targetStore, GatewayOptions options)
    {
        _client = client;
        _targetStore = targetStore;
        _options = options;
    }

    public async Task<BackendProbeResult> Test(CancellationToken cancellationToken = default)
    {
        var target = _targetStore.Read();
        var root = BackendTargetStore.Validate(target.BaseUrl);
        var providers = target.Provider switch
        {
            "ollama" => new[] { "ollama" },
            "openai" => new[] { "openai" },
            _ => new[] { "ollama", "openai" },
        };
        var errors = new List<string>();
        foreach (var provider in providers)
        {
            var result = await TryProvider(root, provider, cancellationToken);
            if (result.Connected)
                return result;
            if (!string.IsNullOrWhiteSpace(result.Explanation))
                errors.Add(result.Explanation);
        }
        return new BackendProbeResult(
            false, target.Provider, target.BaseUrl, [], null,
            errors.Count == 0
                ? "PoCiSys could not reach a compatible model-list endpoint."
                : string.Join(" ", errors.Distinct(StringComparer.Ordinal)));
    }

    private async Task<BackendProbeResult> TryProvider(Uri root, string provider, CancellationToken cancellationToken)
    {
        var path = provider == "ollama" ? "api/tags" : "v1/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, BackendUri.Append(root, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.BackendApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BackendApiKey);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new BackendProbeResult(false, provider, root.AbsoluteUri.TrimEnd('/'), [],
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                    $"{ProviderName(provider)} answered with HTTP {(int)response.StatusCode}.");
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token);
            var models = provider == "ollama" ? ReadOllamaModels(document.RootElement) : ReadOpenAiModels(document.RootElement);
            return new BackendProbeResult(
                true, provider, root.AbsoluteUri.TrimEnd('/'), models,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                models.Count == 0
                    ? "Connection succeeded, but the AI service reported no installed or permitted models."
                    : $"Connected to {ProviderName(provider)} and found {models.Count} model{(models.Count == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException)
        {
            return new BackendProbeResult(false, provider, root.AbsoluteUri.TrimEnd('/'), [],
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), "The AI service did not answer within eight seconds.");
        }
        catch (HttpRequestException)
        {
            return new BackendProbeResult(false, provider, root.AbsoluteUri.TrimEnd('/'), [],
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), "PoCiSys could not open a connection to the AI address.");
        }
        catch (JsonException)
        {
            return new BackendProbeResult(false, provider, root.AbsoluteUri.TrimEnd('/'), [],
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), $"{ProviderName(provider)} returned an unreadable model list.");
        }
    }

    private static IReadOnlyList<string> ReadOllamaModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return [];
        return models.EnumerateArray()
            .Select(item => ReadString(item, "name") ?? ReadString(item, "model"))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Take(500).ToArray();
    }

    private static IReadOnlyList<string> ReadOpenAiModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray().Select(item => ReadString(item, "id"))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Take(500).ToArray();
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ProviderName(string provider) => provider == "ollama" ? "Ollama" : "the OpenAI-compatible service";
}

public sealed record BackendProbeResult(
    bool Connected,
    string Provider,
    string Backend,
    IReadOnlyList<string> Models,
    double? LatencyMs,
    string Explanation);

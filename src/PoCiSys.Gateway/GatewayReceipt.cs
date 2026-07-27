using System.Text.Json.Serialization;

namespace PoCiSys.Gateway;

public sealed record GatewayReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("receipt_id")] string ReceiptId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("backend_origin")] string BackendOrigin,
    [property: JsonPropertyName("status_code")] int StatusCode,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("first_output_ms")] double? FirstOutputMs,
    [property: JsonPropertyName("first_output_source")] string? FirstOutputSource,
    [property: JsonPropertyName("request_bytes")] long RequestBytes,
    [property: JsonPropertyName("response_bytes")] long ResponseBytes,
    [property: JsonPropertyName("request_sha256")] string RequestSha256,
    [property: JsonPropertyName("response_sha256")] string ResponseSha256,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("usage_source")] string UsageSource,
    [property: JsonPropertyName("tokens_per_second")] double? TokensPerSecond,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("error_kind")] string? ErrorKind)
{
    [JsonPropertyName("assessment")]
    public string Assessment { get; init; } = "learning";

    [JsonPropertyName("findings")]
    public IReadOnlyList<string> Findings { get; init; } = [];

    [JsonPropertyName("session_hash")]
    public string? SessionHash { get; init; }
}

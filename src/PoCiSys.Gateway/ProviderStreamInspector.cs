using System.Text;
using System.Text.Json;

namespace PoCiSys.Gateway;

public sealed class ProviderStreamInspector
{
    private const int MaximumPendingCharacters = 1024 * 1024;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();

    public string? Model { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public double? ProviderTokensPerSecond { get; private set; }
    public bool SawOutput { get; private set; }

    public bool Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return false;

        var before = SawOutput;
        var charCount = _decoder.GetCharCount(bytes, flush: false);
        var chars = new char[charCount];
        _decoder.GetChars(bytes, chars, flush: false);
        _pending.Append(chars);

        var text = _pending.ToString();
        var consumed = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', consumed);
            if (newline < 0)
                break;
            InspectLine(text.AsSpan(consumed, newline - consumed).Trim());
            consumed = newline + 1;
        }

        if (consumed > 0)
            _pending.Remove(0, consumed);
        if (_pending.Length > MaximumPendingCharacters)
            _pending.Clear();
        return !before && SawOutput;
    }

    public bool Complete()
    {
        var before = SawOutput;
        if (_pending.Length > 0)
            InspectLine(_pending.ToString().AsSpan().Trim());
        _pending.Clear();
        return !before && SawOutput;
    }

    private void InspectLine(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
            return;
        if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            line = line[5..].Trim();
        if (line.SequenceEqual("[DONE]"))
            return;

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(line.ToString()));
            var root = document.RootElement;
            ReadString(root, "model", value => Model = value);
            InspectOpenAi(root);
            InspectOllama(root);
        }
        catch (JsonException)
        {
            // Transparent forwarding is more important than understanding an unknown provider chunk.
        }
    }

    private void InspectOpenAi(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            InputTokens = ReadLong(usage, "prompt_tokens") ?? ReadLong(usage, "input_tokens") ?? InputTokens;
            OutputTokens = ReadLong(usage, "completion_tokens") ?? ReadLong(usage, "output_tokens") ?? OutputTokens;
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return;
        foreach (var choice in choices.EnumerateArray())
        {
            if (HasText(choice, "text") || HasNestedText(choice, "delta", "content") ||
                HasNestedText(choice, "message", "content"))
            {
                SawOutput = true;
                return;
            }
        }
    }

    private void InspectOllama(JsonElement root)
    {
        if (HasText(root, "response") || HasNestedText(root, "message", "content"))
            SawOutput = true;
        InputTokens = ReadLong(root, "prompt_eval_count") ?? InputTokens;
        OutputTokens = ReadLong(root, "eval_count") ?? OutputTokens;

        var durationNanoseconds = ReadLong(root, "eval_duration");
        if (OutputTokens is > 0 && durationNanoseconds is > 0)
            ProviderTokensPerSecond = OutputTokens.Value / (durationNanoseconds.Value / 1_000_000_000d);
    }

    private static long? ReadLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static void ReadString(JsonElement parent, string name, Action<string> accept)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
            accept(text);
    }

    private static bool HasText(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString());

    private static bool HasNestedText(JsonElement parent, string objectName, string valueName) =>
        parent.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object &&
        HasText(nested, valueName);
}

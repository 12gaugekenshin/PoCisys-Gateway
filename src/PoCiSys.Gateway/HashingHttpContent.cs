using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PoCiSys.Gateway;

public sealed class HashingHttpContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _maximumBytes;

    public HashingHttpContent(Stream source, long maximumBytes, IHeaderDictionary headers)
    {
        _source = source;
        _maximumBytes = maximumBytes;
        foreach (var header in headers)
        {
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    public long BytesRead { get; private set; }
    public string Hash { get; private set; } = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await _source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            BytesRead += read;
            if (BytesRead > _maximumBytes)
                throw new InvalidDataException($"Request body exceeded the {_maximumBytes} byte gateway limit.");
            hasher.AppendData(buffer, 0, read);
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        Hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

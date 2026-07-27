using System.Text.Json;

namespace PoCiSys.Gateway;

public sealed class BackendTargetStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private BackendTarget _target;

    public BackendTargetStore(GatewayOptions options, string? overrideRoot = null)
    {
        var configured = Environment.GetEnvironmentVariable("POCISYS_GATEWAY_DATA_DIR");
        var root = overrideRoot is not null
            ? Path.GetFullPath(overrideRoot)
            : !string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoCiSys", "Gateway");
        _path = Path.Combine(root, "backend-target.json");
        _target = Load(options.BackendBaseUrl);
    }

    public BackendTarget Read()
    {
        lock (_gate)
            return _target with { };
    }

    public Uri ReadUri() => Validate(Read().BaseUrl);

    public BackendTarget Save(BackendTargetUpdate update)
    {
        var uri = Validate(update.BaseUrl);
        var provider = NormalizeProvider(update.Provider);
        var target = new BackendTarget(uri.AbsoluteUri.TrimEnd('/'), provider, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(target, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            _target = target;
            return target with { };
        }
    }

    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The AI address must begin with http:// or https://.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("The AI address cannot contain credentials, a query, or a page fragment.");
        return uri;
    }

    public static string NormalizeProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "ollama" => "ollama",
        "openai" => "openai",
        _ => "auto",
    };

    private BackendTarget Load(string fallback)
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<BackendTarget>(File.ReadAllText(_path));
                if (loaded is not null)
                    return loaded with
                    {
                        BaseUrl = Validate(loaded.BaseUrl).AbsoluteUri.TrimEnd('/'),
                        Provider = NormalizeProvider(loaded.Provider),
                    };
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return new BackendTarget(Validate(fallback).AbsoluteUri.TrimEnd('/'), "auto", DateTimeOffset.UtcNow);
    }
}

public sealed record BackendTarget(string BaseUrl, string Provider, DateTimeOffset UpdatedAt);
public sealed record BackendTargetUpdate(string BaseUrl, string? Provider);

public static class BackendUri
{
    public static Uri Append(Uri root, string relativePathAndQuery)
    {
        var baseAddress = root.AbsoluteUri.EndsWith('/') ? root.AbsoluteUri : root.AbsoluteUri + "/";
        return new Uri(new Uri(baseAddress), relativePathAndQuery.TrimStart('/'));
    }
}

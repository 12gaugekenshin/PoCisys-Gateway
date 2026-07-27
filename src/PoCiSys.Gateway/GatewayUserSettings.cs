using System.Text.Json;

namespace PoCiSys.Gateway;

public sealed class GatewayUserSettings
{
    public bool SetupComplete { get; set; }
    public bool BaselinesEnabled { get; set; } = true;
    public bool BuiltInChatEnabled { get; set; } = true;
    public bool MonitorCompanionEnabled { get; set; } = true;
    public bool PersistentEvidenceEnabled { get; set; }
    public string KaspaMode { get; set; } = "off";
    public string MonitoringSensitivity { get; set; } = "balanced";
    public int BaselineLearningRequests { get; set; } = 5;
    public bool MeasureFirstOutput { get; set; } = true;
    public bool MeasureTokensPerSecond { get; set; } = true;
    public bool MeasureTotalTime { get; set; } = true;
    public bool WatchModelIdentity { get; set; } = true;
    public bool TrackContextChanges { get; set; } = true;
    public int AnchorEveryEvents { get; set; } = 1_000;
    public int AnchorEveryMinutes { get; set; } = 5;
    public string? DefaultModel { get; set; }

    public void Normalize()
    {
        MonitoringSensitivity = MonitoringSensitivity is "relaxed" or "strict" ? MonitoringSensitivity : "balanced";
        // Persistent signing and real wallet modes are not accepted until those modules are installed.
        PersistentEvidenceEnabled = false;
        KaspaMode = KaspaMode == "simulation" ? "simulation" : "off";
        BaselineLearningRequests = Math.Clamp(BaselineLearningRequests, 5, 100);
        AnchorEveryEvents = Math.Clamp(AnchorEveryEvents, 10, 100_000);
        AnchorEveryMinutes = Math.Clamp(AnchorEveryMinutes, 1, 1_440);
        DefaultModel = string.IsNullOrWhiteSpace(DefaultModel) ? null : DefaultModel.Trim()[..Math.Min(200, DefaultModel.Trim().Length)];
    }
}

public sealed class GatewayUserSettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private GatewayUserSettings _settings;

    public GatewayUserSettingsStore(string? overrideRoot = null)
    {
        var configured = Environment.GetEnvironmentVariable("POCISYS_GATEWAY_DATA_DIR");
        var root = overrideRoot is not null
            ? Path.GetFullPath(overrideRoot)
            : !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoCiSys", "Gateway");
        _path = Path.Combine(root, "gateway-settings.json");
        _settings = Load();
    }

    public GatewayUserSettings Read()
    {
        lock (_gate)
            return Clone(_settings);
    }

    public GatewayUserSettings Save(GatewayUserSettings settings)
    {
        settings.Normalize();
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            _settings = Clone(settings);
            return Clone(_settings);
        }
    }

    private GatewayUserSettings Load()
    {
        try
        {
            var loaded = File.Exists(_path)
                ? JsonSerializer.Deserialize<GatewayUserSettings>(File.ReadAllText(_path)) ?? new GatewayUserSettings()
                : new GatewayUserSettings();
            loaded.Normalize();
            return loaded;
        }
        catch (JsonException)
        {
            return new GatewayUserSettings();
        }
    }

    private static GatewayUserSettings Clone(GatewayUserSettings value) =>
        JsonSerializer.Deserialize<GatewayUserSettings>(JsonSerializer.Serialize(value))!;
}

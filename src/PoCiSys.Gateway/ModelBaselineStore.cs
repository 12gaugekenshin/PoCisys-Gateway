namespace PoCiSys.Gateway;

public sealed class ModelBaselineStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelState> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private readonly GatewayUserSettingsStore? _settingsStore;

    public ModelBaselineStore(GatewayUserSettingsStore? settingsStore = null, int capacity = 256)
    {
        _settingsStore = settingsStore;
        _capacity = Math.Clamp(capacity, 10, 5_000);
    }

    public BaselineAssessment AssessAndLearn(GatewayReceipt receipt)
    {
        var model = string.IsNullOrWhiteSpace(receipt.Model) ? "Unreported model" : receipt.Model.Trim();
        var settings = _settingsStore?.Read() ?? new GatewayUserSettings { SetupComplete = true };
        if (!settings.BaselinesEnabled)
            return new BaselineAssessment("monitoring_only", []);
        var minimumSamples = settings.BaselineLearningRequests;
        lock (_gate)
        {
            if (!_models.TryGetValue(model, out var state))
            {
                if (_models.Count >= _capacity)
                {
                    var oldest = _models.MinBy(pair => pair.Value.UpdatedAt).Key;
                    _models.Remove(oldest);
                }
                state = new ModelState(model);
                _models.Add(model, state);
            }

            var findings = new List<string>();
            if (receipt.StatusCode >= 400 || receipt.ErrorKind is not null)
                findings.Add("The request failed or did not complete normally.");

            if (state.Samples >= minimumSamples && receipt.Completed)
            {
                if (settings.MeasureTokensPerSecond && IsLow(receipt.TokensPerSecond, state.TokensPerSecond, settings.MonitoringSensitivity))
                    findings.Add("Generation speed was much slower than this model's recent baseline.");
                if (settings.MeasureFirstOutput && IsHigh(receipt.FirstOutputMs, state.FirstOutputMs, settings.MonitoringSensitivity))
                    findings.Add("The first output took much longer than this model normally takes.");
                if (settings.MeasureTotalTime && IsHigh(receipt.DurationMs, state.DurationMs, settings.MonitoringSensitivity))
                    findings.Add("The full request took much longer than this model's recent baseline.");
                if (settings.TrackContextChanges)
                    state.CheckContext(receipt, findings);
            }

            var status = findings.Count > 0 ? "needs_attention" :
                state.Samples < minimumSamples ? "learning" : "within_baseline";
            if (receipt.Completed && receipt.StatusCode is >= 200 and < 400)
                state.Learn(receipt);
            state.LastAssessment = status;
            state.LastFindings = findings;
            state.UpdatedAt = receipt.StartedAt;
            return new BaselineAssessment(status, findings);
        }
    }

    public IReadOnlyList<ModelBaselineSnapshot> Read()
    {
        var minimumSamples = (_settingsStore?.Read() ?? new GatewayUserSettings()).BaselineLearningRequests;
        lock (_gate)
            return _models.Values.OrderByDescending(item => item.UpdatedAt)
                .Select(item => item.Snapshot(minimumSamples)).ToArray();
    }

    private static bool IsLow(double? value, RunningMetric baseline, string sensitivity)
    {
        if (!value.HasValue || baseline.Count < 5 || baseline.Mean <= 0)
            return false;
        var (deviations, percent) = sensitivity switch { "strict" => (1.5, 0.20), "relaxed" => (3d, 0.50), _ => (2d, 0.35) };
        var allowance = Math.Max(baseline.StandardDeviation * deviations, baseline.Mean * percent);
        return value.Value < baseline.Mean - allowance;
    }

    private static bool IsHigh(double? value, RunningMetric baseline, string sensitivity)
    {
        if (!value.HasValue || baseline.Count < 5 || baseline.Mean <= 0)
            return false;
        var (deviations, percent) = sensitivity switch { "strict" => (1.5, 0.30), "relaxed" => (3d, 0.75), _ => (2d, 0.50) };
        var allowance = Math.Max(baseline.StandardDeviation * deviations, baseline.Mean * percent);
        return value.Value > baseline.Mean + allowance;
    }

    private sealed class ModelState(string model)
    {
        public string Model { get; } = model;
        public long Samples { get; private set; }
        public RunningMetric TokensPerSecond { get; } = new();
        public RunningMetric FirstOutputMs { get; } = new();
        public RunningMetric DurationMs { get; } = new();
        public RunningMetric InputTokens { get; } = new();
        private readonly Dictionary<string, long> _lastSessionInput = new(StringComparer.Ordinal);
        public DateTimeOffset UpdatedAt { get; set; }
        public string LastAssessment { get; set; } = "learning";
        public IReadOnlyList<string> LastFindings { get; set; } = [];

        public void Learn(GatewayReceipt receipt)
        {
            Samples++;
            TokensPerSecond.Add(receipt.TokensPerSecond);
            FirstOutputMs.Add(receipt.FirstOutputMs);
            DurationMs.Add(receipt.DurationMs);
            InputTokens.Add(receipt.InputTokens);
            if (receipt.SessionHash is not null && receipt.InputTokens.HasValue)
            {
                if (_lastSessionInput.Count >= 256 && !_lastSessionInput.ContainsKey(receipt.SessionHash))
                    _lastSessionInput.Remove(_lastSessionInput.Keys.First());
                _lastSessionInput[receipt.SessionHash] = receipt.InputTokens.Value;
            }
        }

        public void CheckContext(GatewayReceipt receipt, List<string> findings)
        {
            if (receipt.SessionHash is null || !receipt.InputTokens.HasValue ||
                !_lastSessionInput.TryGetValue(receipt.SessionHash, out var previous) || previous < 128)
                return;
            if (receipt.InputTokens.Value < previous * 0.60)
                findings.Add("The context size dropped sharply in this session. The AI service may have compacted or discarded older context.");
            else if (InputTokens.Mean > 0 && receipt.InputTokens.Value > InputTokens.Mean * 1.5 &&
                     IsLow(receipt.TokensPerSecond, TokensPerSecond, "balanced"))
                findings.Add("This session became slower while its context grew. A long context may be contributing to the slowdown.");
        }

        public ModelBaselineSnapshot Snapshot(int minimumSamples) => new(
            Model, Samples, Round(TokensPerSecond.Mean), Round(FirstOutputMs.Mean),
            Round(DurationMs.Mean), LastAssessment, LastFindings, UpdatedAt,
            Samples < minimumSamples
                ? $"Learning normal behavior ({Samples}/{minimumSamples} successful requests)."
                : "Ready to compare new requests with learned normal behavior.");

        private static double? Round(double value) => value <= 0 ? null : Math.Round(value, 2);
    }

    private sealed class RunningMetric
    {
        private double _sumOfSquares;
        public long Count { get; private set; }
        public double Mean { get; private set; }
        public double StandardDeviation => Count < 2 ? 0 : Math.Sqrt(_sumOfSquares / (Count - 1));

        public void Add(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return;
            Count++;
            var delta = value.Value - Mean;
            Mean += delta / Count;
            _sumOfSquares += delta * (value.Value - Mean);
        }
    }
}

public sealed record BaselineAssessment(string Status, IReadOnlyList<string> Findings);

public sealed record ModelBaselineSnapshot(
    string Model,
    long SuccessfulSamples,
    double? AverageTokensPerSecond,
    double? AverageFirstOutputMs,
    double? AverageDurationMs,
    string LastAssessment,
    IReadOnlyList<string> LastFindings,
    DateTimeOffset UpdatedAt,
    string ConfidenceExplanation);

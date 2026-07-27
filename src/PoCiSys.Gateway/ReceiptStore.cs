namespace PoCiSys.Gateway;

public sealed class ReceiptStore
{
    private readonly object _gate = new();
    private readonly LinkedList<GatewayReceipt> _receipts = new();
    private readonly int _capacity;
    private readonly ModelBaselineStore _baselines;
    private readonly GatewayEvidenceLedger? _evidence;

    public ReceiptStore(GatewayOptions options, ModelBaselineStore baselines, GatewayEvidenceLedger? evidence = null)
    {
        _capacity = Math.Clamp(options.LiveReceiptLimit, 10, 10_000);
        _baselines = baselines;
        _evidence = evidence;
    }

    public void Add(GatewayReceipt receipt)
    {
        var assessment = _baselines.AssessAndLearn(receipt);
        receipt = receipt with { Assessment = assessment.Status, Findings = assessment.Findings };
        _evidence?.TryAppend(receipt);
        lock (_gate)
        {
            _receipts.AddFirst(receipt);
            while (_receipts.Count > _capacity)
                _receipts.RemoveLast();
        }
    }

    public IReadOnlyList<GatewayReceipt> Read(int limit = 100)
    {
        lock (_gate)
            return _receipts.Take(Math.Clamp(limit, 1, _capacity)).ToArray();
    }

    public GatewaySummary Summary()
    {
        lock (_gate)
        {
            var completed = _receipts.Where(item => item.Completed).ToArray();
            var successful = completed.Where(item => item.StatusCode is >= 200 and < 400).ToArray();
            var measured = completed.Where(item => item.TokensPerSecond.HasValue).ToArray();
            return new GatewaySummary(
                _receipts.Count,
                completed.Length,
                completed.Count(item => item.StatusCode >= 400 || item.ErrorKind is not null),
                successful.Length == 0 ? null : successful.Average(item => item.DurationMs),
                measured.Length == 0 ? null : measured.Average(item => item.TokensPerSecond!.Value),
                completed.FirstOrDefault()?.StartedAt);
        }
    }
}

public sealed record GatewaySummary(
    int BufferedReceipts,
    int CompletedRequests,
    int FailedRequests,
    double? AverageLatencyMs,
    double? AverageTokensPerSecond,
    DateTimeOffset? LastRequestAt);

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoCiSys.Gateway;

public sealed class GatewayEvidenceLedger : IDisposable
{
    public const string Assurance = "gateway_self_attested";
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Algorithm = "ecdsa-p256-sha256";
    private static readonly byte[] EntryDomain = Encoding.UTF8.GetBytes("PoCiSys-Gateway-Evidence-v1\0");
    private static readonly byte[] WindowDomain = Encoding.UTF8.GetBytes("PoCiSys-Gateway-Evidence-Window-v1\0");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly int _limit;
    private readonly string _path;
    private readonly ECDsa? _key;
    private GatewayEvidenceWindow _window;
    private string? _lastError;

    public GatewayEvidenceLedger(GatewayOptions options, string? overrideRoot = null)
    {
        _enabled = options.PersistentEvidenceEnabled;
        _limit = Math.Clamp(options.PersistentReceiptLimit, 10, 100_000);
        var root = ResolveRoot(overrideRoot);
        _path = Path.Combine(root, "gateway-evidence-window.json");
        if (!_enabled)
        {
            _window = EmptyWindow();
            return;
        }

        Directory.CreateDirectory(root);
        _key = LoadOrCreateKey(Path.Combine(root, "gateway-evidence-key.p8"));
        GatewayId = ComputeGatewayId(PublicKey);
        _window = File.Exists(_path)
            ? JsonSerializer.Deserialize<GatewayEvidenceWindow>(File.ReadAllText(_path), JsonOptions)
              ?? throw new InvalidDataException("Gateway evidence window is empty.")
            : SignWindow(new GatewayEvidenceWindowBody(
                "pocisys.gateway-evidence-window.v1", Assurance, Algorithm,
                GatewayId, PublicKey, Genesis, []));
        var verification = Verify(_window);
        if (!verification.Valid)
            throw new InvalidDataException("Gateway evidence verification failed: " + string.Join("; ", verification.Errors));
    }

    public string GatewayId { get; } = string.Empty;
    public string PublicKey => _key is null ? string.Empty : Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public GatewaySignedEvidence? Append(GatewayReceipt receipt)
    {
        if (!_enabled)
            return null;
        lock (_gate)
        {
            var entries = _window.Body.Entries.ToList();
            var previous = entries.Count == 0 ? _window.Body.AnchorPreviousHash : entries[^1].ReceiptHash;
            var sequence = entries.Count == 0 ? 1 : entries[^1].Body.Sequence + 1;
            var body = new GatewayEvidenceBody(
                "pocisys.gateway-evidence.v1", Assurance, Algorithm, sequence,
                DateTimeOffset.UtcNow, GatewayId, PublicKey, previous, receipt);
            var hash = Hash(EntryDomain, body);
            var signed = new GatewaySignedEvidence(body, Hex(hash), Convert.ToBase64String(_key!.SignHash(hash)));
            entries.Add(signed);
            var anchor = _window.Body.AnchorPreviousHash;
            while (entries.Count > _limit)
            {
                anchor = entries[0].ReceiptHash;
                entries.RemoveAt(0);
            }
            _window = SignWindow(_window.Body with { AnchorPreviousHash = anchor, Entries = entries });
            Persist();
            return signed;
        }
    }

    public GatewayEvidenceStatus Status()
    {
        lock (_gate)
        {
            var verification = _enabled ? Verify(_window) : new GatewayEvidenceVerification(true, 0, Genesis, []);
            var errors = _lastError is null ? verification.Errors : verification.Errors.Append(_lastError).ToArray();
            return new GatewayEvidenceStatus(
                _enabled, Assurance, _limit, _window.Body.Entries.Count,
                verification.Valid && _lastError is null, verification.ChainHead, errors,
                _enabled ? GatewayId : null,
                "Gateway signatures make retained metadata tamper-evident; they are not independent proof of model execution or answer correctness.");
        }
    }

    public bool TryAppend(GatewayReceipt receipt)
    {
        try
        {
            Append(receipt);
            _lastError = null;
            return true;
        }
        catch (IOException exception) { _lastError = "evidence write failed: " + exception.GetType().Name; }
        catch (UnauthorizedAccessException exception) { _lastError = "evidence write failed: " + exception.GetType().Name; }
        catch (CryptographicException exception) { _lastError = "evidence signing failed: " + exception.GetType().Name; }
        return false;
    }

    public GatewayEvidenceWindow ReadWindow()
    {
        lock (_gate)
            return JsonSerializer.Deserialize<GatewayEvidenceWindow>(JsonSerializer.Serialize(_window, JsonOptions), JsonOptions)!;
    }

    public static GatewayEvidenceVerification Verify(GatewayEvidenceWindow window)
    {
        var errors = new List<string>();
        if (window.Body.Schema != "pocisys.gateway-evidence-window.v1" || window.Body.Assurance != Assurance)
            errors.Add("unsupported window profile");
        if (window.Body.SignatureAlgorithm != Algorithm)
            errors.Add("unsupported window signature algorithm");
        if (ComputeGatewayId(window.Body.GatewayPublicKey) != window.Body.GatewayId)
            errors.Add("window gateway ID does not match public key");
        var windowHash = Hash(WindowDomain, window.Body);
        if (Hex(windowHash) != window.WindowHash)
            errors.Add("window hash mismatch");
        if (!VerifyHash(window.Body.GatewayPublicKey, windowHash, window.Signature))
            errors.Add("window signature invalid");

        var previous = window.Body.AnchorPreviousHash;
        long? previousSequence = null;
        for (var index = 0; index < window.Body.Entries.Count; index++)
        {
            var entry = window.Body.Entries[index];
            var prefix = $"entry {index + 1}";
            if (entry.Body.Schema != "pocisys.gateway-evidence.v1" || entry.Body.Assurance != Assurance)
                errors.Add($"{prefix}: unsupported profile");
            if (entry.Body.SignatureAlgorithm != Algorithm)
                errors.Add($"{prefix}: unsupported signature algorithm");
            if (entry.Body.GatewayId != window.Body.GatewayId || entry.Body.GatewayPublicKey != window.Body.GatewayPublicKey)
                errors.Add($"{prefix}: gateway identity mismatch");
            if (entry.Body.PreviousReceiptHash != previous)
                errors.Add($"{prefix}: previous hash mismatch");
            if (previousSequence.HasValue && entry.Body.Sequence != previousSequence + 1)
                errors.Add($"{prefix}: sequence mismatch");
            var hash = Hash(EntryDomain, entry.Body);
            if (Hex(hash) != entry.ReceiptHash)
                errors.Add($"{prefix}: content hash mismatch");
            if (!VerifyHash(entry.Body.GatewayPublicKey, hash, entry.Signature))
                errors.Add($"{prefix}: signature invalid");
            previous = entry.ReceiptHash;
            previousSequence = entry.Body.Sequence;
        }
        return new GatewayEvidenceVerification(errors.Count == 0, window.Body.Entries.Count, previous, errors);
    }

    private GatewayEvidenceWindow SignWindow(GatewayEvidenceWindowBody body)
    {
        if (_key is null)
            return EmptyWindow();
        var hash = Hash(WindowDomain, body);
        return new GatewayEvidenceWindow(body, Hex(hash), Convert.ToBase64String(_key.SignHash(hash)));
    }

    private void Persist()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_window, JsonOptions), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, _path, true);
    }

    private static string ResolveRoot(string? overrideRoot)
    {
        if (overrideRoot is not null)
            return Path.GetFullPath(overrideRoot);
        var configured = Environment.GetEnvironmentVariable("POCISYS_GATEWAY_DATA_DIR");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoCiSys", "Gateway");
    }

    private static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
            return key;
        }
        var bytes = key.ExportPkcs8PrivateKey();
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        CryptographicOperations.ZeroMemory(bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
        return key;
    }

    private static byte[] Hash(byte[] domain, object value)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var material = new byte[domain.Length + encoded.Length];
        domain.CopyTo(material, 0);
        encoded.CopyTo(material, domain.Length);
        return SHA256.HashData(material);
    }

    private static bool VerifyHash(string publicKey, byte[] hash, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyHash(hash, Convert.FromBase64String(signature));
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private static string ComputeGatewayId(string publicKey)
    {
        try { return "pocisys-gateway:" + Hex(SHA256.HashData(Convert.FromBase64String(publicKey)))[..24]; }
        catch (FormatException) { return string.Empty; }
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static GatewayEvidenceWindow EmptyWindow() => new(
        new GatewayEvidenceWindowBody("pocisys.gateway-evidence-window.v1", Assurance, Algorithm, "", "", Genesis, []), "", "");
    public void Dispose() => _key?.Dispose();
}

public sealed record GatewayEvidenceBody(
    string Schema, string Assurance, string SignatureAlgorithm, long Sequence,
    DateTimeOffset RecordedAt, string GatewayId, string GatewayPublicKey,
    string PreviousReceiptHash, GatewayReceipt Receipt);
public sealed record GatewaySignedEvidence(GatewayEvidenceBody Body, string ReceiptHash, string Signature);
public sealed record GatewayEvidenceWindowBody(
    string Schema, string Assurance, string SignatureAlgorithm, string GatewayId,
    string GatewayPublicKey, string AnchorPreviousHash, IReadOnlyList<GatewaySignedEvidence> Entries);
public sealed record GatewayEvidenceWindow(GatewayEvidenceWindowBody Body, string WindowHash, string Signature);
public sealed record GatewayEvidenceVerification(bool Valid, int Checked, string ChainHead, IReadOnlyList<string> Errors);
public sealed record GatewayEvidenceStatus(
    bool Enabled, string Assurance, int RetentionLimit, int RetainedReceipts,
    bool Valid, string ChainHead, IReadOnlyList<string> Errors, string? GatewayId, string Explanation);

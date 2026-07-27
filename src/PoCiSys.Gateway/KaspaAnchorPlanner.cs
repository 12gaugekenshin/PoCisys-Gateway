using System.Security.Cryptography;
using System.Text;

namespace PoCiSys.Gateway;

public sealed class KaspaAnchorPlanner
{
    public KaspaAnchorPlan CreateMockPlan(
        IReadOnlyList<string> receiptHashes,
        string network = "testnet-10",
        string previousAnchor = "genesis")
    {
        if (receiptHashes.Count == 0)
            throw new ArgumentException("At least one receipt hash is required.", nameof(receiptHashes));
        var leaves = receiptHashes.Select(ParseHash).ToArray();
        var root = MerkleRoot(leaves);
        var batchId = Guid.NewGuid().ToString("N");
        var payloadText = $"POCISYS|1|{network}|{batchId}|{Convert.ToHexString(root).ToLowerInvariant()}|{previousAnchor}|{leaves.Length}";
        var payload = Encoding.UTF8.GetBytes(payloadText);
        return new KaspaAnchorPlan(
            "pocisys.kaspa-anchor-plan.v1", "simulation", network, batchId,
            Convert.ToHexString(root).ToLowerInvariant(), previousAnchor, leaves.Length,
            Convert.ToHexString(payload).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            false,
            "This is a transaction plan only. No wallet key was loaded and nothing was broadcast.");
    }

    public static byte[] MerkleRoot(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 0)
            throw new ArgumentException("Merkle trees require at least one leaf.", nameof(leaves));
        var level = leaves.Select(HashLeaf).ToList();
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var index = 0; index < level.Count; index += 2)
            {
                var right = index + 1 < level.Count ? level[index + 1] : level[index];
                var material = new byte[1 + level[index].Length + right.Length];
                material[0] = 1;
                level[index].CopyTo(material, 1);
                right.CopyTo(material, 1 + level[index].Length);
                next.Add(SHA256.HashData(material));
            }
            level = next;
        }
        return level[0];
    }

    private static byte[] HashLeaf(byte[] hash)
    {
        var material = new byte[hash.Length + 1];
        hash.CopyTo(material, 1);
        return SHA256.HashData(material);
    }

    private static byte[] ParseHash(string hash)
    {
        if (hash.Length != 64)
            throw new FormatException("Receipt hashes must be 32-byte lowercase or uppercase hexadecimal values.");
        try { return Convert.FromHexString(hash); }
        catch (FormatException) { throw new FormatException("Receipt hashes must be hexadecimal values."); }
    }
}

public sealed record KaspaAnchorPlan(
    string Schema,
    string Mode,
    string Network,
    string BatchId,
    string MerkleRoot,
    string PreviousAnchor,
    int ReceiptCount,
    string TransactionPayloadHex,
    string MockReference,
    bool Broadcast,
    string Explanation);

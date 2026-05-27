using System.Security.Cryptography;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal static class TransportStreamService
{
    public static TransportStreamDecodeResult DecodeSegment(byte[] encryptedBytes, HlsSegment segment, CancellationToken cancellationToken)
    {
        var bestCandidate = BuildCandidate(encryptedBytes, "raw");
        if (segment.KeyCandidates.Count == 0 && IsUsableCandidate(bestCandidate))
        {
            return TransportStreamDecodeResult.Success(
                bestCandidate.Bytes,
                bestCandidate,
                ivCandidateCount: 0,
                attempts: 0,
                isRaw: true);
        }

        var ivCandidates = BuildIvCandidates(segment);
        var attempts = 0;

        foreach (var key in segment.KeyCandidates)
        {
            foreach (var iv in ivCandidates)
            {
                foreach (var paddingMode in new[] { PaddingMode.None, PaddingMode.PKCS7 })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;

                    byte[] decryptedBytes;
                    try
                    {
                        decryptedBytes = DecryptAes128Cbc(encryptedBytes, key, iv, paddingMode);
                    }
                    catch (CryptographicException)
                    {
                        continue;
                    }

                    var strategy = $"AES-128-CBC/{paddingMode}/iv={Convert.ToHexString(iv)}";
                    var candidate = BuildCandidate(decryptedBytes, strategy);
                    bestCandidate = SelectBetterCandidate(bestCandidate, candidate);
                }
            }
        }

        return IsUsableCandidate(bestCandidate)
            ? TransportStreamDecodeResult.Success(bestCandidate.Bytes, bestCandidate, ivCandidates.Count, attempts, isRaw: false)
            : TransportStreamDecodeResult.Failure(bestCandidate, ivCandidates.Count, attempts);
    }

    private static byte[] DecryptAes128Cbc(byte[] encryptedBytes, byte[] key, byte[] iv, PaddingMode paddingMode)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = paddingMode;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
    }

    private static IReadOnlyList<byte[]> BuildIvCandidates(HlsSegment segment)
    {
        var candidates = new List<byte[]>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIv(segment.ManifestIv);
        AddIv(HlsManifestService.BuildSequenceIv(segment.SequenceNumber));
        AddIv(new byte[16]);

        return candidates;

        void AddIv(byte[]? iv)
        {
            if (iv is null || iv.Length != 16)
            {
                return;
            }

            var key = Convert.ToHexString(iv);
            if (!seen.Add(key))
            {
                return;
            }

            var copy = new byte[16];
            Buffer.BlockCopy(iv, 0, copy, 0, 16);
            candidates.Add(copy);
        }
    }

    private static TransportStreamCandidate BuildCandidate(byte[] bytes, string strategy)
    {
        var (offset, syncCount) = FindBestSync(bytes);
        if (offset < 0)
        {
            return new TransportStreamCandidate(Array.Empty<byte>(), -1, syncCount, strategy);
        }

        var availableLength = bytes.Length - offset;
        var alignedLength = availableLength - availableLength % 188;
        if (alignedLength < 188)
        {
            return new TransportStreamCandidate(Array.Empty<byte>(), offset, syncCount, strategy);
        }

        var normalized = new byte[alignedLength];
        Buffer.BlockCopy(bytes, offset, normalized, 0, alignedLength);
        return new TransportStreamCandidate(normalized, offset, syncCount, strategy);
    }

    private static TransportStreamCandidate SelectBetterCandidate(
        TransportStreamCandidate current,
        TransportStreamCandidate next)
    {
        var currentUsable = IsUsableCandidate(current);
        var nextUsable = IsUsableCandidate(next);

        if (nextUsable && !currentUsable)
        {
            return next;
        }

        if (!nextUsable && currentUsable)
        {
            return current;
        }

        if (next.SyncCount != current.SyncCount)
        {
            return next.SyncCount > current.SyncCount ? next : current;
        }

        if (next.Offset >= 0 && (current.Offset < 0 || next.Offset < current.Offset))
        {
            return next;
        }

        return current;
    }

    private static bool IsUsableCandidate(TransportStreamCandidate candidate)
    {
        if (candidate.Bytes.Length < 188 || candidate.Bytes[0] != 0x47)
        {
            return false;
        }

        var packetCount = candidate.Bytes.Length / 188;
        var requiredSyncCount = Math.Min(3, packetCount);
        return candidate.SyncCount >= requiredSyncCount;
    }

    private static (int Offset, int SyncCount) FindBestSync(byte[] bytes)
    {
        const int packetSize = 188;
        const int maxProbePackets = 32;
        const int maxSearchPackets = 32;

        if (bytes.Length < packetSize)
        {
            return (-1, 0);
        }

        var maxSearchOffset = Math.Min(bytes.Length - packetSize, packetSize * maxSearchPackets);
        var bestOffset = -1;
        var bestSyncCount = 0;

        for (var offset = 0; offset <= maxSearchOffset; offset++)
        {
            if (bytes[offset] != 0x47)
            {
                continue;
            }

            var availablePackets = Math.Min(maxProbePackets, (bytes.Length - offset) / packetSize);
            var syncCount = 0;
            for (var index = 0; index < availablePackets; index++)
            {
                if (bytes[offset + packetSize * index] != 0x47)
                {
                    break;
                }

                syncCount++;
            }

            if (syncCount > bestSyncCount || (syncCount == bestSyncCount && bestOffset >= 0 && offset < bestOffset))
            {
                bestOffset = offset;
                bestSyncCount = syncCount;
            }
        }

        return bestOffset < 0 ? (-1, bestSyncCount) : (bestOffset, bestSyncCount);
    }
}

internal sealed record TransportStreamDecodeResult(
    bool IsSuccess,
    byte[] Bytes,
    TransportStreamCandidate BestCandidate,
    int IvCandidateCount,
    int Attempts,
    bool IsRaw)
{
    public static TransportStreamDecodeResult Success(
        byte[] bytes,
        TransportStreamCandidate bestCandidate,
        int ivCandidateCount,
        int attempts,
        bool isRaw)
    {
        return new TransportStreamDecodeResult(true, bytes, bestCandidate, ivCandidateCount, attempts, isRaw);
    }

    public static TransportStreamDecodeResult Failure(
        TransportStreamCandidate bestCandidate,
        int ivCandidateCount,
        int attempts)
    {
        return new TransportStreamDecodeResult(false, Array.Empty<byte>(), bestCandidate, ivCandidateCount, attempts, false);
    }
}

using System.Security.Cryptography;

namespace Sofia.Contracts.Infrastructure;

public static class Ids
{
    /// <summary>Time-sortable id: prefix + 11 hex chars of ms timestamp + 8 random hex chars.</summary>
    public static string New(string prefix)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> rnd = stackalloc byte[4];
        RandomNumberGenerator.Fill(rnd);
        return $"{prefix}_{ms:x11}{Convert.ToHexString(rnd).ToLowerInvariant()}";
    }
}

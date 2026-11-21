using System.Collections.Concurrent;
using System.Security.Cryptography;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Infrastructure;
using Sofia.Core.Data;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Security;

/// <summary>One-time pairing codes exchanged for long-lived bearer tokens (remote clients only).</summary>
public sealed class PairingService(SofiaDb db)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTime> _codes = new();

    public (string Code, int ExpiresIn) CreateCode()
    {
        foreach (var (code, expires) in _codes)
            if (expires < DateTime.UtcNow) _codes.TryRemove(code, out _);
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var value = new string(chars);
        _codes[value] = DateTime.UtcNow + CodeLifetime;
        return (value, (int)CodeLifetime.TotalSeconds);
    }

    public string? Redeem(string code)
    {
        var normalized = code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        if (!_codes.TryRemove(normalized, out var expires) || expires < DateTime.UtcNow) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.Tokens.Insert(new AccessToken { Id = Text.Sha256(token) });
        return token;
    }

    public bool IsValid(string token) => !string.IsNullOrEmpty(token) && db.Tokens.FindById(Text.Sha256(token)) is not null;
}

using System.Security.Cryptography;
using System.Text;

namespace Restaurant.Application.Common.Security;

/// <summary>Random secrets (refresh tokens, device tokens, invite codes) and their stored hashes.</summary>
public static class Secrets
{
    // No 0/O, 1/I/L: invite codes are read out loud or typed from a slip of paper.
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string NewToken(int bytes = 48) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>"K7P3-QX9M": 8 characters from a 31-letter alphabet (~40 bits), short-lived and single-use.</summary>
    public static string NewInviteCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];
        }
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }

    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    public static string HashInviteCode(string code) =>
        Hash(new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()));
}

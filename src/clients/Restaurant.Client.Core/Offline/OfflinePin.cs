using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Client.Core.Offline;

/// <summary>
/// Lets the person signed in on a counter PC unlock its screen while the API can't be reached. After each
/// successful PIN sign-in the PC keeps a salted PBKDF2 hash of that PIN in its secure store (Windows Data
/// Protection), per staff member; nothing else about the PIN is kept. Five wrong tries wipe it, so the
/// next unlock needs the connection.
/// </summary>
public class OfflinePin(ISecureStore secure, IPlatformInfo platform)
{
    private const int Iterations = 120_000;
    private const int MaxAttempts = 5;
    private readonly Dictionary<Guid, int> _failures = [];

    public bool Supported => platform.IsDesktop;

    /// <summary>After a PIN sign-in the API accepted.</summary>
    public async Task RememberAsync(Guid membershipId, string pin)
    {
        if (!Supported) return;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(pin, salt);
        await secure.SetAsync(Key(membershipId), $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
        _failures.Remove(membershipId);
    }

    public async Task<bool> HasAsync(Guid membershipId) => Supported && !string.IsNullOrEmpty(await secure.GetAsync(Key(membershipId)));

    public async Task<bool> VerifyAsync(Guid membershipId, string pin)
    {
        if (!Supported) return false;
        var stored = await secure.GetAsync(Key(membershipId));
        var parts = stored?.Split(':');
        if (parts is not { Length: 2 }) return false;

        var ok = CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(parts[0])), Convert.FromBase64String(parts[1]));
        if (ok)
        {
            _failures.Remove(membershipId);
            return true;
        }
        _failures[membershipId] = _failures.GetValueOrDefault(membershipId) + 1;
        if (_failures[membershipId] >= MaxAttempts)
        {
            await ForgetAsync(membershipId);
        }
        return false;
    }

    public Task ForgetAsync(Guid membershipId) => secure.RemoveAsync(Key(membershipId));

    [UnsupportedOSPlatform("browser")]
    private static byte[] DeriveCore(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private byte[] Derive(string pin, byte[] salt) =>
        OperatingSystem.IsBrowser() ? throw new PlatformNotSupportedException() : DeriveCore(pin, salt);

    private static string Key(Guid membershipId) => $"rp.offline-pin.{membershipId:N}";
}

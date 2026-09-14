using System.Security.Cryptography;
using System.Text;

namespace CWGaming.Shared;

/// <summary>
/// Generic credential hashing shared by the BBS host and any hosted door. The BBS owns account
/// passwords, so the hash lives here rather than in a game-specific repository.
///
/// New passwords are hashed with PBKDF2-HMAC-SHA256 (salted, high iteration count). Legacy
/// unsalted SHA-256 hashes are still <em>verified</em> so nobody is locked out, and callers should
/// transparently re-hash them via <see cref="NeedsRehash"/> on the next successful login. PBKDF2
/// was chosen over bcrypt/argon2 because it ships in both the .NET BCL and the Python standard
/// library, letting the game server and the web explorer share one format with zero extra deps.
/// </summary>
public static class BbsSecurity
{
    // OWASP-aligned PBKDF2 parameters. Bump Iterations over time; NeedsRehash() will trigger an
    // upgrade for anyone whose stored hash used a lower count.
    private const string Pbkdf2Prefix = "pbkdf2_sha256";
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Hash a password using the current (PBKDF2) scheme.</summary>
    /// <returns>"pbkdf2_sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;"</returns>
    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? string.Empty),
            salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Pbkdf2Prefix}${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verify a password against a stored hash of either format (current PBKDF2 or legacy SHA-256).
    /// Constant-time so it does not leak information via timing.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        if (storedHash.StartsWith(Pbkdf2Prefix + "$", StringComparison.Ordinal))
            return VerifyPbkdf2(password ?? string.Empty, storedHash);

        // Legacy path: unsalted uppercase-hex SHA-256.
        return FixedTimeEquals(LegacyHash(password ?? string.Empty), storedHash);
    }

    /// <summary>
    /// True when <paramref name="storedHash"/> uses an outdated scheme (legacy SHA-256, or PBKDF2
    /// with fewer iterations than we now use) and should be re-hashed on next successful login.
    /// </summary>
    public static bool NeedsRehash(string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;
        if (!storedHash.StartsWith(Pbkdf2Prefix + "$", StringComparison.Ordinal))
            return true; // legacy SHA-256

        string[] parts = storedHash.Split('$');
        return !(parts.Length == 4
                 && int.TryParse(parts[1], out int iterations)
                 && iterations >= Pbkdf2Iterations);
    }

    private static bool VerifyPbkdf2(string password, string storedHash)
    {
        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Legacy unsalted SHA-256 as uppercase hex, retained ONLY for verifying and migrating old
    // credentials. Never used to write a new hash.
    private static string LegacyHash(string password)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        byte[] ba = Encoding.ASCII.GetBytes(a);
        byte[] bb = Encoding.ASCII.GetBytes(b);
        if (ba.Length != bb.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

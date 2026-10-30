using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Idb.Authentication;

/// <summary>Versioned, salted PBKDF2-SHA256 using the platform cryptography implementation.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly int _iterations;
    public Pbkdf2PasswordHasher(int iterations = 600_000)
    {
        if (iterations is < 600_000 or > 2_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        _iterations = iterations;
    }
    public string Hash(string password)
    {
        AccountCredentialValidation.ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, 32);
        try { return string.Join('$', "idb-pbkdf2-sha256-v1", _iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash)); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    public bool Verify(string password, string encodedHash)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) > 1024 || encodedHash is null || encodedHash.Length > 256) return false;
        try
        {
            var fields = encodedHash.Split('$');
            if (fields.Length != 4 || fields[0] != "idb-pbkdf2-sha256-v1"
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
                || iterations is < 600_000 or > 2_000_000) return false;
            var salt = Convert.FromBase64String(fields[2]); var expected = Convert.FromBase64String(fields[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        catch (FormatException) { return false; }
    }
}

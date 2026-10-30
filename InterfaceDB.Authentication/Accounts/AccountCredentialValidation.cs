using System.Text;

namespace Idb.Authentication;

/// <summary>Input bounds shared by registration and password changes, independent of the injected hash algorithm.</summary>
public static class AccountCredentialValidation
{
    public static string NormalizeUsername(string username)
    {
        if (string.IsNullOrEmpty(username) || username.Length > 128
            || username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@')))
            throw new ArgumentException("Usernames must contain 1–128 ASCII letters, digits, '.', '-', '_' or '@'.");
        return username.ToLowerInvariant();
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12 || Encoding.UTF8.GetByteCount(password) > 1024)
            throw new ArgumentException("A password must contain at least 12 characters and at most 1024 UTF-8 bytes.");
    }
}

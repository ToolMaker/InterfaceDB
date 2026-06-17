using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Services.Encryption;

namespace Idb.Libraries.Implementation.Encryption;

/// <summary>
/// Stores encryption keys as generic credentials for the current Windows user.
/// </summary>
public sealed class WindowsCredentialEncryptionKeyStore : IEncryptionKeyStore
{
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;
    private readonly string _databaseName;
    private readonly string _targetPrefix;

    public WindowsCredentialEncryptionKeyStore(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("Database name cannot be null or whitespace.", nameof(databaseName));
        }

        EnsureWindows();
        _databaseName = databaseName.Trim();
        _targetPrefix = $"InterfaceDB/{_databaseName}/keys/";
    }

    public string GetActiveKeyId()
    {
        var bytes = ReadCredential(GetActiveTarget());
        var keyId = Encoding.UTF8.GetString(bytes);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new CryptographicException(
                $"The active encryption key identifier for database '{_databaseName}' is empty.");
        }

        return keyId;
    }

    public ReadOnlyMemory<byte> GetCurrentKey() => GetKey(GetActiveKeyId());

    public ReadOnlyMemory<byte> GetKey(string keyId)
    {
        ValidateKeyId(keyId);
        return ReadCredential(GetKeyTarget(keyId));
    }

    public void AddOrUpdateKey(string keyId, ReadOnlySpan<byte> key)
    {
        ValidateKeyId(keyId);
        ValidateAesKey(key);
        WriteCredential(GetKeyTarget(keyId), key);
    }

    public void SetActiveKey(string keyId)
    {
        ValidateKeyId(keyId);
        _ = GetKey(keyId);
        WriteCredential(GetActiveTarget(), Encoding.UTF8.GetBytes(keyId));
    }

    public bool RemoveKey(string keyId)
    {
        ValidateKeyId(keyId);
        if (string.Equals(GetActiveKeyIdOrDefault(), keyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The active encryption key cannot be removed.");
        }

        if (CredDeleteW(GetKeyTarget(keyId), CredentialType.Generic, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, $"Unable to remove encryption key '{keyId}'.");
    }

    /// <summary>
    /// Removes the active-key pointer without deleting any stored key material.
    /// </summary>
    public bool ClearActiveKey()
    {
        if (CredDeleteW(GetActiveTarget(), CredentialType.Generic, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, "Unable to clear the active encryption key.");
    }

    private string? GetActiveKeyIdOrDefault()
    {
        try
        {
            return GetActiveKeyId();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNotFound)
        {
            return null;
        }
    }

    private string GetActiveTarget() => $"InterfaceDB/{_databaseName}/active";

    private string GetKeyTarget(string keyId) => _targetPrefix + keyId;

    private static byte[] ReadCredential(string targetName)
    {
        if (!CredReadW(targetName, CredentialType.Generic, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Credential '{targetName}' could not be read.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void WriteCredential(string targetName, ReadOnlySpan<byte> value)
    {
        if (value.Length > MaximumCredentialBlobSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Windows Credential Manager values cannot exceed {MaximumCredentialBlobSize} bytes.");
        }

        var bytes = value.ToArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredentialType.Generic,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredentialPersistence.LocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWriteW(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Credential '{targetName}' could not be written.");
            }
        }
        finally
        {
            handle.Free();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("Key identifier cannot be null or whitespace.", nameof(keyId));
        }

        if (keyId.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("Key identifiers cannot contain path separators.", nameof(keyId));
        }
    }

    private static void ValidateAesKey(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new CryptographicException("AES-GCM requires a 128, 192, or 256-bit key.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager encryption keys are supported only on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public CredentialType Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersistence : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string target,
        CredentialType type,
        uint reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, CredentialType type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

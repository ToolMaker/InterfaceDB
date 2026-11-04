using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Commercial.Repositories;

namespace Idb.Transport.Repositories;

/// <summary>The complete public projection of an approved setup. Name is scoped by Manifest.ContractIdentity.
/// Never put server paths, secrets or backend options here.</summary>
public sealed record ManagedClientSetup(string Name, string LocalProviderId, RepositoryReadPreference ReadPreference,
    CompatibilityManifest Manifest, string RemoteAlias, string Revision)
{
    /// <summary>Optional group/account selector approved by the server. The access policy itself stays server-side.</summary>
    public string? DataScope { get; init; }

}

public sealed record ManagedClientSetupGrant(ManagedClientSetup Setup, string SubjectPartition, string Audience,
    DateTimeOffset ExpiresAt)
{
    public string DataScopeIdentity { get; init; } = "";
    public string? RequestedScope { get; init; }
}

public interface IManagedSetupSigner { byte[] Sign(ReadOnlySpan<byte> payload); }
public interface IManagedSetupVerifier { bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature); }

/// <summary>Server-only signing service. The caller owns the RSA key; protect its private key like other server credentials.</summary>
public sealed class RsaManagedSetupSigner : IManagedSetupSigner
{
    private readonly RSA _key;
    public RsaManagedSetupSigner(RSA key)
    {
        if (key.KeySize < 2048) throw new ArgumentException("Use an RSA key of at least 2048 bits.", nameof(key));
        _key = key;
    }
    public byte[] Sign(ReadOnlySpan<byte> payload)
    { lock (_key) return _key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
}

/// <summary>Application trust service, provisioned independently of the downloaded setup and cache.</summary>
public sealed class RsaManagedSetupVerifier : IManagedSetupVerifier
{
    private readonly byte[] _publicKey;
    public RsaManagedSetupVerifier(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
        if (read != subjectPublicKeyInfo.Length || key.KeySize < 2048) throw new ArgumentException("Invalid setup verification key.");
        _publicKey = subjectPublicKeyInfo.ToArray();
    }
    public bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(_publicKey, out _);
        return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}

public static class ManagedSetupProtocol
{
    public const int MaximumReceiptBytes = 32 * 1024;
    public static CompatibilityManifest Manifest { get; } = new()
    {
        ApiVersion = "1", StorageEngine = "InterfaceDB-setup", StorageFormat = "approved-client-settings-v2",
        EnvelopeFormat = "signed-json-v2", ContractIdentity = "InterfaceDB.ClientSetup", ContractVersion = "2.0.0",
        ContractSchemaHash = "client-setup-v2", ImplementationManifestHash = "client-setup-v2", IndexDefinitionHash = "none"
    };
    /// <summary>One setup endpoint per contract and profile, independent of the selected storage provider.</summary>
    public static string Alias(string contractIdentity, string setupName)
    {
        TransportValidation.ControlString(contractIdentity, false);
        TransportValidation.Alias(setupName);
        return "setup-" + Hash(JsonSerializer.Serialize(new[] { contractIdentity, setupName }));
    }
    public static CompatibilityManifest ClientManifest(CompatibilityManifest manifest) => manifest with
    {
        StorageEngine = "InterfaceDB-remote", StorageFormat = "opaque-envelope-v1", ApprovedBuildIdentity = null
    };
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string RequestKey(string contract, string? provider) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SetupRequest(contract, provider)));
    internal sealed record SetupRequest(string ContractIdentity, string? ProviderId);
    private sealed record SignedReceipt(byte[] Payload, byte[] Signature);
    internal static byte[] Sign(ManagedClientSetupGrant grant, IManagedSetupSigner signer)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(grant);
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new SignedReceipt(payload, signer.Sign(payload)));
        if (receipt.Length > MaximumReceiptBytes) throw new InvalidOperationException("The approved setup exceeds its size limit.");
        return receipt;
    }
    internal static ManagedClientSetupGrant Verify(byte[] receipt, IManagedSetupVerifier verifier)
    {
        try
        {
            if (receipt.Length > MaximumReceiptBytes) throw new InvalidDataException();
            var signed = JsonSerializer.Deserialize<SignedReceipt>(receipt) ?? throw new InvalidDataException();
            if (signed.Payload is null || signed.Signature is null || !verifier.Verify(signed.Payload, signed.Signature)) throw new InvalidDataException();
            return JsonSerializer.Deserialize<ManagedClientSetupGrant>(signed.Payload) ?? throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("The server setup approval is invalid."); }
    }
    internal static void Validate(ManagedClientSetup setup)
    {
        TransportValidation.Alias(setup.Name); TransportValidation.Alias(setup.LocalProviderId); TransportValidation.Alias(setup.RemoteAlias);
        TransportValidation.ControlString(setup.Revision, false); setup.Manifest.Validate();
        if (setup.DataScope is not null) TransportValidation.ControlString(setup.DataScope, false);
        if (!Enum.IsDefined(setup.ReadPreference) || setup.Manifest != ClientManifest(setup.Manifest))
            throw new ArgumentException("The setup must use the client-facing compatibility manifest.");
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Idb.Transport;

/// <summary>A host-approved text schema for a deployed contract. The schema describes data, not access permissions.</summary>
public sealed record ContractDescriptor(string ContractIdentity, string ContractVersion, string ContractSchemaHash,
    string MediaType, string Schema);

/// <summary>Supplies a descriptor for an explicitly registered repository. Called when the host starts.</summary>
public interface IContractDescriptorProvider
{
    ContractDescriptor? GetDescriptor(string storeAlias, CompatibilityManifest manifest);
}

/// <summary>The active compatibility manifest and its published schema.</summary>
public sealed record TcpContractDescription(CompatibilityManifest Manifest, ContractDescriptor Descriptor);

internal static class ContractDescriptorWire
{
    private const int MaximumPayloadBytes = 64 * 1024;
    internal const int MaximumFrameBytes = MaximumPayloadBytes + TransportValidation.MaximumHelloFrameBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(ContractDescriptor descriptor, CompatibilityManifest manifest)
    {
        Validate(descriptor, manifest);
        var payload = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        if (payload.Length > MaximumPayloadBytes)
            throw new ArgumentException("The encoded contract descriptor exceeds 64 KiB.", nameof(descriptor));
        return payload;
    }

    internal static ContractDescriptor Decode(EncodedStoreResponse? response, CompatibilityManifest manifest, TransportLimits limits)
    {
        if (response is not { Found: true, Payload: { Length: > 0 and <= MaximumPayloadBytes } payload,
            Records: null, NextAfterKey: null, AffectedCount: 0 } || payload.Length > limits.MaximumPayloadBytes)
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid contract description response.");
        try
        {
            var descriptor = JsonSerializer.Deserialize<ContractDescriptor>(payload)
                ?? throw new JsonException("Missing descriptor.");
            Validate(descriptor, manifest);
            return descriptor;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid contract descriptor.", exception);
        }
    }

    private static void Validate(ContractDescriptor descriptor, CompatibilityManifest manifest)
    {
        if (descriptor.ContractIdentity != manifest.ContractIdentity
            || descriptor.ContractVersion != manifest.ContractVersion
            || descriptor.ContractSchemaHash != manifest.ContractSchemaHash)
            throw new ArgumentException("The contract descriptor does not match the active manifest.", nameof(descriptor));
        TransportValidation.ControlString(descriptor.MediaType, false);
        if (!MediaTypeHeaderValue.TryParse(descriptor.MediaType, out var mediaType) || mediaType.MediaType!.Contains('*')
            || string.IsNullOrWhiteSpace(descriptor.Schema) || descriptor.Schema.Length > MaximumPayloadBytes
            || Utf8.GetByteCount(descriptor.Schema) > MaximumPayloadBytes)
            throw new ArgumentException("A concrete media type and a bounded UTF-8 schema are required.", nameof(descriptor));
    }
}

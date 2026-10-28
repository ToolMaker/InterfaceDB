using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Transport;

/// <summary>Optional write preflight. All validation finishes before any storage operation starts.</summary>
public interface IDataAccessWritePolicy : IDataAccessPolicy
{
    void ValidateWrite(DataAccessContext context, EncodedStoreRequest request);
}

/// <summary>Application-owned envelope decoding/validation seam; no storage-provider dependency.</summary>
public interface IAccessPolicyEnvelopeReader<TContract> where TContract : class
{
    TContract Read(string storeAlias, string logicalKey, byte[] payload);
}

/// <summary>Uses the configured storage serializer once, without reserializing the validated payload.</summary>
public sealed class AccessPolicyEnvelopeReader<TContract>(IObjectSerializer<TContract> serializer,
    IDbContractConfigDto<TContract> contract, int maximumPayloadBytes = 4 * 1024 * 1024)
    : IAccessPolicyEnvelopeReader<TContract> where TContract : class, IStoredContract<TContract>
{
    private readonly EncodedEnvelopeSerializer<TContract> _codec = new(serializer, maximumPayloadBytes);
    public TContract Read(string storeAlias, string logicalKey, byte[] payload)
    {
        var envelope = _codec.Deserialize(logicalKey, payload, serializer is IContextualObjectSerializer<TContract>
            ? StorageEncryptionContext.Create(contract, "tcp-repository-v1:" + storeAlias, logicalKey) : []);
        if (envelope.VersionTag != contract.ActiveVersion || envelope.ObjectInstance is null)
            throw new InvalidDataException("The envelope does not match the deployed contract version.");
        return envelope.ObjectInstance;
    }
}

/// <summary>
/// ANDs interface-declared ownership criteria. Claims establish the allowed values; writes must
/// contain those exact values. Reads select the resulting physical partition without decoding rows.
/// Multiple criteria use a JSON RequestedScope object keyed by property name when selection is needed.
/// </summary>
public sealed class ContractDataAccessPolicy<TContract> : IDataAccessWritePolicy where TContract : class
{
    private sealed record Criterion(string Property, string Claim, Func<TContract, string?> Read);
    private readonly Criterion[] _criteria;
    private readonly IAccessPolicyEnvelopeReader<TContract> _reader;
    private readonly IReadOnlyDictionary<string, string> _fixed;
    private readonly string _schema;
    public EncodedStoreCapabilities AllowedCapabilities { get; }

    public ContractDataAccessPolicy(IAccessPolicyEnvelopeReader<TContract> reader,
        IReadOnlyDictionary<string, string>? selectedValues = null,
        EncodedStoreCapabilities allowedCapabilities = EncodedStoreCapabilities.All)
    {
        ArgumentNullException.ThrowIfNull(reader); _reader = reader;
        if (!typeof(TContract).IsInterface) throw new ArgumentException("Access criteria belong on a stored interface.");
        TransportValidation.Capabilities(allowedCapabilities); AllowedCapabilities = allowedCapabilities;
        var declarations = new[] { typeof(TContract) }.Concat(typeof(TContract).GetInterfaces())
            .SelectMany(type => type.GetProperties()).Distinct().ToArray();
        if (declarations.GroupBy(p => p.Name, StringComparer.Ordinal).Any(group => group.Count() > 1
            && group.Any(p => p.GetCustomAttribute<StorageAccessClaimAttribute>() is not null)))
            throw new ArgumentException("An access criterion cannot be shadowed by another interface property.");
        var properties = declarations.Where(p => p.GetCustomAttribute<StorageAccessClaimAttribute>() is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        if (properties.Length is < 1 or > 8 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new ArgumentException("Declare one to eight unambiguous access criteria.");
        _criteria = properties.Select(property =>
        {
            var claim = property.GetCustomAttribute<StorageAccessClaimAttribute>()!.ClaimType;
            TransportValidation.ControlString(claim, false);
            if (property.PropertyType != typeof(string) || property.GetMethod is null || property.GetIndexParameters().Length != 0
                || property.GetCustomAttribute<NotSearchIndexedAttribute>() is not null
                || property.GetCustomAttribute<EncryptedStorageFieldAttribute>() is not null)
                throw new ArgumentException("Access criteria require readable, indexed, unencrypted string properties.");
            var item = Expression.Parameter(typeof(TContract));
            var getter = Expression.Lambda<Func<TContract, string?>>(Expression.Property(
                Expression.Convert(item, property.DeclaringType!), property), item).Compile(!RuntimeFeature.IsDynamicCodeSupported);
            return new Criterion(property.Name, claim, getter);
        }).ToArray();
        var fixedValues = new Dictionary<string, string>(selectedValues ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (property, value) in fixedValues)
        {
            if (!_criteria.Any(c => c.Property == property)) throw new ArgumentException("Unknown fixed access criterion.");
            TransportValidation.ControlString(value, false);
        }
        _fixed = fixedValues;
        _schema = JsonSerializer.Serialize(new object[] { "contract-claims-v1", typeof(TContract).FullName!,
            _criteria.Select(c => new[] { c.Property, c.Claim }).ToArray() });
        if (System.Text.Encoding.UTF8.GetByteCount(_schema) > 3000) throw new ArgumentException("Access declaration is too large.");
    }

    public DataAccessScope? Authorize(DataAccessContext context)
    {
        var resolved = Resolve(context);
        return resolved is null ? null : DataAccessScope.Partitioned(
            JsonSerializer.Serialize(new[] { _schema, resolved.Value.Issuer }), JsonSerializer.Serialize(resolved.Value.Values));
    }

    private (string Issuer, string[] Values)? Resolve(DataAccessContext context)
    {
        if ((context.RequiredCapabilities & ~AllowedCapabilities) != 0
            || !DataAccessClaims.TrySubject(context.Identity, out var issuer, out _)) return null;
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context.RequestedScope is { } selector)
        {
            if (_criteria.Length == 1) requested.Add(_criteria[0].Property, selector);
            else
            {
                try
                {
                    using var json = JsonDocument.Parse(selector);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
                    foreach (var value in json.RootElement.EnumerateObject())
                        if (value.Value.ValueKind != JsonValueKind.String || !_criteria.Any(c => c.Property == value.Name)
                            || !requested.TryAdd(value.Name, value.Value.GetString()!)) return null;
                }
                catch (JsonException) { return null; }
            }
        }
        var values = new string[_criteria.Length];
        for (int i = 0; i < _criteria.Length; i++)
        {
            var criterion = _criteria[i];
            var memberships = DataAccessClaims.Memberships(context.Identity!, criterion.Claim);
            if (memberships is null) return null;
            _fixed.TryGetValue(criterion.Property, out var pinned);
            requested.TryGetValue(criterion.Property, out var selected);
            if (pinned is not null && selected is not null && pinned != selected) return null;
            selected = pinned ?? selected ?? (memberships.Count == 1 ? memberships.Single() : null);
            if (string.IsNullOrWhiteSpace(selected) || !memberships.Contains(selected)
                || System.Text.Encoding.UTF8.GetByteCount(selected) > 256) return null;
            values[i] = selected;
        }
        return (issuer, values);
    }

    public void ValidateWrite(DataAccessContext context, EncodedStoreRequest request)
    {
        var resolved = Resolve(context) ?? throw new UnauthorizedAccessException("The ownership criteria are not authorized.");
        void Validate(string key, byte[]? payload)
        {
            if (payload is null) return;
            if (key == ".idb-remote-repository-clock-v1")
            {
                if (payload.Length != 16 || BinaryPrimitives.ReadInt64LittleEndian(payload) < 0
                    || BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8)) != ~BinaryPrimitives.ReadInt64LittleEndian(payload))
                    throw new InvalidDataException("Invalid repository revision clock.");
                return;
            }
            var item = _reader.Read(context.StoreAlias, key, payload);
            for (int i = 0; i < _criteria.Length; i++)
                if (!string.Equals(_criteria[i].Read(item), resolved.Values[i], StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("A stored ownership field does not match the authorized partition.");
        }
        if (request.Command == EncodedStoreCommand.Put) Validate(request.Key!, request.Payload);
        else if (request.Command == EncodedStoreCommand.ApplyBatch)
            foreach (var mutation in request.Mutations!) Validate(mutation.Key, mutation.Payload);
    }
}

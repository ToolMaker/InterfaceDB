using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Identifies an application copy inside the logical resilient repository without exposing
/// either endpoint's physical identity or synchronization metadata.
/// </summary>
internal sealed class ResilientApplicationEnvelope<TContract> : IObjectEnvelope<TContract>
    where TContract : class, IStoredContract<TContract>
{
    internal ResilientApplicationEnvelope(
        string storageKey,
        string versionTag,
        TContract objectInstance)
    {
        OriginalStorageKey = storageKey;
        EnvelopeCacheKey = storageKey;
        VersionTag = versionTag;
        ObjectInstance = objectInstance;
        ImplementationTypeName = objectInstance.GetType().AssemblyQualifiedName;
    }

    /// <summary>
    /// Retains the repository-issued logical identity even if application code edits the
    /// mutable compatibility properties exposed by <see cref="IObjectEnvelope{TContract}"/>.
    /// </summary>
    internal string OriginalStorageKey { get; }

    public string EnvelopeCacheKey { get; set; }

    public string VersionTag { get; set; }

    public long RevisionTimestampUtcMicroseconds { get; set; }

    public TContract? ObjectInstance { get; set; }

    public WeakReference<TContract>? ObjectInstanceWeakReference { get; set; }

    public string? ImplementationTypeName { get; set; }

    public bool CacheObjectInstance { get; set; }

    public IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }
        = new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);

    public byte[]? ExtensionMetadata { get; set; }

    public string? Location { get; set; }

    public StorageObjectState State { get; set; } = StorageObjectState.Active;
}

/// <summary>
/// Attaches a logical envelope while supporting the public or non-public setter shape required
/// by InterfaceDB stored contracts.
/// </summary>
internal static class ResilientStoredContractEnvelopeAccessor<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private static readonly ConcurrentDictionary<Type, Action<object, IObjectEnvelope<TContract>?>> Setters = new();

    internal static void SetEnvelope(TContract entity, IObjectEnvelope<TContract>? envelope)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var setter = Setters.GetOrAdd(entity.GetType(), CreateSetter);
        setter(entity, envelope);
    }

    private static Action<object, IObjectEnvelope<TContract>?> CreateSetter(Type runtimeType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var envelopeType = typeof(IObjectEnvelope<TContract>);
        var candidates = runtimeType
            .GetProperties(flags)
            .Where(property => property.PropertyType == envelopeType
                && property.GetSetMethod(nonPublic: true) is not null)
            .ToArray();
        var property = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, nameof(IStoredContract<TContract>.Envelope), StringComparison.Ordinal))
            ?? candidates.SingleOrDefault();
        if (property is null)
        {
            throw new InvalidOperationException(
                $"Type '{runtimeType.FullName}' must expose a settable '{envelopeType.Name}' property. "
                + "Use a public or non-public Envelope setter.");
        }

        var setter = property.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                $"Type '{runtimeType.FullName}' must expose a setter for '{property.Name}'.");
        var instanceParameter = Expression.Parameter(typeof(object), "instance");
        var envelopeParameter = Expression.Parameter(envelopeType, "envelope");
        var call = Expression.Call(
            Expression.Convert(instanceParameter, runtimeType),
            setter,
            envelopeParameter);
        return Expression
            .Lambda<Action<object, IObjectEnvelope<TContract>?>>(
                call,
                instanceParameter,
                envelopeParameter)
            .Compile();
    }
}

using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Provides helper methods to wrap and unwrap contracts into envelopes while avoiding circular references.
/// </summary>
public class EnvelopeLifecycle<TContract> : IEnvelopeLifecycle<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly Func<IObjectEnvelope<TContract>> _envelopeFactory;
    private readonly IEnvelopeStorageIdentityFactory<TContract> _storageIdentityFactory;

    public EnvelopeLifecycle(
        Func<IObjectEnvelope<TContract>> envelopeFactory,
        IEnvelopeStorageIdentityFactory<TContract> storageIdentityFactory)
    {
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _storageIdentityFactory = storageIdentityFactory ?? throw new ArgumentNullException(nameof(storageIdentityFactory));
    }

    public ValueTask<IObjectEnvelope<TContract>> WrapAsync(TContract entity)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        IObjectEnvelope<TContract> wrappedEnvelope;

        if (entity.Envelope == null)
        {
            wrappedEnvelope = _envelopeFactory()
                ?? throw new InvalidOperationException("Envelope factory returned null.");
        }
        else
        {
            wrappedEnvelope = entity.Envelope;
        }

        StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope: null);
        wrappedEnvelope.ObjectInstance = entity;
        _storageIdentityFactory.InitEnvelope(wrappedEnvelope);
        
        return ValueTask.FromResult(wrappedEnvelope ?? throw new InvalidOperationException("Failed to resolve envelope instance from service provider."));
    }

    public TContract Unwrap(IObjectEnvelope<TContract> envelope)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        var entity = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope does not contain an object instance.");

        StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
        return entity;
    }

    //private IObjectEnvelope<TContract, string> CreateEnvelope(
    //string file,
    //byte[] payload,
    //string? versionOverride = null)
    //{
    //    var version = !string.IsNullOrWhiteSpace(versionOverride)
    //        ? versionOverride!
    //        : _contractConfig.ActiveVersion ?? "1.0.0";
    //    var envalope = _serviceProvider.GetService<IObjectEnvelope<TContract, string>>();
    //    envalope.Location = file;
    //    envalope.VersionTag = version;

    //    return envalope;
    //}
}

internal static class StoredContractEnvelopeAccessor<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private static readonly ConcurrentDictionary<Type, Action<object, IObjectEnvelope<TContract>?>> Setters = new();

    internal static void SetEnvelope(TContract entity, IObjectEnvelope<TContract>? envelope)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var setter = Setters.GetOrAdd(entity.GetType(), CreateSetter);
        setter(entity, envelope);
    }

    private static Action<object, IObjectEnvelope<TContract>?> CreateSetter(Type runtimeType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var envelopeType = typeof(IObjectEnvelope<TContract>);

        var candidates = runtimeType
            .GetProperties(flags)
            .Where(property => property.PropertyType == envelopeType && property.GetSetMethod(nonPublic: true) is not null)
            .ToArray();

        var propertyInfo = candidates.FirstOrDefault(property => string.Equals(property.Name, "Envelope", StringComparison.Ordinal))
            ?? candidates.SingleOrDefault();

        if (propertyInfo is null)
        {
            throw new InvalidOperationException(
                $"Type '{runtimeType.FullName}' must expose a settable '{envelopeType.Name}' property. " +
                "Use 'public IObjectEnvelope<TContract>? Envelope { get; private set; }' or an equivalent non-public setter.");
        }

        var setter = propertyInfo.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                $"Type '{runtimeType.FullName}' must expose a setter for '{propertyInfo.Name}'.");

        var instanceParameter = Expression.Parameter(typeof(object), "instance");
        var valueParameter = Expression.Parameter(envelopeType, "value");
        var call = Expression.Call(
            Expression.Convert(instanceParameter, runtimeType),
            setter,
            valueParameter);

        return Expression
            .Lambda<Action<object, IObjectEnvelope<TContract>?>>(call, instanceParameter, valueParameter)
            .Compile();
    }
}


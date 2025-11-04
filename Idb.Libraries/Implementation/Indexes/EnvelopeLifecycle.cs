using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Provides helper methods to wrap and unwrap contracts into envelopes while avoiding circular references.
/// </summary>
public class EnvelopeLifecycle<TContract> : IEnvelopeLifecycle<TContract>
    where TContract : class, IRepoDto<TContract>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnvelopeKeyAndLocationFactory<TContract> _fileNameFactory;

    public EnvelopeLifecycle(IServiceProvider serviceProvider, 
        IEnvelopeKeyAndLocationFactory<TContract> fileNameFactory)
    {
        _serviceProvider = serviceProvider;
        _fileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
    }

    public async ValueTask<IObjectEnvelope<TContract>> WrapAsync(TContract entity)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        IObjectEnvelope<TContract> wrappedEnvelope;

        if (entity.Envelope == null)
        {
            wrappedEnvelope = _serviceProvider.GetService<IObjectEnvelope<TContract>>()
                ?? throw new InvalidOperationException("Failed to resolve envelope instance from service provider."); ;
        }
        else
        {
            wrappedEnvelope = entity.Envelope;
        }

        entity.Envelope = null;
        wrappedEnvelope.ObjectInstance = entity;
        _fileNameFactory.InitEnvelope(wrappedEnvelope);
        
        return wrappedEnvelope ?? throw new InvalidOperationException("Failed to resolve envelope instance from service provider.");
    }

    public TContract Unwrap(IObjectEnvelope<TContract> envelope)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        var entity = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope does not contain an object instance.");

        entity.Envelope = envelope;
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


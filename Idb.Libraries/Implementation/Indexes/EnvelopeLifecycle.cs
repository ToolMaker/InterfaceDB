using System;
using System.IO;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Provides helper methods to wrap and unwrap contracts into envelopes while avoiding circular references.
/// </summary>
internal static class EnvelopeLifecycle
{
    public static async ValueTask<IObjectEnvelope<TContract, FileInfo>> WrapAsync<TContract>(
        TContract entity,
        Func<TContract, ValueTask<IObjectEnvelope<TContract, FileInfo>>> action)
        where TContract : class, IRepoDto<TContract, FileInfo>
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var previousEnvelope = entity.Envelope;
        entity.Envelope = null;

        try
        {
            return await action(entity).ConfigureAwait(false);
        }
        finally
        {
            if (entity.Envelope is null)
            {
                entity.Envelope = previousEnvelope;
            }
        }
    }

    public static TContract Unwrap<TContract>(IObjectEnvelope<TContract, FileInfo> envelope)
        where TContract : class, IRepoDto<TContract, FileInfo>
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
}


using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

public sealed class StrictTimestampMatchStrategy<TContract> : IWriteConflictStrategy<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public ValueTask<EnvelopeWriteDecision<TContract>> DecideAsync(EnvelopeWriteConflict<TContract> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EnvelopeWriteDecision<TContract>(context.IsInsert
            ? context.CurrentRevision is null && context.ExpectedRevision == 0
            : context.CurrentRevision is long current && current == context.ExpectedRevision));
    }
}

using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

/// <summary>A notification callback tried to turn a cache observation into another command on its source.</summary>
public sealed class RepositoryFeedbackLoopException : InvalidOperationException
{
    public RepositoryFeedbackLoopException()
        : base("Change callbacks may read this composed repository, but cannot save, delete or resolve conflicts on it. Issue writes from an explicit application command.") { }
}

public sealed partial class ResilientRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    private void ThrowIfCompositionFeedbackWrite(ResilientRepositoryOperation operation)
    {
        if (!_compositionChanges.IsDispatchingCallback) return;
        var exception = new RepositoryFeedbackLoopException();
        ReportFailure(null, operation, null, exception);
        throw exception;
    }
}

using Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Backward-compatible alias for the flat file-system layout.
/// </summary>
public sealed class DefaultFileNameFactory<TContract> : FlatFileNameFactory<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public DefaultFileNameFactory(IDbContractConfigDto<TContract> config)
        : base(config)
    {
    }
}

namespace Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Backwards-compatible alias for the original envelope-bearing storage contract abstraction.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the DTO payload.</typeparam>
public interface IRepoDto<TContract> : IStoredContract<TContract>
    where TContract : class, IStoredContract<TContract>
{
}

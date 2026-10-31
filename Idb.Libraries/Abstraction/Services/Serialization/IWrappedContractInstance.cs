namespace Idb.Libraries.Abstractions.Services.Serialization;

// Internal capability shared by adapters, indexes and cache policy. The backing
// implementation is for metadata/index discovery; caller reads use a materialized copy.
internal interface IWrappedContractInstance<TContract>
{
    TContract Implementation { get; }
    TContract CreateImplementationCopy();
}

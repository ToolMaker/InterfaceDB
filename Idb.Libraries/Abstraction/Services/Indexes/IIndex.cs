namespace Idb.Libraries.Abstractions.Services.Indexes;

public interface IIndex<TContract>
    where TContract : class
{
    string Name { get; }
    IndexTypeEnum IndexType { get; }
    IReadOnlyDictionary<string, TContract> Settings { get; }
}


namespace Idb.Libraries.Abstraction.Services.Cache;

public interface IEnvelopeDictionaryCaheManager<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
{
    int GetGroupCount(TKey groupKey);

    IReadOnlyList<TValue> GetGroup(TKey groupKey);

    bool TryGetGroup(TKey groupKey, out IReadOnlyList<TValue> values);

    void AddOrUpdate(TKey physicalKey, TKey groupKey, TValue value);
}

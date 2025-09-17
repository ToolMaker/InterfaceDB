using TransactionManager.Abstraction.Services;

namespace TransactionManager.Services
{
    public sealed class ScopeService : ILocalScopeService
    {
        private readonly IReadOnlyDictionary<Type, DirectoryInfo> _map;
        private readonly IReadOnlyDictionary<string, Type> _reverse;

        public ScopeService(IReadOnlyDictionary<Type, DirectoryInfo> map)
        {
            _map = map;
            _reverse = map.ToDictionary(k => k.Value.FullName, k => k.Key);
        }
        public DirectoryInfo? GetDirectory<t>() => _map.TryGetValue(typeof(t), out var d) ? d : null;
        public Type? GetTypeByDirectory(DirectoryInfo d) => _reverse.TryGetValue(d.FullName, out var t) ? t : null;
    }
}

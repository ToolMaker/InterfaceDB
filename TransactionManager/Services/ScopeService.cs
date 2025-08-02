using System.Collections;
using TransactionManager.Abstraction.Services;
using TransactionManager.Attributes;

namespace TransactionManager.Services
{

    internal sealed class ScopeService : IScopeService
    {
        private readonly IReadOnlyDictionary<Type, DirectoryInfo> _map;
        private readonly IReadOnlyDictionary<string, Type> _reverse;

        public ScopeService(IReadOnlyDictionary<Type, DirectoryInfo> map)
        {
            _map = map;
            _reverse = map.ToDictionary(kvp => kvp.Value.FullName, kvp => kvp.Key);
        }

        public IEnumerable<DirectoryInfo> GetScopeDirectories<T>()
        {
            var type = typeof(T);
            var list = new List<DirectoryInfo>();

            // 1. root for the concrete type (or interface) itself
            if (_map.TryGetValue(type, out var root))
                list.Add(root);

            // 2. add “/base/<BaseType>” if the base class is [StorageScope]
            if (type.IsClass &&
                type.BaseType is { } baseType &&
                baseType.IsDefined(typeof(StorageScopeAttribute), false) &&
                _map.TryGetValue(baseType, out var baseRoot))
            {
                list.Add(new DirectoryInfo(
                    Path.Combine(root.FullName, "base", baseType.Name)));
            }

            // 3. add interface roots *only for the interfaces T actually implements*
            foreach (var iface in type.GetInterfaces()
                                      .Where(i => i.IsDefined(typeof(StorageScopeAttribute), false)))
            {
                if (_map.TryGetValue(iface, out var ifaceRoot))
                    list.Add(ifaceRoot);
            }

            return list.Distinct().ToList();
        }


        public Type? GetScopeType(DirectoryInfo path)
            => _reverse.TryGetValue(path.FullName, out var t) ? t : null;
    }

}

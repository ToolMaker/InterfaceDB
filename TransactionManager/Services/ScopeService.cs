using System;
using System.Collections.Generic;
using System.IO;
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

            foreach (var dir in map.Values)
            {
                EnsureDirectory(dir);
            }

            _reverse = map.ToDictionary(k => k.Value.FullName,
                                        k => k.Key,
                                        StringComparer.OrdinalIgnoreCase);
        }

        public DirectoryInfo? GetDirectory<T>()
        {
            if (_map.TryGetValue(typeof(T), out var directory))
            {
                EnsureDirectory(directory);
                directory.Refresh();
                return directory;
            }

            return null;
        }

        public Type? GetTypeByDirectory(DirectoryInfo directory)
        {
            var key = directory.FullName;
            return _reverse.TryGetValue(key, out var type) ? type : null;
        }

        public IEnumerable<KeyValuePair<Type, DirectoryInfo>> EnumerateScopes()
        {
            foreach (var pair in _map)
            {
                yield return pair;
            }
        }

        private static void EnsureDirectory(DirectoryInfo directory)
        {
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
            }
        }
    }
}

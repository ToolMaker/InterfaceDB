using System;
using System.Collections.Generic;
using System.IO;
using TransactionManager.Abstraction.Services;
using TransactionManager.Dtos;

namespace TransactionManager.Services
{
    public sealed class ScopeService : ILocalScopeService
    {
        private readonly IReadOnlyDictionary<Type, ScopeDescriptor> _scopes;
        private readonly IReadOnlyDictionary<string, Type> _reverse;

        public ScopeService(IReadOnlyDictionary<Type, ScopeDescriptor> scopes)
        {
            _scopes = scopes;

            foreach (var descriptor in scopes.Values)
            {
                EnsureDirectory(descriptor.Directory);
            }

            _reverse = scopes.Values.ToDictionary(d => d.Directory.FullName,
                                                  d => d.ContractType,
                                                  StringComparer.OrdinalIgnoreCase);
        }

        public DirectoryInfo? GetDirectory<TContract>()
            => GetScope<TContract>()?.Directory;

        public ScopeDescriptor? GetScope<TContract>()
            => GetScope(typeof(TContract));

        public ScopeDescriptor? GetScope(Type contractType)
        {
            if (_scopes.TryGetValue(contractType, out var descriptor))
            {
                EnsureDirectory(descriptor.Directory);
                descriptor.Directory.Refresh();
                return descriptor;
            }

            return null;
        }

        public Type? GetContractByDirectory(DirectoryInfo dir)
        {
            var key = dir.FullName;
            return _reverse.TryGetValue(key, out var contract) ? contract : null;
        }

        public IEnumerable<ScopeDescriptor> GetScopes()
            => _scopes.Values;

        private static void EnsureDirectory(DirectoryInfo directory)
        {
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
            }
        }
    }
}

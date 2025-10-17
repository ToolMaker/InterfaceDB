using System.Collections.Generic;
using Idb.Libraries.Abstractions.Services.Repository.FileRepository;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Implementation.Dtos;

namespace Idb.Libraries.Implementation.Services
{
    public sealed class ScopeService : ILocalScopeService
    {
        private readonly IReadOnlyDictionary<Type, IScopeDescriptor> _scopes;
        private readonly IReadOnlyDictionary<string, Type> _reverse;

        public ScopeService(IReadOnlyDictionary<Type, IScopeDescriptor> scopes)
        {
            _scopes = scopes;

            foreach (var descriptor in scopes.Values)
            {
                EnsureDirectory(descriptor.Directory);
            }

                        var reverse = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in scopes.Values)
            {
                var key = descriptor.Directory.FullName;
                if (!reverse.ContainsKey(key))
                {
                    reverse.Add(key, descriptor.ContractType);
                }
            }

            _reverse = reverse;
        }

        public DirectoryInfo? GetDirectory<TContract>()
            => GetScope<TContract>()?.Directory;

        public IScopeDescriptor? GetScope<TContract>()
            => GetScope(typeof(TContract));

        public IScopeDescriptor? GetScope(Type contractType)
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

        public IEnumerable<IScopeDescriptor> GetScopes()
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


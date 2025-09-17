using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TransactionManager.Attributes;
using TransactionManager.Dtos;

namespace TransactionManager.Builders
{
    public sealed class ScopeTreeBuilder
    {
        private readonly IReadOnlyCollection<Assembly> _assemblies;
        private readonly DirectoryInfo _root;

        public ScopeTreeBuilder(IEnumerable<Assembly> assemblies, DirectoryInfo root)
        {
            _assemblies = assemblies.Distinct().ToArray();
            _root = root;
        }

        public IReadOnlyDictionary<Type, ScopeDescriptor> Build()
        {
            if (!_root.Exists)
            {
                Directory.CreateDirectory(_root.FullName);
                _root.Refresh();
            }

            var allTypes = _assemblies.SelectMany(GetLoadableTypes).ToArray();

            var scopedInterfaces = allTypes
                .Where(t => t.IsInterface && t.IsDefined(typeof(StorageScopeAttribute), inherit: false))
                .ToArray();

            var candidateImplementations = allTypes
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToArray();

            var descriptors = new Dictionary<Type, ScopeDescriptor>();

            foreach (var contract in scopedInterfaces)
            {
                var implementations = candidateImplementations
                    .Where(contract.IsAssignableFrom)
                    .ToArray();

                var directoryPath = Path.Combine(_root.FullName, contract.Name);
                var directory = new DirectoryInfo(directoryPath);

                if (!directory.Exists)
                {
                    Directory.CreateDirectory(directory.FullName);
                    directory.Refresh();
                }

                var versionTag = ResolveVersionTag(contract);

                descriptors[contract] = new ScopeDescriptor(contract,
                                                             directory,
                                                             implementations,
                                                             versionTag);
            }

            return descriptors;
        }

        private static string ResolveVersionTag(Type contract)
        {
            var attribute = contract.GetCustomAttribute<StorageScopeAttribute>(inherit: false);
            var tag = attribute?.VersionTag;

            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = "1.0";
            }

            if (tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new FormatException($"Version tag '{tag}' declared on '{contract.FullName}' contains invalid path characters.");
            }

            if (tag.Contains(Path.DirectorySeparatorChar) || tag.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new FormatException($"Version tag '{tag}' declared on '{contract.FullName}' cannot contain directory separators.");
            }

            return tag;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
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

        private static readonly ConcurrentDictionary<AssemblySetKey, IReadOnlyList<ScopeMetadata>> MetadataCache = new();

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

            var key = AssemblySetKey.Create(_assemblies);
            var metadata = MetadataCache.GetOrAdd(key, _ => BuildMetadata(_assemblies));

            var descriptors = new Dictionary<Type, ScopeDescriptor>(metadata.Count);

            foreach (var meta in metadata)
            {
                var directoryPath = Path.Combine(_root.FullName, meta.ContractType.Name);
                var directory = new DirectoryInfo(directoryPath);

                if (!directory.Exists)
                {
                    Directory.CreateDirectory(directory.FullName);
                    directory.Refresh();
                }

                descriptors[meta.ContractType] = new ScopeDescriptor(
                    meta.ContractType,
                    directory,
                    meta.Implementations,
                    meta.VersionTag);
            }

            return descriptors;
        }

        private static IReadOnlyList<ScopeMetadata> BuildMetadata(IReadOnlyCollection<Assembly> assemblies)
        {
            var allTypes = assemblies.SelectMany(GetLoadableTypes).ToArray();

            var scopedInterfaces = allTypes
                .Where(t => t.IsInterface && t.IsDefined(typeof(StorageScopeAttribute), inherit: false))
                .ToArray();

            var candidateImplementations = allTypes
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToArray();

            var list = new List<ScopeMetadata>(scopedInterfaces.Length);

            foreach (var contract in scopedInterfaces)
            {
                var implementations = candidateImplementations
                    .Where(contract.IsAssignableFrom)
                    .ToArray();

                var versionTag = ResolveVersionTag(contract);

                list.Add(new ScopeMetadata(contract, versionTag, implementations));
            }

            return list;
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

        private readonly record struct ScopeMetadata(Type ContractType, string VersionTag, Type[] Implementations);

        private readonly record struct AssemblySetKey(string[] AssemblyNames)
        {
            public static AssemblySetKey Create(IEnumerable<Assembly> assemblies)
            {
                var names = assemblies
                    .Where(a => !a.IsDynamic)
                    .Select(a => a.FullName ?? a.GetName().Name ?? string.Empty)
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                return new AssemblySetKey(names);
            }

            public bool Equals(AssemblySetKey other)
                => AssemblyNames.AsSpan().SequenceEqual(other.AssemblyNames);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                foreach (var name in AssemblyNames)
                {
                    hash.Add(name, StringComparer.Ordinal);
                }
                return hash.ToHashCode();
            }
        }
    }
}

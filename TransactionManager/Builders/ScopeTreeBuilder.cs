using System.Reflection;
using TransactionManager.Attributes;

namespace TransactionManager.Builders
{

    public sealed class ScopeStorageBuilder
    {
        public IEnumerable<Assembly> AssembliesToScan { get; set; } = AppDomain.CurrentDomain.GetAssemblies();
    }
    /// <summary>
    /// Scans assemblies for [StorageScope] types and resolves
    ///   • interface roots
    ///   • class/struct roots
    ///   • “base/” chains for class inheritance
    /// Returns a map Type ➡ absolute directory path.
    /// </summary>
    public sealed class ScopeTreeBuilder
    {
        private readonly IReadOnlyCollection<Assembly> _assemblies;
        private readonly char _dirSep = Path.DirectorySeparatorChar;

        public ScopeTreeBuilder(IEnumerable<Assembly> assembliesToScan)
        {
            _assemblies = assembliesToScan.Distinct().ToArray();
        }

        public IReadOnlyDictionary<Type, DirectoryInfo> Build()
        {
            var map = new Dictionary<Type, DirectoryInfo>();

            foreach (var type in _assemblies.SelectMany(a => a.GetTypes())
                                            .Where(t => t.IsDefined(typeof(StorageScopeAttribute), false)))
            {
                var rootPath = new DirectoryInfo($"{_dirSep}{type.Name}");
                map[type] = rootPath;

                // Rule 3: inheritance chain
                if (type.IsClass && type.BaseType is { } baseType &&
                    baseType.IsDefined(typeof(StorageScopeAttribute), false))
                {
                    var basePath = new DirectoryInfo(
                        $"{rootPath.FullName}{_dirSep}base{_dirSep}{baseType.Name}");
                    map[baseType] = basePath;            // ensures base is mapped too
                }
            }

            return map;
        }
    }

}

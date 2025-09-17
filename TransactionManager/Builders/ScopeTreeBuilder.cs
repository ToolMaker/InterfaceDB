using System.IO;
using System.Linq;
using System.Reflection;
using TransactionManager.Attributes;

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

        public IReadOnlyDictionary<Type, DirectoryInfo> Build()
        {
            if (!_root.Exists)
            {
                Directory.CreateDirectory(_root.FullName);
                _root.Refresh();
            }

            return _assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsDefined(typeof(StorageScopeAttribute), false)
                            && !t.IsAbstract && !t.IsInterface)
                .ToDictionary(t => t, CreateDirectoryForType);
        }

        private DirectoryInfo CreateDirectoryForType(Type type)
        {
            var path = Path.Combine(_root.FullName, type.Name);
            var dir = new DirectoryInfo(path);

            if (!dir.Exists)
            {
                Directory.CreateDirectory(dir.FullName);
                dir.Refresh();
            }

            return dir;
        }
    }
}

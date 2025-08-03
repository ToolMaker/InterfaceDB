using System.Reflection;
using TransactionManager.Attributes;
using TransactionManager.Attributes.TransactionManager.Attributes;

namespace TransactionManager.Builders
{
    public sealed class ScopeTreeBuilder
    {
        private readonly IReadOnlyCollection<Assembly> _assemblies;
        private readonly char _sep = Path.DirectorySeparatorChar;

        public ScopeTreeBuilder(IEnumerable<Assembly> asms)
            => _assemblies = asms.Distinct().ToArray();

        public IReadOnlyDictionary<Type, DirectoryInfo> Build()
            => _assemblies.SelectMany(a => a.GetTypes())
                  .Where(t => t.IsDefined(typeof(StorageScopeAttribute), false)
                           && !t.IsAbstract && !t.IsInterface)
                  .ToDictionary(t => t,
                                t => new DirectoryInfo($"{_sep}{t.Name}"));
    }

}

using System.IO;
using System.Reflection;

namespace TransactionManager.Dtos
{
    public sealed class ScopeStorageBuilder
    {
        /// <summary>If null or empty, all loaded assemblies are scanned.</summary>
        public IEnumerable<Assembly>? AssembliesToScan { get; set; }

        /// <summary>Base directory that holds per-scope folders. Defaults to AppContext.BaseDirectory/Storage.</summary>
        public DirectoryInfo? RootDirectory { get; set; }
    }
}

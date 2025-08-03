using System.Reflection;

namespace TransactionManager.Dtos
{
    public sealed class ScopeStorageBuilder
    {
        /// <summary>If null or empty, all loaded assemblies are scanned.</summary>
        public IEnumerable<Assembly>? AssembliesToScan { get; set; }
    }
}
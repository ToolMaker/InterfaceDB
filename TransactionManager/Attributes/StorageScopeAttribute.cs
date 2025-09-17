using System;

namespace TransactionManager.Attributes
{
    /// <summary>
    /// Marks an interface contract as an on-disk storage scope.
    /// Concrete implementations are discovered via reflection at startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface,
                    Inherited = false, AllowMultiple = false)]
    public sealed class StorageScopeAttribute : Attribute
    {
    }
}

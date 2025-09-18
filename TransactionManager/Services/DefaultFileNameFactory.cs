using System;
using TransactionManager.Abstraction.Services;

namespace TransactionManager.Services
{
    /// <summary>
    /// Default implementation that returns the normalized identifier without modification.
    /// </summary>
    public sealed class DefaultFileNameFactory : IFileNameFactory
    {
        public string Create(object value, string normalizedId, Type contractType)
            => normalizedId;
    }
}

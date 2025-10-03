using System;
using Idb.Libraries.Abstractions.Services;

namespace Idb.Libraries.Implementation.Services
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

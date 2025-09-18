using System;

namespace TransactionManager.Abstraction.Services
{
    /// <summary>
    /// Produces UNIQUE storage file names (sans extension) for contract instances.
    /// </summary>
    public interface IFileNameFactory
    {
        /// <summary>
        /// Creates a file-name segment for the supplied instance.
        /// </summary>
        /// <param name="value">Concrete object being persisted.</param>
        /// <param name="normalizedId">Caller-supplied identifier normalized by the writer.</param>
        /// <param name="contractType">Contract interface associated with the scope.</param>
        /// <returns>File-name segment without extension.</returns>
        string Create(object value, string normalizedId, Type contractType);
    }
}

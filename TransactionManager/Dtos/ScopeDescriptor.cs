using System;
using System.Collections.Generic;
using System.IO;

namespace TransactionManager.Dtos
{
    public sealed class ScopeDescriptor
    {
        public ScopeDescriptor(Type contractType, DirectoryInfo directory, IReadOnlyCollection<Type> implementations)
        {
            ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Implementations = implementations ?? Array.Empty<Type>();
        }

        public Type ContractType { get; }
        public DirectoryInfo Directory { get; }
        public IReadOnlyCollection<Type> Implementations { get; }
    }
}

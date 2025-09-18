using System;
using System.Collections.Generic;
using System.IO;

namespace TransactionManager.Implementation.Dtos
{
    public sealed class ScopeDescriptor
    {
        public ScopeDescriptor(Type contractType,
                               DirectoryInfo directory,
                               IReadOnlyCollection<Type> implementations,
                               string versionTag,
                               Type fileNameFactoryType)
        {
            if (string.IsNullOrWhiteSpace(versionTag))
            {
                throw new ArgumentException("Version tag cannot be null or whitespace.", nameof(versionTag));
            }

            ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Implementations = implementations ?? Array.Empty<Type>();
            VersionTag = versionTag;
            FileNameFactoryType = fileNameFactoryType ?? throw new ArgumentNullException(nameof(fileNameFactoryType));
        }

        public Type ContractType { get; }
        public DirectoryInfo Directory { get; }
        public IReadOnlyCollection<Type> Implementations { get; }
        public string VersionTag { get; }
        public Type FileNameFactoryType { get; }
    }
}

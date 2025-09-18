using System;
using System.Collections.Generic;
using System.IO;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Services
{
    public interface ILocalScopeService
    {
        DirectoryInfo? GetDirectory<TContract>();
        ScopeDescriptor? GetScope<TContract>();
        ScopeDescriptor? GetScope(Type contractType);
        Type? GetContractByDirectory(DirectoryInfo dir);
        IEnumerable<ScopeDescriptor> GetScopes();
    }
}

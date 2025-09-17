using System;
using System.Collections.Generic;
using System.IO;

namespace TransactionManager.Abstraction.Services
{
    public interface ILocalScopeService
    {
        DirectoryInfo? GetDirectory<TType>();
        Type? GetTypeByDirectory(DirectoryInfo dir);
        IEnumerable<KeyValuePair<Type, DirectoryInfo>> EnumerateScopes();
    }

}

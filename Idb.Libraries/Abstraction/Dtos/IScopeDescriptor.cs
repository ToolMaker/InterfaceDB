
namespace Idb.Libraries.Abstractions.Dtos
{
    public interface IScopeDescriptor
    {
        Type ContractType { get; }
        DirectoryInfo Directory { get; }
        Type FileNameFactoryType { get; }
        IReadOnlyCollection<Type> Implementations { get; }
        string VersionTag { get; }
    }
}


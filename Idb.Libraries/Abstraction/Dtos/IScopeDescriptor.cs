
namespace Idb.Libraries.Abstractions.Dtos
{
    public interface IScopeDescriptor<TContract>
        where TContract : class
    {
        Type ContractType { get; }
        DirectoryInfo Directory { get; }
        Type FileNameFactoryType { get; }
        IReadOnlyCollection<Type> Implementations { get; }
        string VersionTag { get; }
    }
}


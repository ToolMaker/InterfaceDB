using System.Diagnostics.CodeAnalysis;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

internal static class ContractInstance<TContract> where TContract : class, IStoredContract<TContract>
{
    internal static TContract Implementation(TContract value)
        => value is IWrappedContractInstance<TContract> wrapped ? wrapped.Implementation : value;

    internal static bool Is<TImplementation>(TContract? value) where TImplementation : class, TContract
        => value is not null && Implementation(value) is TImplementation;

    internal static bool TryRead<TImplementation>(TContract? value, [NotNullWhen(true)] out TImplementation? result)
        where TImplementation : class, TContract
    {
        result = value as TImplementation;
        if (result is null && value is IWrappedContractInstance<TContract> wrapped && wrapped.Implementation is TImplementation)
            result = (TImplementation)wrapped.CreateImplementationCopy();
        return result is not null;
    }
}

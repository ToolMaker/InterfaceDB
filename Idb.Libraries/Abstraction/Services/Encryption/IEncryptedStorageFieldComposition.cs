namespace Idb.Libraries.Abstractions.Services.Encryption;

/// <summary>
/// Optional marker for concrete storage implementations that expose a secure InterfaceDB contract view.
/// </summary>
public interface IEncryptedStorageFieldComposition<out TContract>
    where TContract : class
{
    TContract Contract { get; }
}

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Internal hand-off used by the file repository to bring its initial cache snapshot online
/// without leaving a gap in which file-system notifications can be lost.
/// </summary>
internal interface IFileEnvelopeStorageChangeBootstrap
{
    ValueTask StartBufferingAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteBootstrapAsync(
        IReadOnlyCollection<string> loadedProviderLocators,
        CancellationToken cancellationToken = default);
}

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Publishes already flushed files and persists directory-entry changes.</summary>
public interface IFilePublication
{
    void Replace(string stagedPath, string destinationPath);
    void Delete(string path);
    void FlushDirectory(string directoryPath);
}

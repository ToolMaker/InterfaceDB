namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Publishes already flushed files and persists directory-entry changes.</summary>
public interface IFilePublication
{
    /// <summary>
    /// Creates missing directories and persists each new directory and its containing directory.
    /// Override for platforms that publish directories through a different barrier.
    /// Existing paths are provisioned by their owner. Failed barriers are retried on
    /// this publication instance before acknowledging that directory or a descendant.
    /// </summary>
    void EnsureDirectory(string directoryPath)
    {
        FilePublicationDirectories.EnsureDirectory(this, directoryPath);
    }

    void Replace(string stagedPath, string destinationPath);
    void Delete(string path);
    void FlushDirectory(string directoryPath);
}

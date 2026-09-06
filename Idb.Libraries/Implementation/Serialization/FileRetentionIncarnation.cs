namespace Idb.Libraries.Implementation.Serialization;

/// <summary>An opt-in per-payload marker prevents another process's delete/recreate from reviving an edit.
/// Marker reads/writes are protected by the same payload sidecar lock as commits.</summary>
internal static class FileRetentionIncarnation
{
    internal static string Capture(string payloadPath)
    {
        var path = payloadPath + ".edit-incarnation";
        if (!File.Exists(path)) Write(path);
        return File.ReadAllText(path);
    }
    internal static bool Matches(string payloadPath, string incarnation)
        => File.Exists(payloadPath + ".edit-incarnation") && File.ReadAllText(payloadPath + ".edit-incarnation") == incarnation;
    internal static void Invalidate(string payloadPath)
    {
        var path = payloadPath + ".edit-incarnation";
        if (File.Exists(path)) File.Delete(path);
    }
    private static void Write(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"))); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}

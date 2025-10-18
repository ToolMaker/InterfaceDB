namespace Idb.Libraries.Abstractions.Services.Serialization
{
    public interface IObjectEnvelope<TContract>
        where TContract : class
    {
        string VersionTag { get; }
        Type ObjectType { get; }
        FileInfo File { get; }
        byte[] Payload { get; set; }
    }
}


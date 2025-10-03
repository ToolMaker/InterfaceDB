namespace Idb.Libraries.Abstractions.Serialization
{
    public interface IObjectSerializer
    {
        string FileExtension { get; }

        byte[] Serialize<T>(T obj);
        byte[] Serialize(object value, Type valueType);

        T Deserialize<T>(byte[] data);
        object Deserialize(byte[] data, Type valueType);
    }
}

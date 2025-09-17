namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectSerializer
    {
        byte[] Serialize<T>(T obj);
        T Deserialize<T>(byte[] data);
    }
}
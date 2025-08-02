namespace TransactionManager.Serializers
{
    public interface IObjectSerializer
    {
        byte[] Serialize<T>(T obj);
        T Deserialize<T>(byte[] data);
    }
}
namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectReader
    {
        public T ReadObject<T>(T obj); //?????
    }
}

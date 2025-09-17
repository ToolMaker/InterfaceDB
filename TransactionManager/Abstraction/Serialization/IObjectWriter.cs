namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectWriter
    {
        public T WriteObject<T>(T obj);
    }
}

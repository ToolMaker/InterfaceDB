namespace TransactionManager.Abstraction.Encryption
{
    public interface IEncryptor
    {
        byte[] Encrypt(ReadOnlySpan<byte> plain);
        byte[] Decrypt(ReadOnlySpan<byte> cipher);
    }
}


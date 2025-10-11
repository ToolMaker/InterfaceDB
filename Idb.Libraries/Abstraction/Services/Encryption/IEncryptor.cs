namespace Idb.Libraries.Abstractions.Services.Encryption
{
    public interface IEncryptor
    {
        byte[] Encrypt(ReadOnlySpan<byte> plain);
        byte[] Decrypt(ReadOnlySpan<byte> cipher);
    }
}


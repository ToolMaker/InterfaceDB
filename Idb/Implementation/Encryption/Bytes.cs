namespace TransactionManager.Implementation.Encryption
{
    public static class Bytes
    {
        /// <summary>Concatenate any number of byte-arrays quickly.</summary>
        public static byte[] Concat(params byte[][] chunks)
        {
            var length = chunks.Sum(a => a.Length);
            var result = new byte[length];

            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            return result;
        }
    }
}

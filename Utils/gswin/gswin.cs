namespace Utils
{
    internal static class GSWIN
    {
        public static byte[] Decompress(byte[] data, int compressedSize)
        {
            int limit = Math.Min(compressedSize, data.Length);
            int index = 0;
            using var output = new MemoryStream(limit * 2); // 粗略给个 2 倍容量
            Lzss.Decompress(
                () => index < limit ? data[index++] : -1,
                b => output.WriteByte(b),
                limit
            );
            return output.ToArray();
        }

        public static void Decompress(Stream input, int compressedSize, Stream output)
        {
            Lzss.Decompress(
                () => input.ReadByte(),
                b => output.WriteByte(b),
                compressedSize
            );
        }

        public static void XorDecrypt(byte[] data)
        {
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(data[i] ^ (byte)i);
        }
    }
}
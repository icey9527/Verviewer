using System;
using System.IO;

namespace Utils
{
    internal static class NinesFox
    {
        public static bool Decompress(byte[] data, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (data == null || data.Length < 8)
                return false;

            using var ms = new MemoryStream(data, false);
            return Decompress(ms, data.Length, out output);
        }

        public static bool Decompress(Stream input, int compressedSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (input == null || !input.CanRead || compressedSize < 8)
                return false;

            var header = new byte[8];
            if (input.Read(header, 0, 8) < 8)
                return false;

            if (header[0] != 'L' || header[1] != 'Z' || header[2] != 'S' || header[3] != 0)
                return false;

            uint sizeRaw = BitConverter.ToUInt32(header, 4);
            if (sizeRaw > int.MaxValue)
                return false;

            int expectedSize = (int)sizeRaw;
            byte[] buffer = new byte[expectedSize];
            byte[] dict = new byte[0x1000];
            Array.Fill(dict, (byte)0x20);

            int remaining = compressedSize - 8;
            int outPos = 0;
            int dictPos = 0xFEE;
            int flags = 0;
            int mask = 0;

            Func<int> readByte = () =>
            {
                if (remaining <= 0)
                    return -1;

                int b = input.ReadByte();
                if (b < 0)
                    return -1;

                remaining--;
                return b;
            };

            while (outPos < expectedSize)
            {
                if (mask == 0)
                {
                    flags = readByte();
                    if (flags < 0)
                        break;
                    mask = 1;
                }

                if ((flags & mask) != 0)
                {
                    int b = readByte();
                    if (b < 0)
                        break;

                    byte v = (byte)b;
                    buffer[outPos++] = v;
                    dict[dictPos] = v;
                    dictPos = (dictPos + 1) & 0xFFF;
                }
                else
                {
                    int b1 = readByte();
                    int b2 = readByte();
                    if (b1 < 0 || b2 < 0)
                        break;

                    int pos = b1 | ((b2 & 0xF0) << 4);
                    int len = (b2 & 0x0F) + 3;

                    for (int i = 0; i < len && outPos < expectedSize; i++)
                    {
                        byte v = dict[(pos + i) & 0xFFF];
                        buffer[outPos++] = v;
                        dict[dictPos] = v;
                        dictPos = (dictPos + 1) & 0xFFF;
                    }
                }

                mask = (mask << 1) & 0xFF;
            }

            if (outPos != expectedSize)
                return false;

            output = buffer;
            return true;
        }
    }
}
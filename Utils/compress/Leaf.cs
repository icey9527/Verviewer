using System;
using System.IO;

namespace Utils
{
    internal static class Leaf
    {
        const int RingSize = 0x1000;
        const int RingMask = RingSize - 1;
        const int RingInit = 4078;
        const int MinMatch = 3;

        public static bool Decompress(byte[] data, int outputSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (data == null || outputSize < 0)
                return false;

            using var ms = new MemoryStream(data, writable: false);
            return Decompress(ms, data.Length, outputSize, out output);
        }

        public static bool Decompress(Stream input, int compressedSize, int outputSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (input == null || !input.CanRead || compressedSize < 0 || outputSize < 0)
                return false;

            byte[] buffer = new byte[outputSize];
            byte[] ring = new byte[RingSize];
            int ringPos = RingInit;
            int outPos = 0;
            int remaining = compressedSize;

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

            while (outPos < outputSize)
            {
                int flagByte = readByte();
                if (flagByte < 0)
                    return false;

                ushort flags = (ushort)((((~flagByte) & 0xFF) << 8) | 0x00FF);

                while (outPos < outputSize)
                {
                    bool literal = (flags & 0x8000) != 0;
                    flags = (ushort)((flags << 1) & 0xFFFF);

                    if (literal)
                    {
                        int raw = readByte();
                        if (raw < 0)
                            return false;

                        byte value = (byte)~raw;
                        buffer[outPos++] = value;
                        ring[ringPos] = value;
                        ringPos = (ringPos + 1) & RingMask;
                    }
                    else
                    {
                        int b0 = readByte();
                        int b1 = readByte();
                        if (b0 < 0 || b1 < 0)
                            return false;

                        ushort pair = (ushort)~(b0 | (b1 << 8));
                        int offset = pair >> 4;
                        int length = (pair & 0x0F) + MinMatch;

                        for (int i = 0; i < length && outPos < outputSize; i++)
                        {
                            byte value = ring[(offset + i) & RingMask];
                            buffer[outPos++] = value;
                            ring[ringPos] = value;
                            ringPos = (ringPos + 1) & RingMask;
                        }
                    }

                    if ((flags & 0x00FF) == 0)
                        break;
                }
            }

            output = buffer;
            return true;
        }
    }
}

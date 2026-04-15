using System;
using System.IO;

namespace Utils
{
    internal static class Broccoli
    {
        const int Iel1HeaderSize = 8;
        const int RingSize = 0x1000;
        const int RingMask = RingSize - 1;
        const int RingInit = 4078;

        public static bool IsIel1(byte[] data)
        {
            return data != null
                && data.Length >= Iel1HeaderSize
                && data[0] == (byte)'I'
                && data[1] == (byte)'E'
                && data[2] == (byte)'L'
                && data[3] == (byte)'1';
        }

        public static bool DecompressIel1(byte[] data, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (!IsIel1(data))
                return false;

            int outputSize = ReadInt32LE(data, 4);
            if (outputSize < 0)
                return false;

            output = new byte[outputSize];
            byte[] ring = new byte[RingSize];
            for (int i = 0; i < RingInit; i++)
                ring[i] = 0x20;

            int src = Iel1HeaderSize;
            int srcEnd = data.Length;
            int dst = 0;
            int ringPos = RingInit;
            uint flags = 0;

            while (src < srcEnd)
            {
                if ((flags & 0x100) == 0)
                {
                    if (src >= srcEnd)
                        break;

                    flags = (uint)(data[src++] | 0xFF00);
                }

                if ((flags & 1) != 0)
                {
                    if (src >= srcEnd)
                        break;

                    byte value = data[src++];
                    if (dst >= output.Length)
                        return false;

                    output[dst++] = value;
                    ring[ringPos & RingMask] = value;
                    ringPos++;
                }
                else
                {
                    if (src >= srcEnd)
                        break;

                    int b0 = data[src++];
                    if (src >= srcEnd)
                        break;

                    int b1 = data[src++];
                    int refPos = b0 | ((b1 & 0xF0) << 4);
                    int count = (b1 & 0x0F) + 3;

                    for (int i = 0; i < count; i++)
                    {
                        byte value = ring[(refPos + i) & RingMask];
                        if (dst >= output.Length)
                            return false;

                        output[dst++] = value;
                        ring[ringPos & RingMask] = value;
                        ringPos++;
                    }
                }

                flags >>= 1;
            }

            return true;
        }

        static int ReadInt32LE(byte[] data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }
    }
}

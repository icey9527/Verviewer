using System;

namespace Verviewer.Archives
{
    internal static class Lzss
    {
        public static int Decompress(Func<int> readByte, Action<byte> writeByte, int compressedSize)
        {
            var window = new byte[0x1000];
            int pos = 0xFEE;
            int flags = 0;
            int remaining = compressedSize;
            int written = 0;
            while (remaining > 0)
            {
                flags >>= 1;
                if ((flags & 0x100) == 0)
                {
                    int fb = readByte();
                    if (fb < 0) break;
                    remaining--;
                    flags = 0xFF00 | fb;
                }
                if ((flags & 1) != 0)
                {
                    int b = readByte();
                    if (b < 0) break;
                    remaining--;
                    byte v = (byte)b;
                    writeByte(v);
                    window[pos] = v;
                    pos = (pos + 1) & 0xFFF;
                    written++;
                }
                else
                {
                    int b1 = readByte();
                    int b2 = readByte();
                    if (b1 < 0 || b2 < 0) break;
                    remaining -= 2;
                    int offset = b1 | ((b2 & 0xF0) << 4);
                    int length = (b2 & 0x0F) + 3;
                    for (int k = 0; k < length; k++)
                    {
                        byte v = window[offset & 0xFFF];
                        writeByte(v);
                        window[pos] = v;
                        offset++;
                        pos = (pos + 1) & 0xFFF;
                        written++;
                    }
                }
            }
            return written;
        }
    }
}
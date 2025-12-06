using System;
using System.IO;

namespace Utils
{
    internal static class Artdink
    {
        public static bool Decompress(byte[] data, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (data == null || data.Length < 8) return false;

            using var ms = new MemoryStream(data, false);
            return Decompress(ms, data.Length, out output);
        }

        public static bool Decompress(Stream input, int compressedSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (input == null || !input.CanRead || compressedSize < 8) return false;

            var header = new byte[8];
            if (input.Read(header, 0, 8) < 8) return false;

            var mode = ParseMode(header[3]);
            if (mode < 0) return false;

            var hasPrefix = false;
            if (header[0] == (byte)'A' && header[1] == (byte)'R' && header[2] == (byte)'Z')
                hasPrefix = true;
            else if (header[0] == (byte)' ' && header[1] == (byte)'3' && header[2] == (byte)';')
                hasPrefix = true;

            if (!hasPrefix) return false;

            var sizeRaw = BitConverter.ToUInt32(header, 4);
            if (sizeRaw == 0 || sizeRaw > int.MaxValue) return false;

            var expectedSize = (int)sizeRaw;
            var buffer = new byte[expectedSize];
            var outIndex = 0;
            var remaining = compressedSize - 8;

            Func<int> readByte = () =>
            {
                if (remaining <= 0) return -1;
                var b = input.ReadByte();
                if (b < 0) return -1;
                remaining--;
                return b ^ 0x72;
            };

            Action<byte> writeByte = v =>
            {
                if (outIndex < expectedSize)
                    buffer[outIndex++] = v;
            };

            if (mode != 0 && mode != 1) return false;

            if (mode == 0)
            {
                while (remaining > 0 && outIndex < expectedSize)
                {
                    var b = readByte();
                    if (b < 0) break;
                    writeByte((byte)b);
                }
            }
            else if (mode == 1)
            {
                Lzss.Decompress(readByte, writeByte, remaining);
            }

            output = new byte[outIndex];
            Buffer.BlockCopy(buffer, 0, output, 0, outIndex);
            return true;
        }

        static int ParseMode(byte value)
        {
            if (value >= (byte)'0' && value <= (byte)'9')
                return value - (byte)'0';
            if (value >= (byte)'A' && value <= (byte)'F')
                return value - (byte)'A' + 10;
            if (value >= (byte)'a' && value <= (byte)'f')
                return value - (byte)'a' + 10;
            return -1;
        }
    }
}
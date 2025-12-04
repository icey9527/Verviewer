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
            int readHeader = input.Read(header, 0, 8);
            if (readHeader < 8) return false;

            uint expectedSizeRaw = BitConverter.ToUInt32(header, 4);
            if (expectedSizeRaw == 0 || expectedSizeRaw > int.MaxValue) return false;

            int expectedSize = (int)expectedSizeRaw;
            int remaining = compressedSize - 8;

            var dict = new byte[0x1000];
            uint dictPos = 0xFEE;
            uint control = 0;
            int outIndex = 0;
            var buffer = new byte[expectedSize];

            bool isMode1 = compressedSize > 3 &&
                           header[1] == (byte)'3' &&
                           header[2] == (byte)';' &&
                           header[3] == (byte)'1';

            bool isMode0 = compressedSize > 3 &&
                           header[1] == (byte)'3' &&
                           header[2] == (byte)';' &&
                           header[3] == (byte)'0';

            if (isMode1)
            {
                bool stop = false;
                while (!stop)
                {
                    while (true)
                    {
                        control >>= 1;
                        uint temp = control;

                        if ((control & 0x100) == 0)
                        {
                            if (remaining <= 0)
                            {
                                stop = true;
                                break;
                            }

                            int pb = input.ReadByte();
                            if (pb < 0)
                            {
                                stop = true;
                                break;
                            }

                            remaining--;
                            byte x = (byte)(pb ^ 0x72);
                            control = (uint)(x | 0xFF00);
                            temp = x;
                        }

                        if ((temp & 1) != 0)
                            break;

                        if (remaining <= 1)
                        {
                            stop = true;
                            break;
                        }

                        int b1 = input.ReadByte();
                        int b2 = input.ReadByte();
                        if (b1 < 0 || b2 < 0)
                        {
                            stop = true;
                            break;
                        }
                        remaining -= 2;

                        int cnt = 0;
                        int len = ((b2 ^ 0x72) & 0x0F) + 2;

                        while (cnt <= len)
                        {
                            uint offset = (uint)((b1 ^ 0x72) |
                                                 (((b2 ^ 0x72) & 0xF0) << 4));
                            offset += (uint)cnt;
                            cnt++;

                            byte value = dict[offset & 0x0FFF];

                            if (outIndex >= expectedSize)
                            {
                                stop = true;
                                break;
                            }

                            buffer[outIndex++] = value;
                            dict[dictPos] = value;
                            dictPos = (dictPos + 1) & 0x0FFF;
                        }

                        if (stop)
                            break;
                    }

                    if (stop)
                        break;

                    if (remaining <= 0)
                        break;

                    int b = input.ReadByte();
                    if (b < 0) break;
                    remaining--;

                    byte value2 = (byte)(b ^ 0x72);

                    if (outIndex >= expectedSize)
                        break;

                    buffer[outIndex++] = value2;
                    dict[dictPos] = value2;
                    dictPos = (dictPos + 1) & 0x0FFF;
                }
            }
            else if (isMode0)
            {
                while (remaining > 0 && outIndex < expectedSize)
                {
                    int b = input.ReadByte();
                    if (b < 0) break;
                    remaining--;
                    buffer[outIndex++] = (byte)(b ^ 0x72);
                }
            }
            else
            {
                return false;
            }

            if (outIndex < 0) outIndex = 0;
            if (outIndex > expectedSize) outIndex = expectedSize;

            output = new byte[outIndex];
            Buffer.BlockCopy(buffer, 0, output, 0, outIndex);
            return true;
        }
    }
}
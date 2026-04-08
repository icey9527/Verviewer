using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "LEAF LCF",
        extensions: new[] { "lcf" },
        magics: new[] { "LEAFCFL" }
    )]
    internal sealed class LeafLcfImageHandler : IImageHandler
    {
        const int HeaderSize = 24;

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();

            try
            {
                if (s.Length < HeaderSize)
                    return null;

                uint dataOffset = s.ReadUInt32LEAt(0x10);
                int width = s.ReadUInt16LEAt(0x0C);
                int height = s.ReadUInt16LEAt(0x0E);
                int unpackedSize = s.ReadInt32LEAt(0x14);

                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    return null;
                if (dataOffset != HeaderSize)
                    return null;
                if (unpackedSize < 0)
                    return null;

                byte[] compressed = s.ReadBytesAt(HeaderSize, checked((int)(s.Length - HeaderSize)));
                if (!Leaf.Decompress(compressed, unpackedSize, out byte[] pixels))
                    return null;
                if (!TryDecodePixels(pixels, width, height, out Bitmap? bmp))
                    return null;

                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }

        static bool TryDecodePixels(byte[] src, int width, int height, out Bitmap? bmp)
        {
            bmp = null;
            int srcPos = 0;

            bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
            Bitmap createdBmp = bmp;

            try
            {
                byte[] row = new byte[width * 4];

                for (int y = 0; y < height; y++)
                {
                    Array.Clear(row, 0, row.Length);

                    for (int x = 0; x < width; x++)
                    {
                        if (srcPos >= src.Length)
                            return false;

                        byte control = src[srcPos++];
                        int dst = x * 4;

                        if (control == 0)
                            continue;

                        if (srcPos + 3 > src.Length)
                            return false;

                        byte b = src[srcPos++];
                        byte g = src[srcPos++];
                        byte r = src[srcPos++];
                        byte a = control == 0xFF ? (byte)255 : control;

                        row[dst + 0] = b;
                        row[dst + 1] = g;
                        row[dst + 2] = r;
                        row[dst + 3] = a;
                    }

                    int destY = height - 1 - y;
                    ImageUtils.CopyRowToBitmap(bmpData, destY, row, stride);
                }

                return true;
            }
            catch
            {
                createdBmp.Dispose();
                bmp = null;
                return false;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bmpData, createdBmp);
            }
        }
    }
}

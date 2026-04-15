using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "BROCCOLI GRP",
        extensions: new[] { "grp" }
    )]
    internal sealed class BroccoliGrpImageHandler : IImageHandler
    {
        const int HeaderSize = 40;
        const int PalettePlaneSize = 256;
        const int PaletteSize = PalettePlaneSize * 4;

        public Image? TryDecode(Stream stream, string? ext)
        {
            if (!stream.CanRead)
                return null;

            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < HeaderSize + PaletteSize)
                    return null;

                int bpp = s.ReadInt32LEAt(0);
                int stride = Math.Abs(s.ReadInt32LEAt(12));
                int width = s.ReadInt32LEAt(32);
                int height = s.ReadInt32LEAt(36);

                if (width <= 0 || height <= 0 || width > 0x4000 || height > 0x4000)
                    return null;

                int minStride = bpp switch
                {
                    8 => width,
                    24 => width * 3,
                    32 => width * 4,
                    _ => -1
                };
                if (minStride <= 0 || stride < minStride)
                    return null;

                long pixelBytes = (long)stride * height;
                if (pixelBytes <= 0 || pixelBytes > int.MaxValue)
                    return null;

                var palR = new byte[PalettePlaneSize];
                var palG = new byte[PalettePlaneSize];
                var palB = new byte[PalettePlaneSize];
                var palA = new byte[PalettePlaneSize];

                s.Position = HeaderSize;
                s.ReadExactly(palR, 0, palR.Length);
                s.ReadExactly(palG, 0, palG.Length);
                s.ReadExactly(palB, 0, palB.Length);
                s.ReadExactly(palA, 0, palA.Length);

                byte[] pixels;
                using (var z = new ZLibStream(s, CompressionMode.Decompress, leaveOpen: true))
                    pixels = z.ReadExactly((int)pixelBytes);

                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int bmpStride);
                try
                {
                    var row = new byte[width * 4];
                    var row24 = bpp == 24 ? new byte[width * 3] : Array.Empty<byte>();

                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = y * stride;

                        if (bpp == 8)
                        {
                            DecodeIndexedRow(pixels, srcRow, width, row, palR, palG, palB, palA);
                        }
                        else if (bpp == 24)
                        {
                            Buffer.BlockCopy(pixels, srcRow, row24, 0, row24.Length);
                            ImageUtils.ConvertRowBgr24ToBgra(row24, row, width);
                        }
                        else
                        {
                            DecodeBgra32Row(pixels, srcRow, width, row);
                        }

                        ImageUtils.CopyRowToBitmap(bmpData, y, row, bmpStride);
                    }

                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    return null;
                }
                finally
                {
                    ImageUtils.UnlockBitmap(bmpData, bmp);
                }
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

        static void DecodeIndexedRow(
            byte[] pixels,
            int srcRow,
            int width,
            byte[] row,
            byte[] palR,
            byte[] palG,
            byte[] palB,
            byte[] palA)
        {
            for (int x = 0; x < width; x++)
            {
                int c = pixels[srcRow + x];
                int dst = x * 4;
                row[dst + 0] = palB[c];
                row[dst + 1] = palG[c];
                row[dst + 2] = palR[c];
                row[dst + 3] = palA[c];
            }
        }

        static void DecodeBgra32Row(byte[] pixels, int srcRow, int width, byte[] row)
        {
            for (int x = 0; x < width; x++)
            {
                int src = srcRow + x * 4;
                int dst = x * 4;
                row[dst + 0] = pixels[src + 0];
                row[dst + 1] = pixels[src + 1];
                row[dst + 2] = pixels[src + 2];
                row[dst + 3] = 255;
            }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;
using Utils;  // StreamUtils, ImageUtils

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink FAC",
        extensions: new[] { "fac" }
    )]
    internal sealed class FacImageHandler : IImageHandler
    {
        private static readonly byte[] MagicPattern =
        {
            0x00,0x00,0x00,0x00,
            0x01,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,
            0x10,0x00,0x10,0x00
        };

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek) return null;

                long pos = FindPattern(s, MagicPattern);
                if (pos < 0) return null;

                long headerStart = pos - 0x40;
                if (headerStart < 0) return null;

                ushort w = (ushort)s.ReadUInt16LEAt(headerStart + 0x38);
                ushort h = (ushort)s.ReadUInt16LEAt(headerStart + 0x3A);
                if (w == 0 || h == 0) return null;

                long pixelCount = (long)w * h;
                long pixelStart = pos + 16;
                long palStart = pixelStart + pixelCount;

                if (pixelStart < 0 || pixelStart + pixelCount > s.Length)
                    return null;

                var palette = BuildPaletteBgra(s, palStart);
                if (palette == null) return null;

                s.Position = pixelStart;

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    int stride = bd.Stride;
                    var rowIdx = new byte[w];
                    var row = new byte[w * 4];

                    for (int y = 0; y < h; y++)
                    {
                        s.ReadExactly(rowIdx, 0, rowIdx.Length);

                        int dst = 0;
                        for (int x = 0; x < w; x++)
                        {
                            int pi = rowIdx[x] * 4;
                            row[dst + 0] = palette[pi + 0];
                            row[dst + 1] = palette[pi + 1];
                            row[dst + 2] = palette[pi + 2];
                            row[dst + 3] = palette[pi + 3];
                            dst += 4;
                        }

                        IntPtr dest = IntPtr.Add(bd.Scan0, y * stride);
                        Marshal.Copy(row, 0, dest, row.Length);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

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

        // 使用公共的 PS2 256 色调色板函数，语义与原 FAC BuildPalette 完全相同
        private static byte[]? BuildPaletteBgra(Stream s, long palOffset)
        {
            if (palOffset < 0 || palOffset + 0x400 > s.Length)
                return null;

            s.Position = palOffset;
            byte[] palData = s.ReadExactly(256 * 4); // 原始 RGBA

            return ImageUtils.BuildPs2Palette256Bgra_Block32(palData);
        }

        private static long FindPattern(Stream s, byte[] pattern)
        {
            long save = s.Position;
            s.Position = 0;

            const int chunk = 64 * 1024;
            int pLen = pattern.Length;
            var buffer = new byte[chunk + pLen - 1];
            long baseOffset = 0;
            int keep = 0;

            while (true)
            {
                int read = s.Read(buffer, keep, chunk);
                if (read <= 0) break;

                int total = keep + read;
                int limit = total - pLen + 1;

                for (int i = 0; i < limit; i++)
                {
                    int j = 0;
                    for (; j < pLen; j++)
                    {
                        if (buffer[i + j] != pattern[j])
                            break;
                    }
                    if (j == pLen)
                    {
                        long found = baseOffset + i;
                        s.Position = save;
                        return found;
                    }
                }

                keep = Math.Min(pLen - 1, total);
                Buffer.BlockCopy(buffer, total - keep, buffer, 0, keep);
                baseOffset += total - keep;
            }

            s.Position = save;
            return -1;
        }
    }
}
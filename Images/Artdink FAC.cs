using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;

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
            Stream s = EnsureSeekable(stream);
            try
            {
                if (!s.CanSeek) return null;

                long pos = FindPattern(s, MagicPattern);
                if (pos < 0) return null;

                long headerStart = pos - 0x40;
                if (headerStart < 0) return null;

                ushort w = ReadUInt16At(s, headerStart + 0x38);
                ushort h = ReadUInt16At(s, headerStart + 0x3A);
                if (w == 0 || h == 0) return null;

                long pixelCount = (long)w * h;
                long pixelStart = pos + 16;
                long palStart = pixelStart + pixelCount;

                // 构建调色板
                var palette = BuildPalette(s, palStart);
                if (palette == null) return null;

                // 解像素（逐行读）
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var row = new byte[w];

                s.Position = pixelStart;
                for (int y = 0; y < h; y++)
                {
                    ReadExactlyInto(s, row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        bmp.SetPixel(x, y, palette[row[x]]);
                    }
                }

                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (!ReferenceEquals(s, stream)) s.Dispose();
            }
        }

        private static Color[]? BuildPalette(Stream s, long palOffset)
        {
            if (palOffset < 0 || palOffset + 0x400 > s.Length) return null;

            s.Position = palOffset;
            var palData = ReadExactly(s, 256 * 4);

            var original = new (byte R, byte G, byte B, byte A)[256];
            for (int i = 0; i < 256; i++)
            {
                int off = i * 4;
                byte r = palData[off + 0];
                byte g = palData[off + 1];
                byte b = palData[off + 2];
                byte a = FixAlpha(palData[off + 3]);
                original[i] = (r, g, b, a);
            }

            var palette = new Color[256];
            int dst = 0;
            for (int major = 0; major < 256; major += 32)
            {
                for (int i = 0; i < 8; i++) { var p = original[major + i]; palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B); }
                for (int i = 16; i < 24; i++) { var p = original[major + i]; palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B); }
                for (int i = 8; i < 16; i++) { var p = original[major + i]; palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B); }
                for (int i = 24; i < 32; i++) { var p = original[major + i]; palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B); }
            }
            return palette;
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
                        if (buffer[i + j] != pattern[j]) break;
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

        private static byte FixAlpha(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        private static Stream EnsureSeekable(Stream s)
        {
            if (s.CanSeek) return s;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        private static ushort ReadUInt16At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int lo = s.ReadByte(); int hi = s.ReadByte();
            s.Position = save;
            if (lo < 0 || hi < 0) return 0;
            return (ushort)(lo | (hi << 8));
        }

        private static byte[] ReadExactly(Stream s, int count)
        {
            var buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, total, count - total);
                if (r <= 0) break;
                total += r;
            }
            if (total == count) return buf;
            Array.Resize(ref buf, total);
            return buf;
        }

        private static void ReadExactlyInto(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0) throw new EndOfStreamException();
                total += r;
            }
        }
    }
}
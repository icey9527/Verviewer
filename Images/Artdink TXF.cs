using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink TXF",
        extensions: new[] { "txf" }
    )]
    internal sealed class TxfImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = EnsureSeekable(stream);
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;
                int len = (int)Math.Min(int.MaxValue, s.Length);
                int glyphOff0 = ReadInt32At(s, 0x30);
                if (glyphOff0 < 0 || glyphOff0 + 0x20 > len) return null;
                int glyph = glyphOff0;
                ushort count = ReadUInt16At(s, glyph + 0x04);
                if (count == 0) return null;
                int pixelRel = ReadInt32At(s, glyph + 0x08);
                int pixelPtr = glyph + pixelRel;
                if (pixelPtr < 0 || pixelPtr >= len) return null;
                byte fmt = ReadByteAt(s, glyph + 0x0E);
                if (fmt != 0x13 && fmt != 0x1B) return null;
                int width = ReadUInt16At(s, glyph + 0x18);
                int height = ReadUInt16At(s, glyph + 0x1A);
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
                long pixels = (long)width * height;
                if (pixels <= 0) return null;
                const int paletteSize = 0x400;
                if (len < paletteSize + pixelPtr) return null;
                int palOffset = len - paletteSize;
                int pixelBytes = palOffset - pixelPtr;
                if (pixelBytes < pixels) return null;
                s.Position = palOffset;
                var palRaw = ReadExactly(s, paletteSize);
                if (palRaw.Length < paletteSize) return null;
                var pal = new byte[256 * 4];
                for (int i = 0; i < 256; i++)
                {
                    int p = i * 4;
                    byte r = palRaw[p + 0];
                    byte g = palRaw[p + 1];
                    byte b = palRaw[p + 2];
                    byte a = FixAlpha(palRaw[p + 3]);
                    pal[p + 0] = r;
                    pal[p + 1] = g;
                    pal[p + 2] = b;
                    pal[p + 3] = a;
                }
                var palReordered = new byte[256 * 4];
                int dst = 0;
                for (int major = 0; major < 256; major += 32)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int src = (major + i) * 4;
                        palReordered[dst + 0] = pal[src + 2];
                        palReordered[dst + 1] = pal[src + 1];
                        palReordered[dst + 2] = pal[src + 0];
                        palReordered[dst + 3] = pal[src + 3];
                        dst += 4;
                    }
                    for (int i = 16; i < 24; i++)
                    {
                        int src = (major + i) * 4;
                        palReordered[dst + 0] = pal[src + 2];
                        palReordered[dst + 1] = pal[src + 1];
                        palReordered[dst + 2] = pal[src + 0];
                        palReordered[dst + 3] = pal[src + 3];
                        dst += 4;
                    }
                    for (int i = 8; i < 16; i++)
                    {
                        int src = (major + i) * 4;
                        palReordered[dst + 0] = pal[src + 2];
                        palReordered[dst + 1] = pal[src + 1];
                        palReordered[dst + 2] = pal[src + 0];
                        palReordered[dst + 3] = pal[src + 3];
                        dst += 4;
                    }
                    for (int i = 24; i < 32; i++)
                    {
                        int src = (major + i) * 4;
                        palReordered[dst + 0] = pal[src + 2];
                        palReordered[dst + 1] = pal[src + 1];
                        palReordered[dst + 2] = pal[src + 0];
                        palReordered[dst + 3] = pal[src + 3];
                        dst += 4;
                    }
                }
                if (pixelPtr < 0 || pixelPtr >= len) return null;
                s.Position = pixelPtr;
                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                BitmapData bd;
                try
                {
                    bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                }
                catch
                {
                    bmp.Dispose();
                    return null;
                }
                try
                {
                    int stride = bd.Stride;
                    var rowIdx = new byte[width];
                    var row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        ReadExactlyInto(s, rowIdx, 0, rowIdx.Length);
                        int di = 0;
                        for (int x = 0; x < width; x++)
                        {
                            int pi = rowIdx[x] * 4;
                            row[di + 0] = palReordered[pi + 0];
                            row[di + 1] = palReordered[pi + 1];
                            row[di + 2] = palReordered[pi + 2];
                            row[di + 3] = palReordered[pi + 3];
                            di += 4;
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
                if (!ReferenceEquals(s, stream)) s.Dispose();
            }
        }

        static Stream EnsureSeekable(Stream s)
        {
            if (s.CanSeek) return s;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        static int ReadInt32At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2 | b3) < 0) return 0;
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        static ushort ReadUInt16At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int lo = s.ReadByte();
            int hi = s.ReadByte();
            s.Position = save;
            if (lo < 0 || hi < 0) return 0;
            return (ushort)(lo | (hi << 8));
        }

        static byte ReadByteAt(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int v = s.ReadByte();
            s.Position = save;
            return v < 0 ? (byte)0 : (byte)v;
        }

        static byte FixAlpha(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        static byte[] ReadExactly(Stream s, int count)
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

        static void ReadExactlyInto(Stream s, byte[] buf, int offset, int count)
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
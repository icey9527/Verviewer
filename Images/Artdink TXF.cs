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
                if (fmt != 0x13 && fmt != 0x1B) return null; // 8bpp

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

                var original = new (byte r, byte g, byte b, byte a)[256];
                for (int i = 0; i < 256; i++)
                {
                    int p = i * 4;
                    original[i] = (palRaw[p + 0], palRaw[p + 1], palRaw[p + 2], FixAlpha(palRaw[p + 3]));
                }

                var pal = new (byte r, byte g, byte b, byte a)[256];
                int dst = 0;
                for (int major = 0; major < 256; major += 32)
                {
                    for (int i = 0; i < 8; i++) pal[dst++] = original[major + i];
                    for (int i = 16; i < 24; i++) pal[dst++] = original[major + i];
                    for (int i = 8; i < 16; i++) pal[dst++] = original[major + i];
                    for (int i = 24; i < 32; i++) pal[dst++] = original[major + i];
                }

                s.Position = pixelPtr;
                int stride = width * 4;
                var full = new byte[stride * height];
                var row = new byte[width];

                for (int y = 0; y < height; y++)
                {
                    ReadExactlyInto(s, row, 0, row.Length);
                    int di = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        var p = pal[row[x]];
                        full[di + 0] = p.b;
                        full[di + 1] = p.g;
                        full[di + 2] = p.r;
                        full[di + 3] = p.a;
                        di += 4;
                    }
                }

                return ToBitmap32(width, height, full);
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

        static Image ToBitmap32(int width, int height, byte[] full)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = width * 4;
                if (bd.Stride == stride)
                {
                    Marshal.Copy(full, 0, bd.Scan0, full.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr dst = IntPtr.Add(bd.Scan0, y * bd.Stride);
                        Marshal.Copy(full, y * stride, dst, stride);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
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
            int b0 = s.ReadByte(), b1 = s.ReadByte(), b2 = s.ReadByte(), b3 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2 | b3) < 0) return 0;
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        static ushort ReadUInt16At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int lo = s.ReadByte(); int hi = s.ReadByte();
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
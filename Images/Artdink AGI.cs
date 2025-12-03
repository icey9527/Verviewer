using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink AGI",
        extensions: new[] { "agi" }
    )]
    internal sealed class AgiImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = EnsureSeekable(stream);
            try
            {
                if (!s.CanSeek || s.Length < 0x30) return null;
                int width = ReadUInt16At(s, 0x18);
                int height = ReadUInt16At(s, 0x1A);
                if (width <= 0 || height <= 0) return null;
                var flag = ReadBytesAt(s, 0x2C, 4);
                if (flag.Length < 4) return null;
                string bppFlag = BytesToHex(flag);
                return bppFlag switch
                {
                    "44494449" => Read16bpp(s, width, height),
                    "00300100" => Read24bpp(s, width, height),
                    "10001000" => Read8bpp(s, width, height),
                    "00001400" or "08000200" => Read4bpp(s, width, height),
                    _ => null
                };
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

        static Image Read4bpp(Stream s, int width, int height)
        {
            uint palOff = ReadUInt32At(s, 0x1C);
            if (palOff == 0) throw new ArgumentException();
            s.Position = palOff;
            var palRaw = ReadExactly(s, 16 * 4);
            if (palRaw.Length < 16 * 4) throw new EndOfStreamException();
            var palBGRA = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                int p = i * 4;
                palBGRA[p + 0] = palRaw[p + 2];
                palBGRA[p + 1] = palRaw[p + 1];
                palBGRA[p + 2] = palRaw[p + 0];
                palBGRA[p + 3] = FixAlpha(palRaw[p + 3]);
            }
            int pixelDataOffset = 0x30;
            if (pixelDataOffset >= s.Length) throw new ArgumentException();
            s.Position = pixelDataOffset;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                var packed = new byte[(width + 1) / 2];
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    ReadExactlyInto(s, packed, 0, packed.Length);
                    int dst = 0;
                    for (int x = 0; x < width; x++)
                    {
                        int b = packed[x >> 1];
                        int idx = (x & 1) == 0 ? (b & 0x0F) : ((b >> 4) & 0x0F);
                        int pi = idx * 4;
                        row[dst + 0] = palBGRA[pi + 0];
                        row[dst + 1] = palBGRA[pi + 1];
                        row[dst + 2] = palBGRA[pi + 2];
                        row[dst + 3] = palBGRA[pi + 3];
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

        static Image Read8bpp(Stream s, int width, int height)
        {
            int palOff = ReadUInt24At(s, 0x1C);
            if (palOff <= 0) throw new ArgumentException();
            s.Position = palOff;
            var palData = ReadExactly(s, 256 * 4);
            if (palData.Length < 256 * 4) throw new EndOfStreamException();
            var original = new (byte r, byte g, byte b, byte a)[256];
            for (int i = 0; i < 256; i++)
            {
                int p = i * 4;
                original[i] = (palData[p + 0], palData[p + 1], palData[p + 2], FixAlpha(palData[p + 3]));
            }
            var pal = new (byte r, byte g, byte b, byte a)[256];
            int dsti = 0;
            for (int major = 0; major < 256; major += 32)
            {
                for (int i = 0; i < 8; i++) pal[dsti++] = original[major + i];
                for (int i = 16; i < 24; i++) pal[dsti++] = original[major + i];
                for (int i = 8; i < 16; i++) pal[dsti++] = original[major + i];
                for (int i = 24; i < 32; i++) pal[dsti++] = original[major + i];
            }
            int pixelDataOffset = 0x30;
            if (pixelDataOffset >= s.Length) throw new ArgumentException();
            s.Position = pixelDataOffset;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                var idxRow = new byte[width];
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    ReadExactlyInto(s, idxRow, 0, idxRow.Length);
                    int dst = 0;
                    for (int x = 0; x < width; x++)
                    {
                        var p = pal[idxRow[x]];
                        row[dst + 0] = p.b;
                        row[dst + 1] = p.g;
                        row[dst + 2] = p.r;
                        row[dst + 3] = p.a;
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

        static Image Read16bpp(Stream s, int width, int height)
        {
            int pixelDataOffset = 0x20;
            long need = (long)width * height * 2;
            if (pixelDataOffset + need > s.Length) throw new ArgumentException();
            s.Position = pixelDataOffset;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                var rowSrc = new byte[width * 2];
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    ReadExactlyInto(s, rowSrc, 0, rowSrc.Length);
                    int src = 0, dst = 0;
                    for (int x = 0; x < width; x++)
                    {
                        ushort px = (ushort)(rowSrc[src] | (rowSrc[src + 1] << 8));
                        src += 2;
                        int b5 = (px >> 10) & 0x1F;
                        int g5 = (px >> 5) & 0x1F;
                        int r5 = px & 0x1F;
                        byte r = (byte)((r5 << 3) | (r5 >> 2));
                        byte g = (byte)((g5 << 3) | (g5 >> 2));
                        byte b = (byte)((b5 << 3) | (b5 >> 2));
                        row[dst + 0] = b;
                        row[dst + 1] = g;
                        row[dst + 2] = r;
                        row[dst + 3] = 255;
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

        static Image Read24bpp(Stream s, int width, int height)
        {
            int pixelDataOffset = 0x50;
            long need = (long)width * height * 3;
            if (pixelDataOffset + need > s.Length) throw new ArgumentException();
            s.Position = pixelDataOffset;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                var rowSrc = new byte[width * 3];
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    ReadExactlyInto(s, rowSrc, 0, rowSrc.Length);
                    int src = 0, dst = 0;
                    for (int x = 0; x < width; x++)
                    {
                        byte r = rowSrc[src++];
                        byte g = rowSrc[src++];
                        byte b = rowSrc[src++];
                        row[dst + 0] = b;
                        row[dst + 1] = g;
                        row[dst + 2] = r;
                        row[dst + 3] = 255;
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

        static byte FixAlpha(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        static Stream EnsureSeekable(Stream s)
        {
            if (s.CanSeek) return s;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        static int ReadUInt16At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int lo = s.ReadByte();
            int hi = s.ReadByte();
            s.Position = save;
            if (lo < 0 || hi < 0) return 0;
            return lo | (hi << 8);
        }

        static uint ReadUInt32At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2 | b3) < 0) return 0;
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        static int ReadUInt24At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2) < 0) return 0;
            return b0 | (b1 << 8) | (b2 << 16);
        }

        static byte[] ReadBytesAt(Stream s, long off, int count)
        {
            long save = s.Position;
            s.Position = off;
            var buf = ReadExactly(s, count);
            s.Position = save;
            return buf;
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

        static string BytesToHex(byte[] bytes)
        {
            char[] c = new char[bytes.Length * 2];
            int i = 0;
            foreach (var b in bytes)
            {
                c[i++] = GetHexNibble(b >> 4);
                c[i++] = GetHexNibble(b & 0xF);
            }
            return new string(c);
        }

        static char GetHexNibble(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
    }
}
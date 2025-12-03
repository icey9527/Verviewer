// Some parts of this implementation are based on:
// https://github.com/punk7890/PS2-Visual-Novel-Tool
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink TEX",
        extensions: new[] { "tex" }
    )]
    internal sealed class TexImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = EnsureSeekable(stream);
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;
                int len = (int)Math.Min(int.MaxValue, s.Length);
                int tileW = ReadUInt16At(s, 0x38);
                int tileH = ReadUInt16At(s, 0x3A);
                int fW = (int)ReadUInt32At(s, 0x14);
                int fH = (int)ReadUInt32At(s, 0x18);
                int extSize = (int)ReadUInt32At(s, 0x28);
                int baseOff = 0x20;
                int pixelStartExt = baseOff + extSize;
                byte fmt = ReadByteAt(s, 0x2E);
                if (fW <= 0 || fH <= 0 || fW > 16384 || fH > 16384) return null;
                var layout = DetectByFormat(len, tileW, tileH, fW, fH, pixelStartExt, fmt);
                if (!layout.IsValid)
                    layout = DetectBySizeHeuristic(len, tileW, tileH, fW, fH, baseOff, extSize);
                if (!layout.IsValid) return null;
                return layout.HasPalette ? DecodeIndexed(s, layout) : DecodeTrueColor(s, layout);
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

        struct TexLayout
        {
            public bool IsValid;
            public int Width;
            public int Height;
            public int Bpp;
            public bool HasPalette;
            public int PixelOffset;
            public int PaletteOffset;
            public int PaletteSize;
        }

        static TexLayout DetectByFormat(int len, int tileW, int tileH, int fW, int fH, int pixelStartExt, byte fmt)
        {
            var layout = new TexLayout { IsValid = false };
            if (pixelStartExt < 0 || pixelStartExt >= len) return layout;
            long tilePixels = (long)tileW * tileH;
            long fullPixels = (long)fW * fH;
            int bpp;
            bool indexed = false;
            int palSize = 0;
            switch (fmt)
            {
                case 0x00: bpp = 32; break;
                case 0x01: bpp = 24; break;
                case 0x02:
                case 0x0A: bpp = 16; break;
                case 0x13:
                case 0x1B: bpp = 8; indexed = true; palSize = 0x400; break;
                case 0x14:
                case 0x24:
                case 0x2C: bpp = 4; indexed = true; palSize = 0x40; break;
                default: return layout;
            }
            if (tileW > 0 && tileH > 0 &&
                TryMatchLayout(len, pixelStartExt, tilePixels, bpp, indexed, palSize, out layout))
            {
                layout.Width = tileW;
                layout.Height = tileH;
                return layout;
            }
            if (TryMatchLayout(len, pixelStartExt, fullPixels, bpp, indexed, palSize, out layout))
            {
                layout.Width = fW;
                layout.Height = fH;
                return layout;
            }
            layout.IsValid = false;
            return layout;
        }

        static bool TryMatchLayout(int fileLen, int pixelStart, long pixels, int bpp, bool indexed, int palSize, out TexLayout layout)
        {
            layout = new TexLayout { IsValid = false };
            if (pixels <= 0) return false;
            long pixelBytes = bpp switch
            {
                32 => pixels * 4,
                24 => pixels * 3,
                16 => pixels * 2,
                8 => pixels,
                4 => (pixels + 1) / 2,
                _ => 0
            };
            if (pixelBytes <= 0) return false;
            long need = pixelBytes + palSize;
            long have = fileLen - pixelStart;
            if (have != need) return false;
            layout.IsValid = true;
            layout.Bpp = bpp;
            layout.HasPalette = indexed;
            layout.PixelOffset = pixelStart;
            layout.PaletteSize = palSize;
            layout.PaletteOffset = palSize > 0 ? fileLen - palSize : 0;
            return true;
        }

        static TexLayout DetectBySizeHeuristic(int len, int tileW, int tileH, int fW, int fH, int tileDatOff, int tileHdrSize)
        {
            var layout = new TexLayout { IsValid = false };
            int pixelOff = tileDatOff + tileHdrSize;
            int tilePixels = tileW * tileH;
            int fullPixels = fW * fH;
            int tileSize = tilePixels * 4;
            if (tileSize == len - tileDatOff - tileHdrSize)
                return new TexLayout { IsValid = true, Width = tileW, Height = tileH, Bpp = 32, PixelOffset = pixelOff };
            tileSize = tilePixels * 3;
            if (tileSize == len - tileDatOff - tileHdrSize)
                return new TexLayout { IsValid = true, Width = tileW, Height = tileH, Bpp = 24, PixelOffset = pixelOff };
            tileSize = tilePixels * 2;
            if (tileSize == len - tileDatOff - tileHdrSize)
                return new TexLayout { IsValid = true, Width = tileW, Height = tileH, Bpp = 16, PixelOffset = pixelOff };
            tileSize = fullPixels * 4;
            if (tileSize == len - tileDatOff - tileHdrSize)
                return new TexLayout { IsValid = true, Width = fW, Height = fH, Bpp = 32, PixelOffset = pixelOff };
            tileSize = fullPixels * 3;
            if (tileSize == len - tileDatOff - tileHdrSize)
                return new TexLayout { IsValid = true, Width = fW, Height = fH, Bpp = 24, PixelOffset = pixelOff };
            tileSize = fullPixels * 4;
            if (tileSize == len)
                return new TexLayout { IsValid = true, Width = fW, Height = fH, Bpp = 32, PixelOffset = 0 };
            tileSize = fullPixels * 3;
            if (tileSize == len)
                return new TexLayout { IsValid = true, Width = fW, Height = fH, Bpp = 24, PixelOffset = 0 };
            tileSize = tilePixels;
            if (tileSize == len - tileDatOff - tileHdrSize - 0x400)
            {
                return new TexLayout
                {
                    IsValid = true,
                    Width = tileW,
                    Height = tileH,
                    Bpp = 8,
                    HasPalette = true,
                    PixelOffset = pixelOff,
                    PaletteSize = 0x400,
                    PaletteOffset = len - 0x400
                };
            }
            return layout;
        }

        static Image? DecodeTrueColor(Stream s, TexLayout layout)
        {
            int w = layout.Width;
            int h = layout.Height;
            int bpp = layout.Bpp;
            int bytesPerPixel = bpp switch { 16 => 2, 24 => 3, 32 => 4, _ => 0 };
            if (bytesPerPixel == 0) return null;
            long pixels = (long)w * h;
            long needBytes = pixels * bytesPerPixel;
            if (layout.PixelOffset < 0 || layout.PixelOffset + needBytes > s.Length) return null;
            s.Position = layout.PixelOffset;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                if (bpp == 16)
                {
                    var rowSrc = new byte[w * 2];
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        ReadExactlyInto(s, rowSrc, 0, rowSrc.Length);
                        int src = 0, dst = 0;
                        for (int x = 0; x < w; x++)
                        {
                            ushort px = (ushort)(rowSrc[src] | (rowSrc[src + 1] << 8));
                            src += 2;
                            int b5 = (px >> 10) & 0x1F;
                            int g5 = (px >> 5) & 0x1F;
                            int r5 = px & 0x1F;
                            row[dst + 2] = (byte)((r5 << 3) | (r5 >> 2));
                            row[dst + 1] = (byte)((g5 << 3) | (g5 >> 2));
                            row[dst + 0] = (byte)((b5 << 3) | (b5 >> 2));
                            row[dst + 3] = 255;
                            dst += 4;
                        }
                        IntPtr dest = IntPtr.Add(bd.Scan0, y * stride);
                        Marshal.Copy(row, 0, dest, row.Length);
                    }
                }
                else if (bpp == 24)
                {
                    var rowSrc = new byte[w * 3];
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        ReadExactlyInto(s, rowSrc, 0, rowSrc.Length);
                        int src = 0, dst = 0;
                        for (int x = 0; x < w; x++)
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
                else
                {
                    var rowSrc = new byte[w * 4];
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        ReadExactlyInto(s, rowSrc, 0, rowSrc.Length);
                        int src = 0, dst = 0;
                        for (int x = 0; x < w; x++)
                        {
                            byte r = rowSrc[src++];
                            byte g = rowSrc[src++];
                            byte b = rowSrc[src++];
                            byte a = FixAlpha(rowSrc[src++]);
                            row[dst + 0] = b;
                            row[dst + 1] = g;
                            row[dst + 2] = r;
                            row[dst + 3] = a;
                            dst += 4;
                        }
                        IntPtr dest = IntPtr.Add(bd.Scan0, y * stride);
                        Marshal.Copy(row, 0, dest, row.Length);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }

        static Image? DecodeIndexed(Stream s, TexLayout layout)
        {
            int w = layout.Width;
            int h = layout.Height;
            int bpp = layout.Bpp;
            if (layout.PaletteOffset <= 0 || layout.PaletteOffset + layout.PaletteSize > s.Length) return null;
            s.Position = layout.PaletteOffset;
            var palRaw = ReadExactly(s, layout.PaletteSize);
            if (palRaw.Length < layout.PaletteSize) throw new EndOfStreamException();
            if (bpp == 8)
            {
                var original = new (byte r, byte g, byte b, byte a)[256];
                for (int i = 0; i < 256; i++)
                {
                    int p = i * 4;
                    original[i] = (palRaw[p + 0], palRaw[p + 1], palRaw[p + 2], FixAlpha(palRaw[p + 3]));
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
                if (layout.PixelOffset < 0 || layout.PixelOffset >= s.Length) return null;
                s.Position = layout.PixelOffset;
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
                        ReadExactlyInto(s, rowIdx, 0, rowIdx.Length);
                        int dst = 0;
                        for (int x = 0; x < w; x++)
                        {
                            var p = pal[rowIdx[x]];
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
            else if (bpp == 4)
            {
                long pixels = (long)w * h;
                long pixelBytes = (pixels + 1) / 2;
                if (layout.PixelOffset < 0 || layout.PixelOffset + pixelBytes > layout.PaletteOffset) return null;
                var pal16 = new byte[16 * 4];
                for (int i = 0; i < 16; i++)
                {
                    int p = i * 4;
                    pal16[p + 0] = palRaw[p + 2];
                    pal16[p + 1] = palRaw[p + 1];
                    pal16[p + 2] = palRaw[p + 0];
                    pal16[p + 3] = FixAlpha(palRaw[p + 3]);
                }
                s.Position = layout.PixelOffset;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = bd.Stride;
                    var packed = new byte[(w + 1) / 2];
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        ReadExactlyInto(s, packed, 0, packed.Length);
                        int dst = 0;
                        for (int x = 0; x < w; x++)
                        {
                            int b = packed[x >> 1];
                            int idx = (x & 1) == 0 ? (b & 0x0F) : ((b >> 4) & 0x0F);
                            int pi = idx * 4;
                            row[dst + 0] = pal16[pi + 0];
                            row[dst + 1] = pal16[pi + 1];
                            row[dst + 2] = pal16[pi + 2];
                            row[dst + 3] = pal16[pi + 3];
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
            return null;
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

        static byte ReadByteAt(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int v = s.ReadByte();
            s.Position = save;
            return v < 0 ? (byte)0 : (byte)v;
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
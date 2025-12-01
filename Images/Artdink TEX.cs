// Some parts of this implementation are based on:
// https://github.com/punk7890/PS2-Visual-Novel-Tool

using System;
using System.Drawing;
using System.Drawing.Imaging;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink TEX",
        extensions: new[] { "tex" }
    )]
    internal sealed class TexImageHandler : IImageHandler
    {
        public Image? TryDecode(byte[] data, string extension)
        {
            if (data == null || data.Length < 0x40)
                return null;

            if (string.IsNullOrEmpty(extension) ||
                !extension.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                return null;

            int len = data.Length;
            int tileW = ReadUInt16LE(data, 0x38);
            int tileH = ReadUInt16LE(data, 0x3A);
            int fW = (int)ReadUInt32LE(data, 0x14);
            int fH = (int)ReadUInt32LE(data, 0x18);
            int extSize = (int)ReadUInt32LE(data, 0x28);
            int baseOff = 0x20;
            int pixelStartExt = baseOff + extSize;
            byte fmt = data[0x2E];

            if (fW <= 0 || fH <= 0 || fW > 16384 || fH > 16384)
                return null;

            TexLayout layout = DetectByFormat(len, tileW, tileH, fW, fH, pixelStartExt, fmt);
            if (!layout.IsValid)
                layout = DetectBySizeHeuristic(len, tileW, tileH, fW, fH, baseOff, extSize);

            if (!layout.IsValid)
                return null;

            try
            {
                return layout.HasPalette
                    ? DecodeIndexed(data, layout)
                    : DecodeTrueColor(data, layout);
            }
            catch
            {
                return null;
            }
        }

        private struct TexLayout
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

        private static TexLayout DetectByFormat(
            int len,
            int tileW,
            int tileH,
            int fW,
            int fH,
            int pixelStartExt,
            byte fmt)
        {
            var layout = new TexLayout { IsValid = false };
            if (pixelStartExt < 0 || pixelStartExt >= len)
                return layout;

            long tilePixels = (long)tileW * tileH;
            long fullPixels = (long)fW * fH;

            int bitsPerPixel;
            bool indexed = false;
            int palSize = 0;

            switch (fmt)
            {
                case 0x00: bitsPerPixel = 32; break;
                case 0x01: bitsPerPixel = 24; break;
                case 0x02:
                case 0x0A: bitsPerPixel = 16; break;
                case 0x13:
                case 0x1B:
                    bitsPerPixel = 8; indexed = true; palSize = 0x400; break;
                case 0x14:
                case 0x24:
                case 0x2C:
                    bitsPerPixel = 4; indexed = true; palSize = 0x40; break;
                default:
                    return layout;
            }

            if (tileW > 0 && tileH > 0 &&
                TryMatchLayout(len, pixelStartExt, tilePixels, bitsPerPixel, indexed, palSize, out layout))
            {
                layout.Width = tileW;
                layout.Height = tileH;
                return layout;
            }

            if (TryMatchLayout(len, pixelStartExt, fullPixels, bitsPerPixel, indexed, palSize, out layout))
            {
                layout.Width = fW;
                layout.Height = fH;
                return layout;
            }

            layout.IsValid = false;
            return layout;
        }

        private static bool TryMatchLayout(
            int fileLen,
            int pixelStart,
            long pixels,
            int bitsPerPixel,
            bool indexed,
            int palSize,
            out TexLayout layout)
        {
            layout = new TexLayout { IsValid = false };
            if (pixels <= 0) return false;

            long pixelBytes = bitsPerPixel switch
            {
                32 => pixels * 4,
                24 => pixels * 3,
                16 => pixels * 2,
                8  => pixels,
                4  => (pixels + 1) / 2,
                _  => 0
            };
            if (pixelBytes <= 0) return false;

            long need = pixelBytes + palSize;
            long have = fileLen - pixelStart;
            if (have != need) return false;

            layout.IsValid = true;
            layout.Bpp = bitsPerPixel;
            layout.HasPalette = indexed;
            layout.PixelOffset = pixelStart;
            layout.PaletteSize = palSize;
            layout.PaletteOffset = palSize > 0 ? fileLen - palSize : 0;
            return true;
        }

        private static TexLayout DetectBySizeHeuristic(
            int len,
            int tileW,
            int tileH,
            int fW,
            int fH,
            int tileDatOff,
            int tileHdrSize)
        {
            var layout = new TexLayout { IsValid = false };
            int pixelOff = tileDatOff + tileHdrSize;

            int tilePixels = tileW * tileH;
            int fullPixels = fW * fH;

            while (true)
            {
                int tileSize = tilePixels * 4;
                if (tileSize == len - tileDatOff - tileHdrSize)
                {
                    layout.IsValid = true;
                    layout.Width = tileW;
                    layout.Height = tileH;
                    layout.Bpp = 32;
                    layout.HasPalette = false;
                    layout.PixelOffset = pixelOff;
                    return layout;
                }

                tileSize = tilePixels * 3;
                if (tileSize == len - tileDatOff - tileHdrSize)
                {
                    layout.IsValid = true;
                    layout.Width = tileW;
                    layout.Height = tileH;
                    layout.Bpp = 24;
                    layout.HasPalette = false;
                    layout.PixelOffset = pixelOff;
                    return layout;
                }

                tileSize = tilePixels * 2;
                if (tileSize == len - tileDatOff - tileHdrSize)
                {
                    layout.IsValid = true;
                    layout.Width = tileW;
                    layout.Height = tileH;
                    layout.Bpp = 16;
                    layout.HasPalette = false;
                    layout.PixelOffset = pixelOff;
                    return layout;
                }

                tileSize = fullPixels * 4;
                if (tileSize == len - tileDatOff - tileHdrSize)
                {
                    layout.IsValid = true;
                    layout.Width = fW;
                    layout.Height = fH;
                    layout.Bpp = 32;
                    layout.HasPalette = false;
                    layout.PixelOffset = pixelOff;
                    return layout;
                }

                tileSize = fullPixels * 3;
                if (tileSize == len - tileDatOff - tileHdrSize)
                {
                    layout.IsValid = true;
                    layout.Width = fW;
                    layout.Height = fH;
                    layout.Bpp = 24;
                    layout.HasPalette = false;
                    layout.PixelOffset = pixelOff;
                    return layout;
                }

                tileSize = fullPixels * 4;
                if (tileSize == len)
                {
                    layout.IsValid = true;
                    layout.Width = fW;
                    layout.Height = fH;
                    layout.Bpp = 32;
                    layout.HasPalette = false;
                    layout.PixelOffset = 0;
                    return layout;
                }

                tileSize = fullPixels * 3;
                if (tileSize == len)
                {
                    layout.IsValid = true;
                    layout.Width = fW;
                    layout.Height = fH;
                    layout.Bpp = 24;
                    layout.HasPalette = false;
                    layout.PixelOffset = 0;
                    return layout;
                }

                tileSize = tilePixels;
                if (tileSize == len - tileDatOff - tileHdrSize - 0x400)
                {
                    layout.IsValid = true;
                    layout.Width = tileW;
                    layout.Height = tileH;
                    layout.Bpp = 8;
                    layout.HasPalette = true;
                    layout.PixelOffset = pixelOff;
                    layout.PaletteSize = 0x400;
                    layout.PaletteOffset = len - 0x400;
                    return layout;
                }

                return layout;
            }
        }

        private static Image? DecodeTrueColor(byte[] data, TexLayout layout)
        {
            int width = layout.Width;
            int height = layout.Height;
            int bpp = layout.Bpp;
            int off = layout.PixelOffset;
            int len = data.Length;

            long pixels = (long)width * height;
            int bytesPerPixel = bpp switch
            {
                16 => 2,
                24 => 3,
                32 => 4,
                _  => 0
            };
            if (bytesPerPixel == 0) return null;

            long needBytes = pixels * bytesPerPixel;
            if (off < 0 || off + needBytes > len)
                return null;

            if (bpp == 16)
            {
                var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        ushort px = (ushort)(data[off] | (data[off + 1] << 8));
                        off += 2;

                        int b5 = (px >> 10) & 0x1F;
                        int g5 = (px >> 5) & 0x1F;
                        int r5 = px & 0x1F;

                        int r = (r5 << 3) | (r5 >> 2);
                        int g = (g5 << 3) | (g5 >> 2);
                        int b = (b5 << 3) | (b5 >> 2);

                        bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
                    }
                }

                return bmp;
            }
            else if (bpp == 24)
            {
                var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte r = data[off];
                        byte g = data[off + 1];
                        byte b = data[off + 2];
                        off += 3;

                        bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
                    }
                }

                return bmp;
            }
            else // 32
            {
                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte r = data[off];
                        byte g = data[off + 1];
                        byte b = data[off + 2];
                        byte a = FixAlpha(data[off + 3]);
                        off += 4;

                        bmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
                    }
                }

                return bmp;
            }
        }

        private static Image? DecodeIndexed(byte[] data, TexLayout layout)
        {
            int width = layout.Width;
            int height = layout.Height;
            int off = layout.PixelOffset;
            int palOff = layout.PaletteOffset;
            int palSize = layout.PaletteSize;
            int len = data.Length;
            int bpp = layout.Bpp;

            if (palOff <= 0 || palOff + palSize > len)
                return null;

            long pixels = (long)width * height;

            if (bpp == 8)
            {
                int pixelBytes = palOff - off;
                if (pixelBytes < pixels)
                    return null;

                byte[] palRaw = new byte[palSize];
                Buffer.BlockCopy(data, palOff, palRaw, 0, palSize);

                var original = new (byte r, byte g, byte b, byte a)[256];
                for (int i = 0; i < 256; i++)
                {
                    int p = i * 4;
                    byte r = palRaw[p];
                    byte g = palRaw[p + 1];
                    byte b = palRaw[p + 2];
                    byte a = FixAlpha(palRaw[p + 3]);
                    original[i] = (r, g, b, a);
                }

                var palette = new (byte r, byte g, byte b, byte a)[256];
                int dst = 0;
                for (int major = 0; major < 256; major += 32)
                {
                    for (int i = 0; i < 8; i++) palette[dst++] = original[major + i];
                    for (int i = 16; i < 24; i++) palette[dst++] = original[major + i];
                    for (int i = 8; i < 16; i++) palette[dst++] = original[major + i];
                    for (int i = 24; i < 32; i++) palette[dst++] = original[major + i];
                }

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                int idx = off;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (idx >= palOff) break;
                        byte pi = data[idx++];
                        var p = palette[pi];
                        bmp.SetPixel(x, y, Color.FromArgb(p.a, p.r, p.g, p.b));
                    }
                }

                return bmp;
            }
            else if (bpp == 4)
            {
                long pixelBytes = (pixels + 1) / 2;
                if (off + pixelBytes > palOff)
                    return null;

                var palette = new (byte r, byte g, byte b, byte a)[16];
                for (int i = 0; i < 16; i++)
                {
                    int p = palOff + i * 4;
                    byte r = data[p];
                    byte g = data[p + 1];
                    byte b = data[p + 2];
                    byte a = FixAlpha(data[p + 3]);
                    palette[i] = (r, g, b, a);
                }

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        long pos = (long)y * width + x;
                        long bytePos = pos / 2;
                        if (off + bytePos >= palOff) break;

                        byte packed = data[off + bytePos];
                        int nibbleIndex = (int)(pos % 2);
                        int index = (packed >> (4 * nibbleIndex)) & 0x0F;

                        var p = palette[index];
                        bmp.SetPixel(x, y, Color.FromArgb(p.a, p.r, p.g, p.b));
                    }
                }

                return bmp;
            }

            return null;
        }

        private static int ReadUInt16LE(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return 0;
            return data[offset] | (data[offset + 1] << 8);
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return (uint)(data[offset]
                          | (data[offset + 1] << 8)
                          | (data[offset + 2] << 16)
                          | (data[offset + 3] << 24));
        }

        private static byte FixAlpha(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }
    }
}
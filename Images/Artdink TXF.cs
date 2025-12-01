using System;
using System.Drawing;
using System.Drawing.Imaging;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink TXF",
        extensions: new[] { "txf" }
    )]
    internal sealed class TxfImageHandler : IImageHandler
    {
        public Image? TryDecode(byte[] data, string extension)
        {
            if (data == null || data.Length < 0x40)
                return null;

            if (!extension.EndsWith(".txf", StringComparison.OrdinalIgnoreCase))
                return null;

            // "TXF "
            if (data[0] != (byte)'T' || data[1] != (byte)'X' ||
                data[2] != (byte)'F' || data[3] != (byte)' ')
                return null;

            int len = data.Length;

            // glyph offset table at +0x30, first glyph offset
            int glyphOff0 = ReadInt32LE(data, 0x30);
            if (glyphOff0 < 0 || glyphOff0 + 0x20 > len)
                return null;

            int glyph = glyphOff0;

            // basic glyph header
            ushort count = ReadUInt16LE(data, glyph + 0x04);
            if (count == 0)
                return null;

            int pixelRel = ReadInt32LE(data, glyph + 0x08);
            int pixelPtr = glyph + pixelRel;
            if (pixelPtr < 0 || pixelPtr >= len)
                return null;

            byte fmt = data[glyph + 0x0E];

            // only 8bpp indexed (0x13 / 0x1B) is handled here
            if (fmt != 0x13 && fmt != 0x1B)
                return null;

            // width / height from +0x18 / +0x1A
            int width = ReadUInt16LE(data, glyph + 0x18);
            int height = ReadUInt16LE(data, glyph + 0x1A);

            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return null;

            long pixels = (long)width * height;
            if (pixels <= 0)
                return null;

            const int paletteSize = 0x400;
            if (len < paletteSize + pixelPtr)
                return null;

            int palOffset = len - paletteSize;
            int pixelBytes = palOffset - pixelPtr;
            if (pixelBytes < pixels)
                return null;

            // read palette
            byte[] palRaw = new byte[paletteSize];
            Buffer.BlockCopy(data, palOffset, palRaw, 0, paletteSize);

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

            // palette swizzle, same as AGI/FAC/TEX
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
            int idx = pixelPtr;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (idx >= palOffset)
                        break;

                    byte pi = data[idx++];
                    var p = palette[pi];
                    bmp.SetPixel(x, y, Color.FromArgb(p.a, p.r, p.g, p.b));
                }
            }

            return bmp;
        }

        private static int ReadInt32LE(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return data[offset]
                   | (data[offset + 1] << 8)
                   | (data[offset + 2] << 16)
                   | (data[offset + 3] << 24);
        }

        private static ushort ReadUInt16LE(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return 0;
            return (ushort)(data[offset] | (data[offset + 1] << 8));
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
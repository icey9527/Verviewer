using System;
using System.Drawing;
using System.Drawing.Imaging;
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
            0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x10, 0x00, 0x10, 0x00
        };

        public Image? TryDecode(byte[] data, string extension)
        {
            if (data == null || data.Length < 0x100)
                return null;

            if (!extension.EndsWith(".fac", StringComparison.OrdinalIgnoreCase))
                return null;

            int pos = IndexOf(data, MagicPattern);
            if (pos < 0)
                return null;

            try
            {
                return DecodeFirstLayer(data, pos);
            }
            catch
            {
                return null;
            }
        }

        private static Image? DecodeFirstLayer(byte[] data, int patternPos)
        {
            int headerStart = patternPos - 0x40;
            if (headerStart < 0 || headerStart + 0x3C > data.Length)
                return null;

            ushort w = BitConverter.ToUInt16(data, headerStart + 0x38);
            ushort h = BitConverter.ToUInt16(data, headerStart + 0x3A);
            if (w == 0 || h == 0)
                return null;

            long pixelCount = (long)w * h;
            long pixelStart = patternPos + 16;
            long palStart = pixelStart + pixelCount;
            if (palStart + 0x400 > data.Length)
                return null;

            Color[] palette = BuildPalette(data, (int)palStart);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            int pixOff = (int)pixelStart;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixOff >= palStart)
                        return bmp;

                    byte idx = data[pixOff++];
                    bmp.SetPixel(x, y, palette[idx]);
                }
            }

            return bmp;
        }

        private static Color[] BuildPalette(byte[] data, int palOffset)
        {
            var original = new (byte R, byte G, byte B, byte A)[256];
            for (int i = 0; i < 256; i++)
            {
                int off = palOffset + i * 4;
                byte r = data[off];
                byte g = data[off + 1];
                byte b = data[off + 2];
                byte a = FixAlpha(data[off + 3]);
                original[i] = (r, g, b, a);
            }

            var palette = new Color[256];
            int dst = 0;

            for (int major = 0; major < 256; major += 32)
            {
                for (int i = 0; i < 8; i++)
                {
                    var p = original[major + i];
                    palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B);
                }
                for (int i = 16; i < 24; i++)
                {
                    var p = original[major + i];
                    palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B);
                }
                for (int i = 8; i < 16; i++)
                {
                    var p = original[major + i];
                    palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B);
                }
                for (int i = 24; i < 32; i++)
                {
                    var p = original[major + i];
                    palette[dst++] = Color.FromArgb(p.A, p.R, p.G, p.B);
                }
            }

            return palette;
        }

        private static byte FixAlpha(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            if (pattern.Length == 0 || data.Length < pattern.Length)
                return -1;

            int max = data.Length - pattern.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }
    }
}
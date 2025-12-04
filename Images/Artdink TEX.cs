// Implementation ported/adapted from:
// punk7890/PS2-Visual-Novel-Tool
// https://github.com/punk7890/PS2-Visual-Novel-Tool
// Original copyright (c) 2024 punk7890

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils;

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
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;

                int len = (int)Math.Min(int.MaxValue, s.Length);

                int tileW = s.ReadUInt16LEAt(0x38);
                int tileH = s.ReadUInt16LEAt(0x3A);
                int fW = (int)s.ReadUInt32LEAt(0x14);
                int fH = (int)s.ReadUInt32LEAt(0x18);
                int extSize = (int)s.ReadUInt32LEAt(0x28);
                int baseOff = 0x20;
                int pixelStartExt = baseOff + extSize;
                byte fmt = s.ReadByteAt(0x2E);

                if (fW <= 0 || fH <= 0 || fW > 16384 || fH > 16384)
                    return null;

                var layout = DetectByFormat(len, tileW, tileH, fW, fH, pixelStartExt, fmt);
                if (!layout.IsValid)
                    layout = DetectBySizeHeuristic(len, tileW, tileH, fW, fH, baseOff, extSize);

                if (!layout.IsValid) return null;

                return layout.HasPalette
                    ? DecodeIndexed(s, layout)
                    : DecodeTrueColor(s, layout);
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

            int bytesPerPixel = bpp switch
            {
                16 => 2,
                24 => 3,
                32 => 4,
                _ => 0
            };
            if (bytesPerPixel == 0) return null;

            long pixels = (long)w * h;
            long needBytes = pixels * bytesPerPixel;
            if (layout.PixelOffset < 0 || layout.PixelOffset + needBytes > s.Length)
                return null;

            s.Position = layout.PixelOffset;

            var bmp = ImageUtils.CreateArgbBitmap(w, h, out var bd, out int stride);
            var srcRow = new byte[w * bytesPerPixel];
            var row = new byte[w * 4];

            try
            {
                for (int y = 0; y < h; y++)
                {
                    s.ReadExactly(srcRow, 0, srcRow.Length);

                    switch (bpp)
                    {
                        case 16:
                            // RGB555, 无 Alpha
                            ImageUtils.ConvertRowRgb555ToBgra(srcRow, row, w);
                            break;
                        case 24:
                            // R,G,B
                            ImageUtils.ConvertRowRgb24ToBgra(srcRow, row, w);
                            break;
                        case 32:
                            // RGBA + PS2 Alpha 映射
                            ImageUtils.ConvertRowRgba32ToBgraWithPs2Alpha(srcRow, row, w);
                            break;
                    }

                    ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                }
            }
            catch
            {
                ImageUtils.UnlockBitmap(bd, bmp);
                bmp.Dispose();
                throw;
            }

            ImageUtils.UnlockBitmap(bd, bmp);
            return bmp;
        }

        static Image? DecodeIndexed(Stream s, TexLayout layout)
        {
            int w = layout.Width;
            int h = layout.Height;
            int bpp = layout.Bpp;

            if (layout.PaletteOffset <= 0 || layout.PaletteOffset + layout.PaletteSize > s.Length)
                return null;

            s.Position = layout.PaletteOffset;
            byte[] palRaw = s.ReadExactly(layout.PaletteSize);

            if (bpp == 8)
            {
                // 8bpp: 256 色 RGBA 调色板, PS2 风格 32 色块重排 + PS2 Alpha
                byte[] paletteBgra = ImageUtils.BuildPs2Palette256Bgra_Block32(palRaw);

                if (layout.PixelOffset < 0 || layout.PixelOffset >= s.Length)
                    return null;

                s.Position = layout.PixelOffset;

                var bmp = ImageUtils.CreateArgbBitmap(w, h, out var bd, out int stride);
                var rowIdx = new byte[w];
                var row = new byte[w * 4];

                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        s.ReadExactly(rowIdx, 0, rowIdx.Length);
                        ImageUtils.ConvertRowIndexed8ToBgra(rowIdx, row, w, paletteBgra);
                        ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                    }
                }
                catch
                {
                    ImageUtils.UnlockBitmap(bd, bmp);
                    bmp.Dispose();
                    throw;
                }

                ImageUtils.UnlockBitmap(bd, bmp);
                return bmp;
            }
            else if (bpp == 4)
            {
                long pixels = (long)w * h;
                long pixelBytes = (pixels + 1) / 2;
                if (layout.PixelOffset < 0 || layout.PixelOffset + pixelBytes > layout.PaletteOffset)
                    return null;

                // 4bpp: 16 色调色板, 无重排, 只做 RGBA->BGRA + PS2 Alpha
                byte[] paletteBgra = ImageUtils.BuildPaletteBgraFromRgba(palRaw, 16, applyPs2AlphaFix: true);

                s.Position = layout.PixelOffset;

                var bmp = ImageUtils.CreateArgbBitmap(w, h, out var bd, out int stride);
                var packed = new byte[(w + 1) / 2];
                var row = new byte[w * 4];

                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        s.ReadExactly(packed, 0, packed.Length);
                        ImageUtils.ConvertRowIndexed4ToBgra(packed, row, w, paletteBgra);
                        ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                    }
                }
                catch
                {
                    ImageUtils.UnlockBitmap(bd, bmp);
                    bmp.Dispose();
                    throw;
                }

                ImageUtils.UnlockBitmap(bd, bmp);
                return bmp;
            }

            return null;
        }
    }
}
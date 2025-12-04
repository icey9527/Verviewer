using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils; // StreamUtils, ImageUtils

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
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;

                int len = (int)Math.Min(int.MaxValue, s.Length);

                int glyphOff0 = s.ReadInt32LEAt(0x30);
                if (glyphOff0 < 0 || glyphOff0 + 0x20 > len) return null;

                int glyph = glyphOff0;

                ushort count = (ushort)s.ReadUInt16LEAt(glyph + 0x04);
                if (count == 0) return null;

                int pixelRel = s.ReadInt32LEAt(glyph + 0x08);
                int pixelPtr = glyph + pixelRel;
                if (pixelPtr < 0 || pixelPtr >= len) return null;

                byte fmt = s.ReadByteAt(glyph + 0x0E);
                if (fmt != 0x13 && fmt != 0x1B) return null; // 8bpp indexed

                int width = s.ReadUInt16LEAt(glyph + 0x18);
                int height = s.ReadUInt16LEAt(glyph + 0x1A);
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

                long pixels = (long)width * height;
                if (pixels <= 0) return null;

                const int paletteSize = 0x400;
                if (len < paletteSize + pixelPtr) return null;

                int palOffset = len - paletteSize;
                int pixelBytes = palOffset - pixelPtr;
                if (pixelBytes < pixels) return null;

                // 读取调色板: 256 * 4 RGBA
                s.Position = palOffset;
                byte[] palRaw = s.ReadExactly(paletteSize);

                // 先做 PS2 32 色块重排, 再做 RGBA->BGRA + PS2 Alpha
                byte[] paletteBgra = ImageUtils.BuildPs2Palette256Bgra_Block32(palRaw);

                // 读取像素索引并绘制
                s.Position = pixelPtr;

                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
                var rowIdx = new byte[width];
                var row = new byte[width * 4];

                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        s.ReadExactly(rowIdx, 0, rowIdx.Length);
                        ImageUtils.ConvertRowIndexed8ToBgra(rowIdx, row, width, paletteBgra);
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
    }
}
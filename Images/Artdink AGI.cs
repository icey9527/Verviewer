using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils; // StreamUtils, ImageUtils

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
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x30) return null;

                int width  = s.ReadUInt16LEAt(0x18);
                int height = s.ReadUInt16LEAt(0x1A);
                if (width <= 0 || height <= 0) return null;

                byte[] flag = s.ReadBytesAt(0x2C, 4);
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
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }

        // 4bpp
        static Image? Read4bpp(Stream s, int width, int height)
        {
            uint palOff = s.ReadUInt32LEAt(0x1C);
            if (palOff == 0) return null;
            if (palOff + 16 * 4 > s.Length) return null;

            s.Position = palOff;
            byte[] palRaw = s.ReadExactly(16 * 4);

            byte[] paletteBgra = ImageUtils.BuildPaletteBgraFromRgba(palRaw, 16, applyPs2AlphaFix: true);

            int pixelDataOffset = 0x30;
            if (pixelDataOffset >= s.Length) return null;
            s.Position = pixelDataOffset;

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var packed = new byte[(width + 1) / 2];
            var row = new byte[width * 4];

            try
            {
                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(packed, 0, packed.Length);
                    ImageUtils.ConvertRowIndexed4ToBgra(packed, row, width, paletteBgra);
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

        // 8bpp + PS2 调色板重排
        static Image? Read8bpp(Stream s, int width, int height)
        {
            int palOff = ReadUInt24At(s, 0x1C);
            if (palOff <= 0) return null;
            if (palOff + 256 * 4 > s.Length) return null;

            s.Position = palOff;
            byte[] palData = s.ReadExactly(256 * 4); // RGBA

            // 使用公共的 PS2 256 色调色板构建函数
            byte[] paletteBgra = ImageUtils.BuildPs2Palette256Bgra_Block32(palData);

            int pixelDataOffset = 0x30;
            if (pixelDataOffset >= s.Length) return null;
            s.Position = pixelDataOffset;

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var idxRow = new byte[width];
            var row = new byte[width * 4];

            try
            {
                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(idxRow, 0, idxRow.Length);
                    ImageUtils.ConvertRowIndexed8ToBgra(idxRow, row, width, paletteBgra);
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

        // 16bpp RGB555
        static Image? Read16bpp(Stream s, int width, int height)
        {
            int pixelDataOffset = 0x20;
            long need = (long)width * height * 2;
            if (pixelDataOffset + need > s.Length) return null;

            s.Position = pixelDataOffset;

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var rowSrc = new byte[width * 2];
            var row = new byte[width * 4];

            try
            {
                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(rowSrc, 0, rowSrc.Length);
                    ImageUtils.ConvertRowRgb555ToBgra(rowSrc, row, width);
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

        // 24bpp RGB
        static Image? Read24bpp(Stream s, int width, int height)
        {
            int pixelDataOffset = 0x50;
            long need = (long)width * height * 3;
            if (pixelDataOffset + need > s.Length) return null;

            s.Position = pixelDataOffset;

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var rowSrc = new byte[width * 3];
            var row = new byte[width * 4];

            try
            {
                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(rowSrc, 0, rowSrc.Length);
                    ImageUtils.ConvertRowRgb24ToBgra(rowSrc, row, width);
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

        // 辅助: 24bit 小端读取 + 十六进制字符串

        static int ReadUInt24At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2) < 0)
                throw new EndOfStreamException();
            return b0 | (b1 << 8) | (b2 << 16);
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
using System;
using System.Drawing;
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
        struct AgiEntry
        {
            public uint   DataOffset; // +0x00
            public uint   Reg1;       // +0x04
            public uint   Reg2;       // +0x08
            public uint   Reg3;       // +0x0C
            public ushort Width;      // +0x10
            public ushort Height;     // +0x12
        }

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x20)
                    return null;

                ushort imageCount = s.ReadUInt16LEAt(0x04);
                ushort clutCount  = s.ReadUInt16LEAt(0x06);
                if (imageCount == 0)
                    return null;

                var texEntry = ReadEntryAt(s, 0x08);
                if (texEntry.Width == 0 || texEntry.Height == 0)
                    return null;

                int  width  = texEntry.Width;
                int  height = texEntry.Height;
                byte psm    = (byte)((texEntry.Reg1 >> 16) & 0xFF);
                int  psmIdx = psm & 7;
                bool hasClut = clutCount > 0;

                AgiEntry clutEntry = default;
                if (hasClut)
                {
                    long clutEntryOff = 0x08 + imageCount * 20;
                    if (clutEntryOff + 20 > s.Length)
                        return null;
                    clutEntry = ReadEntryAt(s, clutEntryOff);
                }

                if (hasClut && psmIdx == 3)
                    return Read8bpp(s, width, height, texEntry, clutEntry);
                if (hasClut && psmIdx == 4)
                    return Read4bpp(s, width, height, texEntry, clutEntry);
                if (!hasClut && psmIdx == 2)
                    return Read16bpp(s, width, height, texEntry);
                if (!hasClut && psmIdx == 1)
                    return Read24bpp(s, width, height, texEntry);

                return null;
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

        static AgiEntry ReadEntryAt(Stream s, long offset)
        {
            AgiEntry e;
            e.DataOffset = s.ReadUInt32LEAt(offset + 0);
            e.Reg1       = s.ReadUInt32LEAt(offset + 4);
            e.Reg2       = s.ReadUInt32LEAt(offset + 8);
            e.Reg3       = s.ReadUInt32LEAt(offset + 12);
            e.Width      = s.ReadUInt16LEAt(offset + 16);
            e.Height     = s.ReadUInt16LEAt(offset + 18);
            return e;
        }

        static Image? Read4bpp(Stream s, int width, int height, AgiEntry tex, AgiEntry clut)
        {
            uint palOff = clut.DataOffset;
            if (palOff == 0) return null;
            if (palOff + 16 * 4 > s.Length) return null;

            s.Position = palOff;
            byte[] palRaw = s.ReadExactly(16 * 4);
            byte[] paletteBgra = ImageUtils.BuildPaletteBgraFromRgba(palRaw, 16, applyPs2AlphaFix: true);

            int pixelDataOffset = (int)tex.DataOffset;
            if (pixelDataOffset >= s.Length) return null;
            long need = ((long)(width + 1) / 2) * height;
            if (pixelDataOffset + need > s.Length) return null;

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

        static Image? Read8bpp(Stream s, int width, int height, AgiEntry tex, AgiEntry clut)
        {
            uint palOff = clut.DataOffset;
            if (palOff == 0) return null;
            if (palOff + 256 * 4 > s.Length) return null;

            s.Position = palOff;
            byte[] palData = s.ReadExactly(256 * 4);
            byte[] paletteBgra = ImageUtils.BuildPs2Palette256Bgra_Block32(palData);

            int pixelDataOffset = (int)tex.DataOffset;
            if (pixelDataOffset >= s.Length) return null;
            long need = (long)width * height;
            if (pixelDataOffset + need > s.Length) return null;

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

        static Image? Read16bpp(Stream s, int width, int height, AgiEntry tex)
        {
            int pixelDataOffset = (int)tex.DataOffset;
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

        static Image? Read24bpp(Stream s, int width, int height, AgiEntry tex)
        {
            int pixelDataOffset = (int)tex.DataOffset;
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
    }
}
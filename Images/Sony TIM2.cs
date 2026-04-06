using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Sony TIM2",
        extensions: new[] { "tim2" },
        magics: new[] { "TIM2" }
    )]
    internal sealed class Sony_TIM2 : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            return SonyTim2Common.TryDecode(stream, true);
        }
    }

    [ImagePlugin(
        id: "Sony TIM2",
        extensions: new[] { "tm2" },
        magics: new[] { "TIM2" }
    )]
    internal sealed class Sony_TM2 : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            return SonyTim2Common.TryDecode(stream, false);
        }
    }

    internal static class SonyTim2Common
    {
        public static Image? TryDecode(Stream stream, bool applyPs2Alpha)
        {
            Stream s = stream.EnsureSeekable();

            try
            {
                if (!s.CanSeek || s.Length < 0x40)
                    return null;

                byte alignment = s.WithTemporarySeek(5, x => (byte)x.ReadByte());

                long pictureHeaderOffset = 0x10;
                if (alignment != 0)
                    pictureHeaderOffset += 0x70;

                if (pictureHeaderOffset + 0x30 > s.Length)
                    return null;

                uint clutSize = s.ReadUInt32LEAt(pictureHeaderOffset + 0x04);
                uint imageSize = s.ReadUInt32LEAt(pictureHeaderOffset + 0x08);
                ushort headerSize = s.ReadUInt16LEAt(pictureHeaderOffset + 0x0C);
                byte imageType = s.WithTemporarySeek(pictureHeaderOffset + 0x13, x => (byte)x.ReadByte());
                ushort width = s.ReadUInt16LEAt(pictureHeaderOffset + 0x14);
                ushort height = s.ReadUInt16LEAt(pictureHeaderOffset + 0x16);

                if (width == 0 || height == 0)
                    return null;

                long imageOffset = pictureHeaderOffset + headerSize;
                long clutOffset = imageOffset + imageSize;

                if (imageOffset + imageSize > s.Length)
                    return null;

                if (clutOffset + clutSize > s.Length)
                    return null;

                if (clutSize == 0 || imageType == 3)
                    return Decode32(s, imageOffset, imageSize, width, height, applyPs2Alpha);

                return Decode8(s, imageOffset, clutOffset, width, height, applyPs2Alpha);
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

        static Image? Decode32(Stream s, long imageOffset, uint imageSize, int width, int height, bool applyPs2Alpha)
        {
            if ((long)width * height * 4 > imageSize)
                return null;

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var srcRow = new byte[width * 4];
            var row = new byte[width * 4];

            try
            {
                s.Position = imageOffset;

                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(srcRow, 0, srcRow.Length);

                    if (applyPs2Alpha)
                        ImageUtils.ConvertRowRgba32ToBgraWithPs2Alpha(srcRow, row, width);
                    else
                        ImageUtils.ConvertRowRgba32ToBgra(srcRow, row, width);

                    ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                }

                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bd, bmp);
            }
        }

        static Image? Decode8(Stream s, long imageOffset, long clutOffset, int width, int height, bool applyPs2Alpha)
        {
            if (imageOffset + (long)width * height > s.Length)
                return null;

            s.Position = clutOffset;
            byte[] palRaw = new byte[256 * 4];
            s.ReadExactly(palRaw, 0, palRaw.Length);

            byte[] palette = ImageUtils.BuildPs2Palette256Bgra_Block32(palRaw, applyPs2Alpha);

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
            var idxRow = new byte[width];
            var row = new byte[width * 4];

            try
            {
                s.Position = imageOffset;

                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(idxRow, 0, idxRow.Length);
                    ImageUtils.ConvertRowIndexed8ToBgra(idxRow, row, width, palette);
                    ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                }

                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bd, bmp);
            }
        }
    }
}
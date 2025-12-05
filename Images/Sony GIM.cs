// Implementation ported/adapted from:
// punk7890/PS2-Visual-Novel-Tool
// https://github.com/punk7890/PS2-Visual-Novel-Tool
// Original copyright (c) 2024 punk7890

using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Sony GIM",
        extensions: new[] { "gim" },
        magics: new[] { "MIG", "GIM" }
    )]
    internal sealed class GimImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanRead || !s.CanSeek)
                    return null;

                if (s.Length < 0x20 || s.Length > int.MaxValue)
                    return null;

                int length = (int)s.Length;
                var data = new byte[length];
                s.Position = 0;
                s.ReadExactly(data, 0, length);

                bool littleEndian = true;
                if (length >= 3)
                {
                    if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'M')
                        littleEndian = false;
                    else if (data[0] == (byte)'M' && data[1] == (byte)'I' && data[2] == (byte)'G')
                        littleEndian = true;
                }

                return Decode(data, littleEndian);
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

        static Image? Decode(byte[] data, bool littleEndian)
        {
            if (data == null || data.Length < 0x20)
                return null;

            int imageInfoOffset = -1;
            int paletteInfoOffset = -1;
            int paletteBlockEnd = -1;
            int offset = 0x10;
            int loop = 0;

            while (offset + 0x10 <= data.Length && loop < 64)
            {
                ushort id = ReadUInt16(data, offset, littleEndian);
                if (id == 0xFF)
                    break;

                uint size = ReadUInt32(data, offset + 4, littleEndian);
                uint next = ReadUInt32(data, offset + 8, littleEndian);
                uint headerSize = ReadUInt32(data, offset + 0xC, littleEndian);

                if (size < headerSize || headerSize == 0 || next == 0)
                    return null;

                int blockStart = offset;
                int blockEnd;
                int subHeader;
                try
                {
                    blockEnd = checked(blockStart + (int)size);
                    subHeader = checked(blockStart + (int)headerSize);
                }
                catch
                {
                    return null;
                }

                if (blockEnd > data.Length || subHeader > blockEnd)
                    return null;

                switch (id)
                {
                    case 4:
                        if (imageInfoOffset < 0)
                            imageInfoOffset = subHeader;
                        break;
                    case 5:
                        if (paletteInfoOffset < 0)
                        {
                            paletteInfoOffset = subHeader;
                            paletteBlockEnd = blockEnd;
                        }
                        break;
                }

                offset = blockStart + (int)next;
                loop++;
            }

            if (imageInfoOffset < 0)
                return null;

            ushort imgFormat = ReadUInt16(data, imageInfoOffset + 4, littleEndian);
            ushort pixelOrder = ReadUInt16(data, imageInfoOffset + 6, littleEndian);
            int width = ReadUInt16(data, imageInfoOffset + 8, littleEndian);
            int height = ReadUInt16(data, imageInfoOffset + 0xA, littleEndian);
            ushort bpp = ReadUInt16(data, imageInfoOffset + 0xC, littleEndian);
            uint imgRel = ReadUInt32(data, imageInfoOffset + 0x1C, littleEndian);

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                return null;

            if (bpp != 4 && bpp != 8 && bpp != 16 && bpp != 32)
                return null;

            int imgDataOffset;
            try
            {
                imgDataOffset = checked(imageInfoOffset + (int)imgRel);
            }
            catch
            {
                return null;
            }

            if (imgDataOffset < 0 || imgDataOffset >= data.Length)
                return null;

            long pixels = (long)width * height;
            int pixelBytes = (int)((pixels * bpp + 7) / 8);

            int maxAvailable = data.Length - imgDataOffset;
            if (maxAvailable < pixelBytes)
                return null;

            int bufferSizeToRead = maxAvailable;

            if (paletteInfoOffset > 0 && imgDataOffset < paletteInfoOffset)
            {
                bufferSizeToRead = Math.Min(bufferSizeToRead, paletteInfoOffset - imgDataOffset);
            }

            var imageBytes = new byte[bufferSizeToRead];
            Buffer.BlockCopy(data, imgDataOffset, imageBytes, 0, bufferSizeToRead);

            if (pixelOrder == 1)
            {
                imageBytes = Unswizzle(imageBytes, width, height, bpp);
            }

            byte[] paletteBgra = null;
            if (imgFormat == 0x04 || imgFormat == 0x05)
            {
                if (paletteInfoOffset < 0 || paletteBlockEnd <= 0)
                    return null;

                uint palRel = ReadUInt32(data, paletteInfoOffset + 0x1C, littleEndian);
                int palDataOffset;
                try
                {
                    palDataOffset = checked(paletteInfoOffset + (int)palRel);
                }
                catch
                {
                    return null;
                }

                if (palDataOffset < 0 || palDataOffset >= data.Length || palDataOffset >= paletteBlockEnd)
                    return null;

                int palBytes = paletteBlockEnd - palDataOffset;
                if (palBytes <= 0)
                    return null;

                int colorCount = palBytes / 4;
                if (imgFormat == 0x04)
                    colorCount = Math.Min(colorCount, 16);
                else if (imgFormat == 0x05)
                    colorCount = Math.Min(colorCount, 256);

                if (colorCount <= 0)
                    return null;

                var palRgba = new byte[palBytes];
                Buffer.BlockCopy(data, palDataOffset, palRgba, 0, palBytes);

                paletteBgra = ImageUtils.BuildPaletteBgraFromRgba(palRgba, colorCount, false);
            }

            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);

            int rowSizeInBytes = (width * bpp + 7) / 8;
            byte[] srcRow = new byte[rowSizeInBytes];
            byte[] dstRow = new byte[width * 4];

            try
            {
                int srcIndex = 0;
                for (int y = 0; y < height; y++)
                {
                    if (srcIndex + rowSizeInBytes > imageBytes.Length)
                        break;

                    Buffer.BlockCopy(imageBytes, srcIndex, srcRow, 0, rowSizeInBytes);
                    srcIndex += rowSizeInBytes;

                    switch (imgFormat)
                    {
                        case 0x00:
                            ImageUtils.ConvertRowRgba5650ToBgra(srcRow, dstRow, width);
                            break;
                        case 0x01:
                            ImageUtils.ConvertRowRgba5551ToBgra(srcRow, dstRow, width);
                            break;
                        case 0x02:
                            ImageUtils.ConvertRowRgba4444ToBgra(srcRow, dstRow, width);
                            break;
                        case 0x03:
                            ImageUtils.ConvertRowRgba32ToBgra(srcRow, dstRow, width);
                            break;
                        case 0x04:
                            if (paletteBgra == null)
                            {
                                bmp.Dispose();
                                return null;
                            }
                            ImageUtils.ConvertRowIndexed4ToBgra(srcRow, dstRow, width, paletteBgra);
                            break;
                        case 0x05:
                            if (paletteBgra == null)
                            {
                                bmp.Dispose();
                                return null;
                            }
                            ImageUtils.ConvertRowIndexed8ToBgra(srcRow, dstRow, width, paletteBgra);
                            break;
                    }

                    ImageUtils.CopyRowToBitmap(bd, y, dstRow, stride);
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

        static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        {
            if (offset < 0 || offset + 1 >= data.Length)
                throw new ArgumentOutOfRangeException();
            return littleEndian
                ? (ushort)(data[offset] | (data[offset + 1] << 8))
                : (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (offset < 0 || offset + 3 >= data.Length)
                throw new ArgumentOutOfRangeException();
            return littleEndian
                ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
                : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        static byte[] Unswizzle(byte[] src, int width, int height, int bpp)
        {
            if (src == null)
                return Array.Empty<byte>();

            int dstSize = (width * height * bpp + 7) / 8;
            var dst = new byte[dstSize];

            int blockWidth = 16;
            int blockHeight = 8;

            int rowBlocks = (width + blockWidth - 1) / blockWidth;
            int colBlocks = (height + blockHeight - 1) / blockHeight;

            if (bpp >= 8)
            {
                int bytesPerPixel = bpp / 8;
                int srcIndex = 0;

                for (int by = 0; by < colBlocks; by++)
                {
                    for (int bx = 0; bx < rowBlocks; bx++)
                    {
                        for (int row = 0; row < blockHeight; row++)
                        {
                            for (int col = 0; col < blockWidth; col++)
                            {
                                int x = bx * blockWidth + col;
                                int y = by * blockHeight + row;

                                if (x < width && y < height)
                                {
                                    int dstIndex = (y * width + x) * bytesPerPixel;

                                    if (srcIndex + bytesPerPixel <= src.Length && dstIndex + bytesPerPixel <= dst.Length)
                                    {
                                        for (int b = 0; b < bytesPerPixel; b++)
                                        {
                                            dst[dstIndex + b] = src[srcIndex + b];
                                        }
                                    }
                                }

                                srcIndex += bytesPerPixel;
                            }
                        }
                    }
                }
            }
            else if (bpp == 4)
            {
                int byteWidth = (width + 1) / 2;
                byte[] tempDst = new byte[byteWidth * height];

                int byteRowBlocks = (byteWidth + 15) / 16;

                int srcIndex = 0;
                for (int by = 0; by < colBlocks; by++)
                {
                    for (int bx = 0; bx < byteRowBlocks; bx++)
                    {
                        for (int row = 0; row < 8; row++)
                        {
                            for (int col = 0; col < 16; col++)
                            {
                                int byteX = bx * 16 + col;
                                int y = by * 8 + row;

                                if (byteX < byteWidth && y < height)
                                {
                                    int dstIndex = y * byteWidth + byteX;
                                    if (srcIndex < src.Length && dstIndex < tempDst.Length)
                                    {
                                        tempDst[dstIndex] = src[srcIndex];
                                    }
                                }
                                srcIndex++;
                            }
                        }
                    }
                }
                return tempDst;
            }

            return dst;
        }
    }
}
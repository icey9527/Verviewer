using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "ACTRESS PB6",
        extensions: new[] { "bmp" },
        magics: new[] { "PB6", "BM8" }
    )]
    internal sealed class ActressPb6ImageHandler : IImageHandler
    {
        const int HeaderSize = 54;
        const int PaletteSize = 256 * 4;

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (s.Length < HeaderSize)
                    return null;

                int width = s.ReadUInt16LEAt(18);
                int height = s.ReadUInt16LEAt(22);
                int bpp = s.ReadUInt16LEAt(28);
                bool rawArgb = s.ReadBytesAt(0, 3).AsSpan().SequenceEqual("BM8"u8);

                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    return null;
                if (rawArgb ? bpp != 32 : bpp is not (8 or 24 or 32))
                    return null;

                int pixelSize = bpp / 8;
                int dataOffset = bpp == 8 ? HeaderSize + PaletteSize : HeaderSize;
                int decodedSize = checked(width * height * pixelSize);
                if (dataOffset > s.Length)
                    return null;

                byte[] pixels;
                if (bpp == 8 || rawArgb)
                {
                    if ((long)dataOffset + decodedSize > s.Length)
                        return null;
                    pixels = s.ReadBytesAt(dataOffset, decodedSize);
                }
                else
                {
                    s.Position = dataOffset;
                    pixels = DecodeRle(s, decodedSize, pixelSize);
                }

                byte[]? palette = bpp == 8 ? ReadPalette(s) : null;
                return CreateBitmap(pixels, palette, width, height, bpp);
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

        static byte[] DecodeRle(Stream s, int decodedSize, int pixelSize)
        {
            var output = new byte[decodedSize];
            var pixel = new byte[pixelSize];
            int dst = 0;

            while (dst < output.Length)
            {
                int control = s.ReadByte();
                if (control < 0)
                    throw new EndOfStreamException();

                int count = control & 0x7F;
                if (count == 0)
                    throw new InvalidDataException();

                int take = Math.Min(count, (output.Length - dst) / pixelSize);
                if ((control & 0x80) != 0)
                {
                    Array.Clear(pixel, 0, pixel.Length);
                    s.ReadAtLeast(pixel.AsSpan(), pixel.Length, throwOnEndOfStream: false);
                    for (int i = 0; i < take; i++)
                    {
                        Buffer.BlockCopy(pixel, 0, output, dst, pixel.Length);
                        dst += pixel.Length;
                    }
                }
                else
                {
                    int bytes = take * pixelSize;
                    s.ReadAtLeast(output.AsSpan(dst, bytes), bytes, throwOnEndOfStream: false);
                    dst += bytes;
                }
            }

            return output;
        }

        static byte[] ReadPalette(Stream s)
        {
            byte[] source = s.ReadBytesAt(HeaderSize, PaletteSize);
            var palette = new byte[PaletteSize];
            for (int i = 0; i < 256; i++)
            {
                int p = i * 4;
                palette[p + 0] = source[p + 2];
                palette[p + 1] = source[p + 1];
                palette[p + 2] = source[p + 0];
                palette[p + 3] = 255;
            }
            return palette;
        }

        static Image CreateBitmap(byte[] pixels, byte[]? palette, int width, int height, int bpp)
        {
            var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
            try
            {
                int sourceRowSize = checked(width * (bpp / 8));
                var sourceRow = new byte[sourceRowSize];
                var outputRow = new byte[width * 4];

                for (int sourceY = 0; sourceY < height; sourceY++)
                {
                    Buffer.BlockCopy(pixels, sourceY * sourceRowSize, sourceRow, 0, sourceRowSize);
                    if (bpp == 8)
                        ImageUtils.ConvertRowIndexed8ToBgra(sourceRow, outputRow, width, palette!);
                    else if (bpp == 24)
                        ImageUtils.ConvertRowRgb24ToBgra(sourceRow, outputRow, width);
                    else
                        ConvertRowArgbToBgra(sourceRow, outputRow, width);

                    ImageUtils.CopyRowToBitmap(bmpData, height - 1 - sourceY, outputRow, stride);
                }

                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bmpData, bmp);
            }
        }

        static void ConvertRowArgbToBgra(byte[] source, byte[] output, int width)
        {
            for (int x = 0; x < width; x++)
            {
                int p = x * 4;
                output[p + 0] = source[p + 3];
                output[p + 1] = source[p + 2];
                output[p + 2] = source[p + 1];
                output[p + 3] = source[p + 0];
            }
        }
    }
}

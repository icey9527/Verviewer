using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "LEAF LFB",
        extensions: new[] { "lfb" }
    )]
    internal sealed class LeafLfbImageHandler : IImageHandler
    {
        const int BitmapFileHeaderSize = 14;
        const int BitmapInfoHeaderSize = 40;
        const int IndexedAlphaPaletteOffset = BitmapFileHeaderSize + BitmapInfoHeaderSize;
        const int IndexedAlphaPaletteSize = 256 * 4;
        const int IndexedAlphaPixelOffset = IndexedAlphaPaletteOffset + IndexedAlphaPaletteSize;

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();

            try
            {
                if (s.Length < 4 || s.Length > int.MaxValue)
                    return null;

                int outputSize = s.ReadInt32LEAt(0);
                if (outputSize <= 0)
                    return null;

                int compressedSize = checked((int)s.Length - 4);
                byte[] compressed = s.ReadBytesAt(4, compressedSize);
                if (!Leaf.Decompress(compressed, outputSize, out byte[] bmpBytes))
                    return null;
                if (bmpBytes.Length < 2 || bmpBytes[0] != (byte)'B' || bmpBytes[1] != (byte)'M')
                    return null;

                if (TryDecodeIndexedAlphaBitmap(bmpBytes, out Bitmap? bmp))
                    return bmp;

                if (TryDecodeLeaf32Bitmap(bmpBytes, out bmp))
                    return bmp;

                return Image.FromStream(new MemoryStream(bmpBytes, writable: false));
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

        static bool TryDecodeIndexedAlphaBitmap(byte[] bmpBytes, out Bitmap? bmp)
        {
            bmp = null;

            if (bmpBytes.Length < IndexedAlphaPixelOffset)
                return false;

            int pixelOffset = ReadInt32LE(bmpBytes, 10);
            int dibSize = ReadInt32LE(bmpBytes, 14);
            int width = ReadInt32LE(bmpBytes, 18);
            int heightRaw = ReadInt32LE(bmpBytes, 22);
            int planes = ReadUInt16LE(bmpBytes, 26);
            int bpp = ReadUInt16LE(bmpBytes, 28);
            int compression = ReadInt32LE(bmpBytes, 30);

            if (dibSize != BitmapInfoHeaderSize || planes != 1 || bpp != 16 || compression != 0)
                return false;
            if (pixelOffset != IndexedAlphaPixelOffset || width <= 0 || heightRaw == 0)
                return false;

            int height = heightRaw > 0 ? heightRaw : -heightRaw;
            bool bottomUp = heightRaw > 0;
            int srcStride = checked(width * 2);
            long pixelBytes = (long)srcStride * height;
            if (pixelOffset < 0 || pixelOffset + pixelBytes > bmpBytes.Length)
                return false;

            byte[] palette = new byte[IndexedAlphaPaletteSize];
            Buffer.BlockCopy(bmpBytes, IndexedAlphaPaletteOffset, palette, 0, palette.Length);

            bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
            Bitmap createdBmp = bmp;
            try
            {
                byte[] row = new byte[width * 4];

                for (int y = 0; y < height; y++)
                {
                    int srcY = bottomUp ? (height - 1 - y) : y;
                    int srcRow = pixelOffset + srcY * srcStride;

                    for (int x = 0; x < width; x++)
                    {
                        int src = srcRow + x * 2;
                        int alpha = bmpBytes[src];
                        int paletteIndex = bmpBytes[src + 1];
                        int pal = paletteIndex * 4;
                        int dst = x * 4;

                        row[dst + 0] = palette[pal + 0];
                        row[dst + 1] = palette[pal + 1];
                        row[dst + 2] = palette[pal + 2];
                        row[dst + 3] = (byte)alpha;
                    }

                    ImageUtils.CopyRowToBitmap(bmpData, y, row, stride);
                }

                return true;
            }
            catch
            {
                createdBmp.Dispose();
                bmp = null;
                return false;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bmpData, createdBmp);
            }
        }

        static bool TryDecodeLeaf32Bitmap(byte[] bmpBytes, out Bitmap? bmp)
        {
            bmp = null;

            if (bmpBytes.Length < BitmapFileHeaderSize + BitmapInfoHeaderSize)
                return false;

            int pixelOffset = ReadInt32LE(bmpBytes, 10);
            int dibSize = ReadInt32LE(bmpBytes, 14);
            int width = ReadInt32LE(bmpBytes, 18);
            int heightRaw = ReadInt32LE(bmpBytes, 22);
            int planes = ReadUInt16LE(bmpBytes, 26);
            int bpp = ReadUInt16LE(bmpBytes, 28);
            int compression = ReadInt32LE(bmpBytes, 30);

            if (dibSize != BitmapInfoHeaderSize || planes != 1 || bpp != 32 || compression != 0)
                return false;
            if (pixelOffset < BitmapFileHeaderSize + BitmapInfoHeaderSize || width <= 0 || heightRaw == 0)
                return false;

            int height = heightRaw > 0 ? heightRaw : -heightRaw;
            bool bottomUp = heightRaw > 0;
            int srcStride = checked(width * 4);
            long pixelBytes = (long)srcStride * height;
            if (pixelOffset < 0 || pixelOffset + pixelBytes > bmpBytes.Length)
                return false;

            bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
            Bitmap createdBmp = bmp;
            try
            {
                byte[] row = new byte[width * 4];

                for (int y = 0; y < height; y++)
                {
                    int srcY = bottomUp ? (height - 1 - y) : y;
                    int srcRow = pixelOffset + srcY * srcStride;

                    for (int x = 0; x < width; x++)
                    {
                        int src = srcRow + x * 4;
                        int dst = x * 4;

                        byte a = bmpBytes[src + 0];
                        byte b = bmpBytes[src + 1];
                        byte g = bmpBytes[src + 2];
                        byte r = bmpBytes[src + 3];

                        row[dst + 0] = b;
                        row[dst + 1] = g;
                        row[dst + 2] = r;
                        row[dst + 3] = a;
                    }

                    ImageUtils.CopyRowToBitmap(bmpData, y, row, stride);
                }

                return true;
            }
            catch
            {
                createdBmp.Dispose();
                bmp = null;
                return false;
            }
            finally
            {
                ImageUtils.UnlockBitmap(bmpData, createdBmp);
            }
        }

        static int ReadInt32LE(byte[] data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }

        static int ReadUInt16LE(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8);
        }
    }
}

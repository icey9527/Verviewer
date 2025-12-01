using System;
using System.Drawing;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink AGI",
        extensions: new[] { "agi" }
        )]
    internal class AgiImageHandler : IImageHandler
    {
        public Image? TryDecode(byte[] data, string extension)
        {
            try
            {
                if (data == null || data.Length < 0x30)
                    return null;

                var (width, height) = ReadHeaderInfo(data);

                if (data.Length < 0x30)
                    return null;
        
                byte[] flagBytes = new byte[4];
                Array.Copy(data, 0x2C, flagBytes, 0, 4);
                string bppFlag = BitConverter.ToString(flagBytes).Replace("-", "").ToLowerInvariant();

                return bppFlag switch
                {
                    "44494449" => Read16bpp(data, width, height),
                    "00300100" => Read24bpp(data, width, height),
                    "10001000" => Read8bpp(data, width, height),
                    "00001400" or "08000200" => Read4bpp(data, width, height),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static (int width, int height) ReadHeaderInfo(byte[] data)
        {
            if (data.Length < 0x1C)
                throw new ArgumentException("文件太小，无法包含宽高");
            int width = BitConverter.ToUInt16(data, 0x18);
            int height = BitConverter.ToUInt16(data, 0x1A);
            return (width, height);
        }

        private static byte FixAlpha(byte a)
        {
            int val = a * 2 - 1;
            if (val < 0) val = 0;
            if (val > 255) val = 255;
            return (byte)val;
        }

        private static Image Read4bpp(byte[] data, int width, int height)
        {
            if (data.Length < 0x20)
                throw new ArgumentException("数据不足");

            uint paletteOffset = BitConverter.ToUInt32(data, 0x1C);
            int pixelDataOffset = 0x30;
            if (pixelDataOffset >= data.Length)
                throw new ArgumentException("像素偏移错误");

            int pixelLen = data.Length - pixelDataOffset;
            byte[] pixelData = new byte[pixelLen];
            Buffer.BlockCopy(data, pixelDataOffset, pixelData, 0, pixelLen);

            var palette = new (byte r, byte g, byte b, byte a)[16];
            for (int i = 0; i < 16; i++)
            {
                int off = (int)paletteOffset + i * 4;
                if (off + 3 >= data.Length)
                    throw new ArgumentException("调色板越界");
                byte r = data[off];
                byte g = data[off + 1];
                byte b = data[off + 2];
                byte a = FixAlpha(data[off + 3]);
                palette[i] = (r, g, b, a);
            }

            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pos = y * width + x;
                    int bytePos = pos / 2;
                    if (bytePos >= pixelData.Length)
                    {
                        bmp.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
                        continue;
                    }

                    byte b = pixelData[bytePos];
                    int nibbleIndex = pos % 2;
                    int index = (b >> (4 * nibbleIndex)) & 0x0F;
                    var p = palette[index];
                    bmp.SetPixel(x, y, Color.FromArgb(p.a, p.r, p.g, p.b));
                }
            }

            return bmp;
        }

        private static Image Read8bpp(byte[] data, int width, int height)
        {
            if (data.Length < 0x1F)
                throw new ArgumentException("数据不足");

            int paletteOffset = data[0x1C] | (data[0x1D] << 8) | (data[0x1E] << 16);
            int paletteSize = 256 * 4;
            if (paletteOffset + paletteSize > data.Length)
                throw new ArgumentException("调色板越界");

            byte[] paletteData = new byte[paletteSize];
            Buffer.BlockCopy(data, paletteOffset, paletteData, 0, paletteSize);

            var original = new (byte r, byte g, byte b, byte a)[256];
            for (int i = 0; i < 256; i++)
            {
                int pos = i * 4;
                byte r = paletteData[pos];
                byte g = paletteData[pos + 1];
                byte b = paletteData[pos + 2];
                byte a = FixAlpha(paletteData[pos + 3]);
                original[i] = (r, g, b, a);
            }

            var palette = new (byte r, byte g, byte b, byte a)[256];
            int dst = 0;
            for (int major = 0; major < 256; major += 32)
            {
                for (int i = 0; i < 8; i++) palette[dst++] = original[major + i];
                for (int i = 16; i < 24; i++) palette[dst++] = original[major + i];
                for (int i = 8; i < 16; i++) palette[dst++] = original[major + i];
                for (int i = 24; i < 32; i++) palette[dst++] = original[major + i];
            }

            int pixelDataOffset = 0x30;
            int expectedSize = width * height;
            if (pixelDataOffset + expectedSize > data.Length)
                throw new ArgumentException("像素数据不足");

            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int idx = pixelDataOffset;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (idx >= data.Length) break;
                    byte index = data[idx++];
                    var p = palette[index];
                    bmp.SetPixel(x, y, Color.FromArgb(p.a, p.r, p.g, p.b));
                }
            }

            return bmp;
        }

        private static Image Read16bpp(byte[] data, int width, int height)
        {
            int pixelDataOffset = 0x20;
            int expectedSize = width * height * 2;
            if (pixelDataOffset + expectedSize > data.Length)
                throw new ArgumentException("像素数据不足");

            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            int offset = pixelDataOffset;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (offset + 1 >= data.Length) break;
                    ushort px = BitConverter.ToUInt16(data, offset);
                    offset += 2;

                    int b5 = (px >> 10) & 0x1F;
                    int g5 = (px >> 5) & 0x1F;
                    int r5 = px & 0x1F;

                    int r = (r5 << 3) | (r5 >> 2);
                    int g = (g5 << 3) | (g5 >> 2);
                    int b = (b5 << 3) | (b5 >> 2);

                    bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }

            return bmp;
        }

        private static Image Read24bpp(byte[] data, int width, int height)
        {
            int pixelDataOffset = 0x50;
            int expectedSize = width * height * 3;
            if (pixelDataOffset + expectedSize > data.Length)
                throw new ArgumentException("像素数据不足");

            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            int offset = pixelDataOffset;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (offset + 2 >= data.Length) break;
                    byte r = data[offset];
                    byte g = data[offset + 1];
                    byte b = data[offset + 2];
                    offset += 3;
                    bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }

            return bmp;
        }
    }
}
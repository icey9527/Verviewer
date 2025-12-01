using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Ikusabune TBM",
        extensions: new[] { "tbm" }
    )]
    internal sealed class IkusabuneTbmImageHandler : IImageHandler
    {
        const int MagicNew = 1112359508;
        const int MagicOld = 541934164;

        public Image? TryDecode(byte[] data, string extension)
        {
            if (!extension.Equals(".tbm", StringComparison.OrdinalIgnoreCase))
                return null;
            if (data == null || data.Length < 40)
                return null;

            try
            {
                if (!ReadHeader(data, out var width, out var height, out var parts, out var bpp, out var offsetBase))
                    return null;

                if (width <= 0 || height <= 0 || parts < 0)
                    return null;
                if (bpp != 16 && bpp != 24)
                    return null;

                long pixels = (long)width * height;
                if (pixels <= 0 || pixels > 10000L * 10000L)
                    return null;

                int fullStride;
                int fullSize;
                try
                {
                    fullStride = checked(width * 4);
                    fullSize = checked(fullStride * height);
                }
                catch
                {
                    return null;
                }

                var full = new byte[fullSize];

                int tableBytes;
                try
                {
                    tableBytes = checked(parts * 4);
                }
                catch
                {
                    return null;
                }

                if (offsetBase < 0 || offsetBase + tableBytes > data.Length)
                    return null;

                int bytesPerPixel = bpp / 8;

                for (int i = 0; i < parts; i++)
                {
                    int ofs = BitConverter.ToInt32(data, offsetBase + i * 4);
                    if (ofs < 0 || ofs + 16 > data.Length)
                        return null;

                    int dstX = BitConverter.ToInt32(data, ofs + 0);
                    int dstY = BitConverter.ToInt32(data, ofs + 4);
                    int pw = BitConverter.ToInt32(data, ofs + 8);
                    int ph = BitConverter.ToInt32(data, ofs + 12);

                    if (pw <= 0 || ph <= 0)
                        continue;
                    if (dstX < 0 || dstY < 0 || dstX >= width || dstY >= height)
                        continue;

                    int wClip = pw;
                    int hClip = ph;
                    if (dstX + wClip > width) wClip = width - dstX;
                    if (dstY + hClip > height) hClip = height - dstY;
                    if (wClip <= 0 || hClip <= 0)
                        continue;

                    int srcRow;
                    long need;
                    int pixelOffset;
                    try
                    {
                        srcRow = checked(pw * bytesPerPixel);
                        need = (long)srcRow * ph;
                        pixelOffset = checked(ofs + 16);
                    }
                    catch
                    {
                        return null;
                    }

                    if (pixelOffset < 0 || pixelOffset + need > data.Length)
                        return null;

                    for (int y = 0; y < hClip; y++)
                    {
                        int srcYOff = pixelOffset + y * srcRow;
                        int dstYOff = (dstY + y) * fullStride + dstX * 4;

                        if (bpp == 24)
                        {
                            int sx = srcYOff;
                            int dx = dstYOff;
                            for (int x = 0; x < wClip; x++)
                            {
                                byte b = data[sx + 0];
                                byte g = data[sx + 1];
                                byte r = data[sx + 2];
                                full[dx + 0] = b;
                                full[dx + 1] = g;
                                full[dx + 2] = r;
                                full[dx + 3] = 255;
                                sx += 3;
                                dx += 4;
                            }
                        }
                        else
                        {
                            int sx = srcYOff;
                            int dx = dstYOff;
                            for (int x = 0; x < wClip; x++)
                            {
                                ushort v = (ushort)(data[sx] | (data[sx + 1] << 8));
                                int r5 = (v >> 11) & 0x1F;
                                int g5 = (v >> 6) & 0x1F;
                                int b5 = v & 0x1F;
                                byte r = (byte)((r5 << 3) | (r5 >> 2));
                                byte g = (byte)((g5 << 3) | (g5 >> 2));
                                byte b = (byte)((b5 << 3) | (b5 >> 2));
                                full[dx + 0] = b;
                                full[dx + 1] = g;
                                full[dx + 2] = r;
                                full[dx + 3] = 255;
                                sx += 2;
                                dx += 4;
                            }
                        }
                    }
                }

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    if (bd.Stride == fullStride)
                    {
                        Marshal.Copy(full, 0, bd.Scan0, full.Length);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr dst = bd.Scan0 + y * bd.Stride;
                            Marshal.Copy(full, y * fullStride, dst, fullStride);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        static bool ReadHeader(byte[] data, out int width, out int height, out int parts, out int bpp, out int offsetBase)
        {
            width = height = parts = bpp = offsetBase = 0;
            if (data.Length < 40)
                return false;

            int magic = BitConverter.ToInt32(data, 0);
            if (magic == MagicOld)
                offsetBase = 40;
            else if (magic == MagicNew)
                offsetBase = 44;
            else
                return false;

            if (data.Length < 36)
                return false;

            width = BitConverter.ToInt32(data, 20);
            height = BitConverter.ToInt32(data, 24);
            parts = BitConverter.ToInt32(data, 28);
            bpp = BitConverter.ToInt32(data, 32);
            return true;
        }
    }
}
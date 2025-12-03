using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "TGA",
        extensions: new[] { "tga" },
        magics: null
    )]
    internal sealed class TgaImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (stream == null || !stream.CanRead) return null;
            if (stream.CanSeek) stream.Position = 0;
            var header = new byte[18];
            if (!ReadExact(stream, header, 0, header.Length)) return null;
            byte idLength = header[0];
            byte colorMapType = header[1];
            byte imageType = header[2];
            ushort width = BitConverter.ToUInt16(header, 12);
            ushort height = BitConverter.ToUInt16(header, 14);
            byte pixelDepth = header[16];
            byte descriptor = header[17];
            if (width == 0 || height == 0) return null;
            if (colorMapType != 0) return null;
            bool rle;
            bool gray;
            switch (imageType)
            {
                case 2:
                    rle = false;
                    gray = false;
                    break;
                case 3:
                    rle = false;
                    gray = true;
                    break;
                case 10:
                    rle = true;
                    gray = false;
                    break;
                case 11:
                    rle = true;
                    gray = true;
                    break;
                default:
                    return null;
            }
            int bpp = pixelDepth;
            if (gray)
            {
                if (bpp != 8) return null;
            }
            else
            {
                if (bpp != 24 && bpp != 32) return null;
            }
            if (idLength > 0)
                if (!Skip(stream, idLength)) return null;
            int w = width;
            int h = height;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            BitmapData data;
            try
            {
                data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
            bool ok = true;
            try
            {
                bool originTop = (descriptor & 0x20) != 0;
                if (!rle)
                    DecodeUncompressed(stream, data, w, h, bpp, gray, originTop);
                else
                    DecodeRle(stream, data, w, h, bpp, gray, originTop);
            }
            catch
            {
                ok = false;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            if (!ok)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }

        static bool ReadExact(Stream s, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buffer, offset, count);
                if (n <= 0) return false;
                offset += n;
                count -= n;
            }
            return true;
        }

        static bool Skip(Stream s, int count)
        {
            var buf = new byte[4096];
            int remaining = count;
            while (remaining > 0)
            {
                int n = s.Read(buf, 0, Math.Min(buf.Length, remaining));
                if (n <= 0) return false;
                remaining -= n;
            }
            return true;
        }

        static void DecodeUncompressed(Stream s, BitmapData data, int width, int height, int bpp, bool gray, bool originTop)
        {
            int srcBpp = bpp / 8;
            var pixel = new byte[srcBpp];
            var row = new byte[width * 4];
            int stride = data.Stride;
            for (int y = 0; y < height; y++)
            {
                int destY = originTop ? y : (height - 1 - y);
                int dst = 0;
                for (int x = 0; x < width; x++)
                {
                    if (!ReadExact(s, pixel, 0, srcBpp)) throw new EndOfStreamException();
                    if (gray)
                    {
                        byte v = pixel[0];
                        row[dst] = v;
                        row[dst + 1] = v;
                        row[dst + 2] = v;
                        row[dst + 3] = 255;
                    }
                    else
                    {
                        byte b = pixel[0];
                        byte g = pixel[1];
                        byte r = pixel[2];
                        byte a = srcBpp == 4 ? pixel[3] : (byte)255;
                        row[dst] = b;
                        row[dst + 1] = g;
                        row[dst + 2] = r;
                        row[dst + 3] = a;
                    }
                    dst += 4;
                }
                IntPtr dest = IntPtr.Add(data.Scan0, destY * stride);
                Marshal.Copy(row, 0, dest, row.Length);
            }
        }

        static void DecodeRle(Stream s, BitmapData data, int width, int height, int bpp, bool gray, bool originTop)
        {
            int srcBpp = bpp / 8;
            var pixel = new byte[srcBpp];
            var row = new byte[width * 4];
            int stride = data.Stride;
            int total = width * height;
            int index = 0;
            int currentRow = -1;
            while (index < total)
            {
                int ph = s.ReadByte();
                if (ph < 0) throw new EndOfStreamException();
                bool run = (ph & 0x80) != 0;
                int count = (ph & 0x7F) + 1;
                if (run)
                {
                    if (!ReadExact(s, pixel, 0, srcBpp)) throw new EndOfStreamException();
                    for (int i = 0; i < count && index < total; i++)
                    {
                        WritePixel(data, width, height, bpp, gray, originTop, pixel, row, stride, ref currentRow, index);
                        index++;
                    }
                }
                else
                {
                    for (int i = 0; i < count && index < total; i++)
                    {
                        if (!ReadExact(s, pixel, 0, srcBpp)) throw new EndOfStreamException();
                        WritePixel(data, width, height, bpp, gray, originTop, pixel, row, stride, ref currentRow, index);
                        index++;
                    }
                }
            }
            if (currentRow >= 0)
            {
                int destY = originTop ? currentRow : (height - 1 - currentRow);
                IntPtr dest = IntPtr.Add(data.Scan0, destY * stride);
                Marshal.Copy(row, 0, dest, row.Length);
            }
        }

        static void WritePixel(BitmapData data, int width, int height, int bpp, bool gray, bool originTop, byte[] pixel, byte[] row, int stride, ref int currentRow, int index)
        {
            int y = index / width;
            int x = index % width;
            if (y != currentRow)
            {
                if (currentRow >= 0)
                {
                    int destYPrev = originTop ? currentRow : (height - 1 - currentRow);
                    IntPtr destPrev = IntPtr.Add(data.Scan0, destYPrev * stride);
                    Marshal.Copy(row, 0, destPrev, row.Length);
                }
                Array.Clear(row, 0, row.Length);
                currentRow = y;
            }
            int dst = x * 4;
            if (gray)
            {
                byte v = pixel[0];
                row[dst] = v;
                row[dst + 1] = v;
                row[dst + 2] = v;
                row[dst + 3] = 255;
            }
            else
            {
                byte b = pixel[0];
                byte g = pixel[1];
                byte r = pixel[2];
                byte a = bpp == 32 ? pixel[3] : (byte)255;
                row[dst] = b;
                row[dst + 1] = g;
                row[dst + 2] = r;
                row[dst + 3] = a;
            }
        }
    }
}
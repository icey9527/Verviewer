using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "SALA ONE IPG",
        extensions: new[] { "ipg" },
        magics: new[] { "IPG" }
    )]
    internal sealed class IpgImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            var reader = new BinaryReader(stream, Encoding.ASCII, true);
            if (reader.ReadBytes(4).Length < 4) return null;
            uint w, h;
            try
            {
                w = reader.ReadUInt32();
                h = reader.ReadUInt32();
            }
            catch
            {
                return null;
            }
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return null;
            int width = (int)w;
            int height = (int)h;
            long pixelBytes = (long)width * height * 4;
            if (pixelBytes <= 0 || pixelBytes > int.MaxValue) return null;
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            bool ok = true;
            try
            {
                int stride = bmpData.Stride;
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    if (!ReadExact(stream, row, 0, row.Length))
                    {
                        ok = false;
                        break;
                    }
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        byte r = row[i];
                        row[i] = row[i + 2];
                        row[i + 2] = r;
                    }
                    IntPtr dest = IntPtr.Add(bmpData.Scan0, y * stride);
                    Marshal.Copy(row, 0, dest, row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
            if (!ok)
            {
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) return false;
                offset += n;
                count -= n;
            }
            return true;
        }
    }
}
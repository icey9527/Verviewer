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

            var data = reader.ReadBytes((int)pixelBytes);
            if (data.Length < pixelBytes) return null;

            for (int i = 0; i < data.Length; i += 4)
            {
                byte r = data[i];
                data[i] = data[i + 2];
                data[i + 2] = r;
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                Marshal.Copy(data, 0, bmpData.Scan0, data.Length);
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            return bitmap;
        }
    }
}
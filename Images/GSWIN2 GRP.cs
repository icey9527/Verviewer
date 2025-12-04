using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Archives;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "GSWIN2 GRP"
    )]
    internal sealed class GameImgImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (!stream.CanRead) return null;
            if (stream.CanSeek) stream.Position = 0;

            var header = new byte[40];
            try
            {
                stream.ReadExactly(header, 0, header.Length);
            }
            catch
            {
                return null;
            }

            uint compSizeU = BitConverter.ToUInt32(header, 0);
            uint w = BitConverter.ToUInt32(header, 16);
            uint h = BitConverter.ToUInt32(header, 20);
            uint bpp = BitConverter.ToUInt32(header, 24);

            if (w == 0 || h == 0) return null;

            int width;
            int height;
            int bitsPerPixel;
            try
            {
                width = checked((int)w);
                height = checked((int)h);
                bitsPerPixel = checked((int)bpp);
            }
            catch
            {
                return null;
            }

            if (bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32)
                return null;

            if (compSizeU > int.MaxValue)
                return null;

            int compSize = (int)compSizeU;

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);

            BitmapData bmpData;
            try
            {
                bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            }
            catch
            {
                bmp.Dispose();
                return null;
            }

            bool ok = false;

            try
            {
                if (bitsPerPixel == 8)
                {
                    var writer = new Grp8WriterStream(bmpData, width, height);
                    long need = 256L * 4 + (long)width * height;

                    if (compSize > 0)
                        GSWIN.Decompress(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);

                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha)
                        GswinImageHelpers.EnsureOpaqueAlpha(bmpData, width, height);
                }
                else if (bitsPerPixel == 24)
                {
                    var writer = new Grp24WriterStream(bmpData, width, height);
                    long need = (long)width * height * 3;

                    if (compSize > 0)
                        GSWIN.Decompress(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);

                    ok = writer.Completed;
                }
                else // 32bpp
                {
                    var writer = new Grp32WriterStream(bmpData, width, height);
                    long need = (long)width * height * 4;

                    if (compSize > 0)
                        GSWIN.Decompress(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);

                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha)
                        GswinImageHelpers.EnsureOpaqueAlpha(bmpData, width, height);
                }
            }
            catch
            {
                ok = false;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            if (!ok)
            {
                bmp.Dispose();
                return null;
            }

            return bmp;
        }

        static void CopyRaw(Stream input, Stream output, long bytes)
        {
            var buf = new byte[4096];
            while (bytes > 0)
            {
                int n = input.Read(buf, 0, (int)Math.Min(buf.Length, bytes));
                if (n <= 0) break;
                output.Write(buf, 0, n);
                bytes -= n;
            }
        }
    }
}
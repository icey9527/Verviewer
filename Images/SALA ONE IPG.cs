using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;
using Utils; // StreamUtils, ImageUtils

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
            Stream s = stream.EnsureSeekable();
            try
            {
                // 头部至少 4 + 4 + 4 = 12 字节
                if (!s.CanRead) return null;
                if (s.Length < 12) return null;

                // 读 4 字节头（原代码只是读掉，不做校验）
                byte[] head = s.ReadExactly(4);

                // 读宽高 (小端 uint32)
                uint w, h;
                try
                {
                    w = ReadUInt32LE(s);
                    h = ReadUInt32LE(s);
                }
                catch
                {
                    return null;
                }

                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                    return null;

                int width = (int)w;
                int height = (int)h;

                long pixelBytes = (long)width * height * 4;
                if (pixelBytes <= 0 || pixelBytes > int.MaxValue)
                    return null;

                // 剩余字节至少要够像素数据
                if (s.Position + pixelBytes > s.Length)
                    return null;

                // 创建位图并锁定
                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
                bool ok = true;

                try
                {
                    var srcRow = new byte[width * 4]; // 源 RGBA
                    var row = new byte[width * 4];     // 目标 BGRA

                    for (int y = 0; y < height; y++)
                    {
                        try
                        {
                            s.ReadExactly(srcRow, 0, srcRow.Length);
                        }
                        catch
                        {
                            ok = false;
                            break;
                        }

                        // 源是 RGBA32 -> 转成 BGRA32
                        ImageUtils.ConvertRowRgba32ToBgra(srcRow, row, width);

                        ImageUtils.CopyRowToBitmap(bmpData, y, row, stride);
                    }
                }
                finally
                {
                    ImageUtils.UnlockBitmap(bmpData, bmp);
                }

                if (!ok)
                {
                    bmp.Dispose();
                    return null;
                }

                return bmp;
            }
            catch
            {
                // TryDecode 失败就返回 null, 不向外抛异常
                return null;
            }
            finally
            {
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }

        // 本地小工具: 读一个小端 UInt32 (和你 SALA PFS 里的模式一致)
        static uint ReadUInt32LE(Stream s)
        {
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            if ((b0 | b1 | b2 | b3) < 0)
                throw new EndOfStreamException("Unexpected EOF while reading UInt32.");
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }
    }
}
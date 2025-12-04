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
        id: "Ikusabune TBM",
        extensions: new[] { "tbm" },
        magics: new[] { "TBM ", "TBMB" }
    )]
    internal sealed class IkusabuneTbmImageHandler : IImageHandler
    {
        const int MagicNew = 1112359508;
        const int MagicOld = 541934164;

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 40) return null;

                if (!ReadHeader(s, out int width, out int height, out int parts, out int bpp, out int offsetBase))
                    return null;

                if (width <= 0 || height <= 0 || parts < 0) return null;
                if (bpp != 16 && bpp != 24) return null;

                long pixels = (long)width * height;
                if (pixels <= 0 || pixels > 10000L * 10000L) return null;

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);

                BitmapData bd;
                try
                {
                    bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                }
                catch
                {
                    bmp.Dispose();
                    return null;
                }

                bool ok;
                try
                {
                    ok = FillImage(s, width, height, parts, bpp, offsetBase, bd);
                }
                catch
                {
                    ok = false;
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                if (!ok)
                {
                    bmp.Dispose();
                    return null;
                }

                return bmp;
            }
            finally
            {
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }

        static bool FillImage(Stream s, int width, int height, int parts, int bpp, int offsetBase, BitmapData bd)
        {
            int stride = bd.Stride;
            IntPtr basePtr = bd.Scan0;

            int tableBytes;
            try { tableBytes = checked(parts * 4); } catch { return false; }
            if (offsetBase < 0 || offsetBase + tableBytes > s.Length) return false;

            int bytesPerPixel = bpp / 8;

            for (int i = 0; i < parts; i++)
            {
                int ofs = s.ReadInt32LEAt(offsetBase + i * 4);
                if (ofs < 0 || ofs + 16 > s.Length) return false;

                int dstX = s.ReadInt32LEAt(ofs + 0);
                int dstY = s.ReadInt32LEAt(ofs + 4);
                int pw   = s.ReadInt32LEAt(ofs + 8);
                int ph   = s.ReadInt32LEAt(ofs + 12);

                if (pw <= 0 || ph <= 0) continue;
                if (dstX < 0 || dstY < 0 || dstX >= width || dstY >= height) continue;

                int wClip = pw;
                int hClip = ph;
                if (dstX + wClip > width)  wClip = width  - dstX;
                if (dstY + hClip > height) hClip = height - dstY;
                if (wClip <= 0 || hClip <= 0) continue;

                int srcRow;
                try { srcRow = checked(pw * bytesPerPixel); } catch { return false; }

                long need = (long)srcRow * ph;
                int pixelOffset;
                try { pixelOffset = checked(ofs + 16); } catch { return false; }
                if (pixelOffset < 0 || pixelOffset + need > s.Length) return false;

                s.Position = pixelOffset;

                if (bpp == 24)
                {
                    // 源为 24bpp BGR
                    var rowSrc = new byte[srcRow];
                    var rowOut = new byte[wClip * 4];

                    for (int y = 0; y < hClip; y++)
                    {
                        s.ReadExactly(rowSrc, 0, srcRow);

                        // 只转换左侧 wClip 像素，右边被裁剪掉
                        ImageUtils.ConvertRowBgr24ToBgra(rowSrc, rowOut, wClip);

                        IntPtr dest = IntPtr.Add(basePtr, (dstY + y) * stride + dstX * 4);
                        Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                    }
                }
                else // 16bpp 特殊打包，沿用原来的解码方式
                {
                    var rowSrc = new byte[srcRow];
                    var rowOut = new byte[wClip * 4];

                    for (int y = 0; y < hClip; y++)
                    {
                        s.ReadExactly(rowSrc, 0, srcRow);

                        int sx = 0;
                        int dx = 0;

                        for (int x = 0; x < wClip; x++)
                        {
                            ushort v = (ushort)(rowSrc[sx] | (rowSrc[sx + 1] << 8));
                            sx += 2;

                            int r5 = (v >> 11) & 0x1F;
                            int g5 = (v >> 6) & 0x1F;
                            int b5 = v & 0x1F;

                            byte r = (byte)((r5 << 3) | (r5 >> 2));
                            byte g = (byte)((g5 << 3) | (g5 >> 2));
                            byte b = (byte)((b5 << 3) | (b5 >> 2));

                            rowOut[dx + 0] = b;
                            rowOut[dx + 1] = g;
                            rowOut[dx + 2] = r;
                            rowOut[dx + 3] = 255;
                            dx += 4;
                        }

                        IntPtr dest = IntPtr.Add(basePtr, (dstY + y) * stride + dstX * 4);
                        Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                    }
                }
            }

            return true;
        }

        static bool ReadHeader(Stream s, out int width, out int height, out int parts, out int bpp, out int offsetBase)
        {
            width = height = parts = bpp = offsetBase = 0;

            int magic = s.ReadInt32LEAt(0);
            if (magic == MagicOld) offsetBase = 40;
            else if (magic == MagicNew) offsetBase = 44;
            else return false;

            width  = s.ReadInt32LEAt(20);
            height = s.ReadInt32LEAt(24);
            parts  = s.ReadInt32LEAt(28);
            bpp    = s.ReadInt32LEAt(32);

            return true;
        }
    }
}
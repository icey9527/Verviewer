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
        magics: new[] { "TBM ", "TBMB", "TBMC", "TBMD" }
    )]
    internal sealed class IkusabuneTbmImageHandler : IImageHandler
    {
        const int MagicNew = 1112359508;
        const int MagicOld = 541934164;

        // 大端 (主机版 Lib2D::tagTbmHeaderAusfD): 52B 头, 偏移表在 0x34, color_bits@0x20 只有 24/32
        const int MagicTBMD = unchecked((int)0x444D4254); // "TBMD"
        const int MagicTBMC = unchecked((int)0x434D4254); // "TBMC"

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 40) return null;

                if (!ReadHeader(s, out int width, out int height, out int parts, out int bytesPerPixel, out int offsetBase, out bool be))
                    return null;

                if (width <= 0 || height <= 0 || parts < 0) return null;
                if (bytesPerPixel < 2 || bytesPerPixel > 4) return null;

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
                    ok = FillImage(s, width, height, parts, bytesPerPixel, offsetBase, be, bd);
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

        static bool FillImage(Stream s, int width, int height, int parts, int bytesPerPixel, int offsetBase, bool be, BitmapData bd)
        {
            int stride = bd.Stride;
            IntPtr basePtr = bd.Scan0;

            int tableBytes;
            try { tableBytes = checked(parts * 4); } catch { return false; }
            if (offsetBase < 0 || offsetBase + tableBytes > s.Length) return false;

            for (int i = 0; i < parts; i++)
            {
                int ofs = be ? ReadInt32BEAt(s, offsetBase + i * 4) : s.ReadInt32LEAt(offsetBase + i * 4);
                if (ofs < 0 || ofs + 16 > s.Length) return false;

                int dstX, dstY, pw, ph;
                if (be)
                {
                    dstX = ReadInt32BEAt(s, ofs + 0);
                    dstY = ReadInt32BEAt(s, ofs + 4);
                    pw   = ReadInt32BEAt(s, ofs + 8);
                    ph   = ReadInt32BEAt(s, ofs + 12);
                }
                else
                {
                    dstX = s.ReadInt32LEAt(ofs + 0);
                    dstY = s.ReadInt32LEAt(ofs + 4);
                    pw   = s.ReadInt32LEAt(ofs + 8);
                    ph   = s.ReadInt32LEAt(ofs + 12);
                }

                if (pw <= 0 || ph <= 0) continue;

                // 大端: 负坐标部件做源/目标裁剪 (与 IpfbTool TbmImageCodec 一致); 小端: 保持原跳过行为
                int sx = 0, sy = 0;
                int wClip = pw, hClip = ph;
                int dx = dstX, dy = dstY;
                if (be)
                {
                    if (dx < 0) { sx = -dx; wClip += dx; dx = 0; }
                    if (dy < 0) { sy = -dy; hClip += dy; dy = 0; }
                }
                else if (dx < 0 || dy < 0) continue;

                if (dx >= width || dy >= height) continue;
                if (dx + wClip > width)  wClip = width  - dx;
                if (dy + hClip > height) hClip = height - dy;
                if (wClip <= 0 || hClip <= 0) continue;

                // 游戏按 4 字节对齐的行距读源数据 (sub_60821EF0: 24bpp→Align4(pw*3), 16bpp→Align4(pw*2))
                int srcRow;
                try { srcRow = Align4(checked(pw * bytesPerPixel)); } catch { return false; }

                long start = (long)ofs + 16;
                long need = (long)srcRow * ph;
                if (start < 0 || start + need > s.Length) return false;

                // 顶部被裁的部件从源第 sy 行开始读 (sy 只在大端裁剪时非 0)
                s.Position = start + (long)sy * srcRow;

                var rowSrc = new byte[srcRow];
                var rowOut = new byte[wClip * 4];

                for (int y = 0; y < hClip; y++)
                {
                    s.ReadExactly(rowSrc, 0, srcRow);

                    if (bytesPerPixel == 4)
                    {
                        // 32bpp: 字节序 B,G,R,A (与端序无关), 直接拷贝
                        Buffer.BlockCopy(rowSrc, sx * 4, rowOut, 0, wClip * 4);
                    }
                    else if (bytesPerPixel == 3)
                    {
                        // 24bpp BGR
                        int p = sx * 3;
                        int d = 0;
                        for (int x = 0; x < wClip; x++, p += 3, d += 4)
                        {
                            rowOut[d + 0] = rowSrc[p + 0]; // B
                            rowOut[d + 1] = rowSrc[p + 1]; // G
                            rowOut[d + 2] = rowSrc[p + 2]; // R
                            rowOut[d + 3] = 255;
                        }
                    }
                    else // 16bpp: X RRRRR GGGGG BBBBB (bit15 空置, 无 alpha), 与游戏 sub_60821BC0 一致
                    {
                        int p = sx * 2;
                        int d = 0;
                        for (int x = 0; x < wClip; x++, p += 2, d += 4)
                        {
                            ushort v = (ushort)(rowSrc[p] | (rowSrc[p + 1] << 8));

                            int r5 = (v >> 11) & 0x1F;
                            int g5 = (v >> 6) & 0x1F;
                            int b5 = v & 0x1F;

                            rowOut[d + 0] = (byte)((b5 << 3) | (b5 >> 2));
                            rowOut[d + 1] = (byte)((g5 << 3) | (g5 >> 2));
                            rowOut[d + 2] = (byte)((r5 << 3) | (r5 >> 2));
                            rowOut[d + 3] = 255;
                        }
                    }

                    IntPtr dest = IntPtr.Add(basePtr, (dy + y) * stride + dx * 4);
                    Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                }
            }

            return true;
        }

        static int Align4(int x) => (x + 3) & ~3;

        static bool ReadHeader(Stream s, out int width, out int height, out int parts, out int bytesPerPixel, out int offsetBase, out bool be)
        {
            width = height = parts = bytesPerPixel = offsetBase = 0;
            be = false;

            int magic = s.ReadInt32LEAt(0);
            if (magic == MagicOld) offsetBase = 40;
            else if (magic == MagicNew) offsetBase = 44;
            else if (magic == MagicTBMD || magic == MagicTBMC)
            {
                be = true;
                offsetBase = 52;
            }
            else return false;

            if (be)
            {
                int colorBits = ReadInt32BEAt(s, 32);
                if (colorBits != 24 && colorBits != 32) return false;
                bytesPerPixel = colorBits / 8;

                width  = ReadInt32BEAt(s, 20);
                height = ReadInt32BEAt(s, 24);
                parts  = ReadInt32BEAt(s, 28);
            }
            else
            {
                int bpp = s.ReadInt32LEAt(32);
                if (bpp != 16 && bpp != 24) return false;
                bytesPerPixel = bpp / 8;

                width  = s.ReadInt32LEAt(20);
                height = s.ReadInt32LEAt(24);
                parts  = s.ReadInt32LEAt(28);
            }

            return true;
        }

        static int ReadInt32BEAt(Stream s, long offset)
        {
            byte[] b = s.ReadBytesAt(offset, 4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }
    }
}
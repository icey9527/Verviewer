using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

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
            Stream s = EnsureSeekable(stream);
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
                if (!ReferenceEquals(s, stream)) s.Dispose();
            }
        }

        static bool FillImage(Stream s, int width, int height, int parts, int bpp, int offsetBase, BitmapData bd)
        {
            int stride = bd.Stride;
            IntPtr basePtr = bd.Scan0;
            int fullStride;
            try { fullStride = checked(width * 4); } catch { return false; }
            int tableBytes;
            try { tableBytes = checked(parts * 4); } catch { return false; }
            if (offsetBase < 0 || offsetBase + tableBytes > s.Length) return false;
            int bytesPerPixel = bpp / 8;
            for (int i = 0; i < parts; i++)
            {
                int ofs = ReadInt32At(s, offsetBase + i * 4);
                if (ofs < 0 || ofs + 16 > s.Length) return false;
                int dstX = ReadInt32At(s, ofs + 0);
                int dstY = ReadInt32At(s, ofs + 4);
                int pw = ReadInt32At(s, ofs + 8);
                int ph = ReadInt32At(s, ofs + 12);
                if (pw <= 0 || ph <= 0) continue;
                if (dstX < 0 || dstY < 0 || dstX >= width || dstY >= height) continue;
                int wClip = pw;
                int hClip = ph;
                if (dstX + wClip > width) wClip = width - dstX;
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
                    var rowSrc = new byte[srcRow];
                    var rowOut = new byte[wClip * 4];
                    for (int y = 0; y < hClip; y++)
                    {
                        ReadExactlyInto(s, rowSrc, 0, srcRow);
                        int sx = 0;
                        int dx = 0;
                        for (int x = 0; x < wClip; x++)
                        {
                            byte b = rowSrc[sx + 0];
                            byte g = rowSrc[sx + 1];
                            byte r = rowSrc[sx + 2];
                            rowOut[dx + 0] = b;
                            rowOut[dx + 1] = g;
                            rowOut[dx + 2] = r;
                            rowOut[dx + 3] = 255;
                            sx += 3;
                            dx += 4;
                        }
                        IntPtr dest = IntPtr.Add(basePtr, (dstY + y) * stride + dstX * 4);
                        Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                    }
                }
                else
                {
                    var rowSrc = new byte[srcRow];
                    var rowOut = new byte[wClip * 4];
                    for (int y = 0; y < hClip; y++)
                    {
                        ReadExactlyInto(s, rowSrc, 0, srcRow);
                        int sx = 0;
                        int dx = 0;
                        for (int x = 0; x < wClip; x++)
                        {
                            ushort v = (ushort)(rowSrc[sx] | (rowSrc[sx + 1] << 8));
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
                            sx += 2;
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
            int magic = ReadInt32At(s, 0);
            if (magic == MagicOld) offsetBase = 40;
            else if (magic == MagicNew) offsetBase = 44;
            else return false;
            width = ReadInt32At(s, 20);
            height = ReadInt32At(s, 24);
            parts = ReadInt32At(s, 28);
            bpp = ReadInt32At(s, 32);
            return true;
        }

        static int ReadInt32At(Stream s, long off)
        {
            long save = s.Position;
            s.Position = off;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            s.Position = save;
            if ((b0 | b1 | b2 | b3) < 0) return 0;
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        static void ReadExactlyInto(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0) throw new EndOfStreamException();
                total += r;
            }
        }

        static Stream EnsureSeekable(Stream s)
        {
            if (s.CanSeek) return s;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
    }
}
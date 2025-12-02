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
        const int MagicNew = 1112359508; // "TBMB"
        const int MagicOld = 541934164;  // "TBM "

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

                int fullStride = checked(width * 4);
                var full = new byte[checked(fullStride * height)];

                int tableBytes = checked(parts * 4);
                if (offsetBase < 0 || offsetBase + tableBytes > s.Length) return null;

                int bytesPerPixel = bpp / 8;

                for (int i = 0; i < parts; i++)
                {
                    int ofs = ReadInt32At(s, offsetBase + i * 4);
                    if (ofs < 0 || ofs + 16 > s.Length) return null;

                    int dstX = ReadInt32At(s, ofs + 0);
                    int dstY = ReadInt32At(s, ofs + 4);
                    int pw = ReadInt32At(s, ofs + 8);
                    int ph = ReadInt32At(s, ofs + 12);

                    if (pw <= 0 || ph <= 0) continue;
                    if (dstX < 0 || dstY < 0 || dstX >= width || dstY >= height) continue;

                    int wClip = pw, hClip = ph;
                    if (dstX + wClip > width) wClip = width - dstX;
                    if (dstY + hClip > height) hClip = height - dstY;
                    if (wClip <= 0 || hClip <= 0) continue;

                    int srcRow = checked(pw * bytesPerPixel);
                    long need = (long)srcRow * ph;
                    int pixelOffset = checked(ofs + 16);
                    if (pixelOffset < 0 || pixelOffset + need > s.Length) return null;

                    s.Position = pixelOffset;

                    if (bpp == 24)
                    {
                        var row = new byte[srcRow];
                        for (int y = 0; y < hClip; y++)
                        {
                            ReadExactlyInto(s, row, 0, srcRow);
                            int sx = 0;
                            int dx = (dstY + y) * fullStride + dstX * 4;
                            for (int x = 0; x < wClip; x++)
                            {
                                byte b = row[sx + 0];
                                byte g = row[sx + 1];
                                byte r = row[sx + 2];
                                full[dx + 0] = b;
                                full[dx + 1] = g;
                                full[dx + 2] = r;
                                full[dx + 3] = 255;
                                sx += 3;
                                dx += 4;
                            }
                        }
                    }
                    else
                    {
                        var row = new byte[srcRow];
                        for (int y = 0; y < hClip; y++)
                        {
                            ReadExactlyInto(s, row, 0, srcRow);
                            int sx = 0;
                            int dx = (dstY + y) * fullStride + dstX * 4;
                            for (int x = 0; x < wClip; x++)
                            {
                                ushort v = (ushort)(row[sx] | (row[sx + 1] << 8));
                                int r5 = (v >> 11) & 0x1F;
                                int g5 = (v >> 6) & 0x1F;   // 按你原逻辑保留 5 位
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
            finally
            {
                if (!ReferenceEquals(s, stream)) s.Dispose();
            }
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
            int b0 = s.ReadByte(), b1 = s.ReadByte(), b2 = s.ReadByte(), b3 = s.ReadByte();
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
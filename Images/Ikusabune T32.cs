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
        id: "Ikusabune T32",
        extensions: new[] { "t32" },
        magics: new[] { "T32 ", "T8aB", "T4aB", "T1aB", "4444", "1555" }
    )]
    internal sealed class IkusabuneT32ImageHandler : IImageHandler
    {
        const int MagicT1 = 1113665876;
        const int MagicT4 = 1113666644;
        const int MagicT8 = 1113667668;

        const int OldT8 = 540160852;
        const int OldT4 = 875836468;
        const int OldT1 = 892679473;

        struct T32Header
        {
            public int Magic;
            public int W;
            public int H;
            public int Parts;
            public int OffsetBase;
        }

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 32) return null;

                if (!ReadHeader(s, out var h)) return null;
                if (h.W <= 0 || h.H <= 0 || h.Parts < 0) return null;

                long pixels = (long)h.W * h.H;
                if (pixels <= 0 || pixels > 10000L * 10000L) return null;

                var bmp = new Bitmap(h.W, h.H, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, h.W, h.H);

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
                    ok = FillImage(s, h, bd);
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

        static bool ReadHeader(Stream s, out T32Header h)
        {
            h = default;

            int raw = s.ReadInt32LEAt(0);
            int magic;
            bool old;

            switch (raw)
            {
                case MagicT1:
                case MagicT4:
                case MagicT8:
                    magic = raw;
                    old = false;
                    break;
                case OldT8:
                    magic = MagicT8;
                    old = true;
                    break;
                case OldT4:
                    magic = MagicT4;
                    old = true;
                    break;
                case OldT1:
                    magic = MagicT1;
                    old = true;
                    break;
                default:
                    return false;
            }

            int baseOffset = old ? 32 : 36;
            int w = s.ReadInt32LEAt(20);
            int hh = s.ReadInt32LEAt(24);
            int parts = s.ReadInt32LEAt(28);

            h = new T32Header
            {
                Magic = magic,
                W = w,
                H = hh,
                Parts = parts,
                OffsetBase = baseOffset
            };
            return true;
        }

        static bool FillImage(Stream s, T32Header h, BitmapData bd)
        {
            int width = h.W;
            int height = h.H;
            int stride = bd.Stride;
            IntPtr basePtr = bd.Scan0;

            int tableBytes;
            try { tableBytes = checked(h.Parts * 4); }
            catch { return false; }

            if (h.OffsetBase < 0 || h.OffsetBase + tableBytes > s.Length)
                return false;

            for (int i = 0; i < h.Parts; i++)
            {
                int ofsPos = h.OffsetBase + i * 4;
                int ofs = s.ReadInt32LEAt(ofsPos);
                if (ofs < 0 || ofs + 16 > s.Length)
                    return false;

                int px = s.ReadInt32LEAt(ofs + 0);
                int py = s.ReadInt32LEAt(ofs + 4);
                int pw = s.ReadInt32LEAt(ofs + 8);
                int ph = s.ReadInt32LEAt(ofs + 12);

                if (pw <= 0 || ph <= 0) continue;
                if (px < 0 || py < 0) return false;
                if (px >= width || py >= height) continue;

                int cw = pw;
                int ch = ph;
                if (px + cw > width) cw = width - px;
                if (py + ch > height) ch = height - py;
                if (cw <= 0 || ch <= 0) continue;

                int bpp = h.Magic == MagicT8 ? 4 : 2;
                int pitch = Align4(checked(pw * bpp));
                long start = (long)ofs + 16;
                long total = (long)pitch * ph;
                if (start < 0 || start + total > s.Length)
                    return false;

                s.Position = start;

                if (h.Magic == MagicT8)
                {
                    // 32bpp 直接拷贝 (已是 BGRA)
                    var rowBuf = new byte[pitch];
                    for (int row = 0; row < ch; row++)
                    {
                        s.ReadExactly(rowBuf, 0, pitch);
                        IntPtr dest = IntPtr.Add(basePtr, (py + row) * stride + px * 4);
                        Marshal.Copy(rowBuf, 0, dest, cw * 4);
                    }
                }
                else
                {
                    bool is1555 = h.Magic == MagicT1; // 否则 4444

                    var rowBuf = new byte[pitch];
                    var rowOut = new byte[cw * 4];

                    for (int row = 0; row < ch; row++)
                    {
                        s.ReadExactly(rowBuf, 0, pitch);

                        if (is1555)
                            ImageUtils.ConvertRowArgb1555ToBgra(rowBuf, rowOut, cw);
                        else
                            ImageUtils.ConvertRowArgb4444ToBgra(rowBuf, rowOut, cw);

                        IntPtr dest = IntPtr.Add(basePtr, (py + row) * stride + px * 4);
                        Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                    }
                }
            }

            return true;
        }

        static int Align4(int x) => (x + 3) & ~3;
    }
}
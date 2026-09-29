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
        magics: new[] { "T32 ", "T8aB", "T4aB", "T1aB", "4444", "1555",
                        "T8aD", "T8aC", "T4aD", "T4aC", "T1aD", "T1aC" }
    )]
    internal sealed class IkusabuneT32ImageHandler : IImageHandler
    {
        const int MagicT1 = 1113665876;
        const int MagicT4 = 1113666644;
        const int MagicT8 = 1113667668;

        const int OldT8 = 540160852;
        const int OldT4 = 875836468;
        const int OldT1 = 892679473;

        // 大端 (主机版 Lib2D::tagT32HeaderAusfD): 44B 头, 偏移表在 0x2C
        const int MagicT8D = unchecked((int)0x44613854); // "T8aD"
        const int MagicT8C = unchecked((int)0x43613854); // "T8aC"
        const int MagicT4D = unchecked((int)0x44613454); // "T4aD"
        const int MagicT4C = unchecked((int)0x43613454); // "T4aC"
        const int MagicT1D = unchecked((int)0x44613154); // "T1aD"
        const int MagicT1C = unchecked((int)0x43613154); // "T1aC"

        struct T32Header
        {
            public int Magic;
            public int W;
            public int H;
            public int Parts;
            public int OffsetBase;
            public bool BE;
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
            bool be;

            switch (raw)
            {
                case MagicT1:
                case MagicT4:
                case MagicT8:
                    magic = raw;
                    old = false;
                    be = false;
                    break;
                case OldT8:
                    magic = MagicT8;
                    old = true;
                    be = false;
                    break;
                case OldT4:
                    magic = MagicT4;
                    old = true;
                    be = false;
                    break;
                case OldT1:
                    magic = MagicT1;
                    old = true;
                    be = false;
                    break;
                case MagicT8D:
                case MagicT8C:
                case MagicT4D:
                case MagicT4C:
                case MagicT1D:
                case MagicT1C:
                    magic = raw;
                    old = false;
                    be = true;
                    break;
                default:
                    return false;
            }

            int baseOffset = be ? 44 : (old ? 32 : 36);
            int w, hh, parts;
            if (be)
            {
                w = ReadInt32BEAt(s, 20);
                hh = ReadInt32BEAt(s, 24);
                parts = ReadInt32BEAt(s, 28);
            }
            else
            {
                w = s.ReadInt32LEAt(20);
                hh = s.ReadInt32LEAt(24);
                parts = s.ReadInt32LEAt(28);
            }

            h = new T32Header
            {
                Magic = magic,
                W = w,
                H = hh,
                Parts = parts,
                OffsetBase = baseOffset,
                BE = be
            };
            return true;
        }

        static bool FillImage(Stream s, T32Header h, BitmapData bd)
        {
            int width = h.W;
            int height = h.H;
            int stride = bd.Stride;
            IntPtr basePtr = bd.Scan0;

            bool isT8 = h.Magic == MagicT8 || h.Magic == MagicT8D || h.Magic == MagicT8C;
            bool is1555 = h.Magic == MagicT1 || h.Magic == MagicT1D || h.Magic == MagicT1C;
            int bpp = isT8 ? 4 : 2;

            int tableBytes;
            try { tableBytes = checked(h.Parts * 4); }
            catch { return false; }

            if (h.OffsetBase < 0 || h.OffsetBase + tableBytes > s.Length)
                return false;

            // 主机版 parts==0: 0x2C 起整幅原始像素 (与 IpfbTool DecodeCurrentBe 一致)
            if (h.BE && h.Parts == 0)
            {
                int pitch = Align4(checked(width * bpp));
                long start = h.OffsetBase;
                long total = (long)pitch * height;
                if (start + total > s.Length)
                    return false;

                var rowBuf = new byte[pitch];
                var rowOut = new byte[width * 4];
                s.Position = start;
                for (int y = 0; y < height; y++)
                {
                    s.ReadExactly(rowBuf, 0, pitch);
                    DecodeBeRow(rowBuf, rowOut, width, bpp, is1555);
                    IntPtr dest = IntPtr.Add(basePtr, y * stride);
                    Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                }
                return true;
            }

            for (int i = 0; i < h.Parts; i++)
            {
                int ofsPos = h.OffsetBase + i * 4;
                int ofs = h.BE ? ReadInt32BEAt(s, ofsPos) : s.ReadInt32LEAt(ofsPos);
                if (ofs < 0 || ofs + 16 > s.Length)
                    return false;

                int px, py, pw, ph;
                if (h.BE)
                {
                    px = ReadInt32BEAt(s, ofs + 0);
                    py = ReadInt32BEAt(s, ofs + 4);
                    pw = ReadInt32BEAt(s, ofs + 8);
                    ph = ReadInt32BEAt(s, ofs + 12);
                }
                else
                {
                    px = s.ReadInt32LEAt(ofs + 0);
                    py = s.ReadInt32LEAt(ofs + 4);
                    pw = s.ReadInt32LEAt(ofs + 8);
                    ph = s.ReadInt32LEAt(ofs + 12);
                }

                if (pw <= 0 || ph <= 0) continue;
                if (px < 0 || py < 0)
                {
                    if (h.BE) continue; // 大端: 跳过越界部件 (与 IpfbTool 一致)
                    return false;       // 小端: 原行为
                }
                if (px >= width || py >= height) continue;

                int cw = pw;
                int ch = ph;
                if (px + cw > width) cw = width - px;
                if (py + ch > height) ch = height - py;
                if (cw <= 0 || ch <= 0) continue;

                int pitch = Align4(checked(pw * bpp));
                long start = (long)ofs + 16;
                long total = (long)pitch * ph;
                if (start < 0 || start + total > s.Length)
                    return false;

                s.Position = start;

                if (h.BE)
                {
                    var rowBuf = new byte[pitch];
                    var rowOut = new byte[cw * 4];
                    for (int row = 0; row < ch; row++)
                    {
                        s.ReadExactly(rowBuf, 0, pitch);
                        DecodeBeRow(rowBuf, rowOut, cw, bpp, is1555);
                        IntPtr dest = IntPtr.Add(basePtr, (py + row) * stride + px * 4);
                        Marshal.Copy(rowOut, 0, dest, rowOut.Length);
                    }
                }
                else if (h.Magic == MagicT8)
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

        // 大端像素行 → BGRA: 32bpp 字节序 A,R,G,B; 16bpp 大端字, 位域与小端相同
        static void DecodeBeRow(byte[] src, byte[] dest, int width, int bpp, bool is1555)
        {
            if (bpp == 4)
            {
                int p = 0;
                int d = 0;
                for (int x = 0; x < width; x++, p += 4, d += 4)
                {
                    dest[d + 0] = src[p + 3]; // B
                    dest[d + 1] = src[p + 2]; // G
                    dest[d + 2] = src[p + 1]; // R
                    dest[d + 3] = src[p + 0]; // A
                }
            }
            else
            {
                int p = 0;
                int d = 0;
                for (int x = 0; x < width; x++, p += 2, d += 4)
                {
                    ushort v = (ushort)((src[p] << 8) | src[p + 1]);
                    byte a, r, g, b;
                    if (is1555)
                        ImageUtils.DecodeArgb1555(v, out a, out r, out g, out b);
                    else
                        ImageUtils.DecodeArgb4444(v, out a, out r, out g, out b);
                    dest[d + 0] = b;
                    dest[d + 1] = g;
                    dest[d + 2] = r;
                    dest[d + 3] = a;
                }
            }
        }

        static int ReadInt32BEAt(Stream s, long offset)
        {
            byte[] b = s.ReadBytesAt(offset, 4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        static int Align4(int x) => (x + 3) & ~3;
    }
}
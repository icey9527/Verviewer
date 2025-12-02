using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;

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
            Stream s = EnsureSeekable(stream);
            try
            {
                if (!s.CanSeek || s.Length < 32) return null;

                if (!ReadHeader(s, out var h)) return null;
                if (h.W <= 0 || h.H <= 0 || h.Parts < 0) return null;
                long pixels = (long)h.W * h.H;
                if (pixels <= 0 || pixels > 10000L * 10000L) return null;

                int stride = checked(h.W * 4);
                var full = new byte[checked(stride * h.H)];

                if (!FillImage(s, h, full, stride)) return null;

                var bmp = new Bitmap(h.W, h.H, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, h.W, h.H);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (bd.Stride == stride)
                    {
                        Marshal.Copy(full, 0, bd.Scan0, full.Length);
                    }
                    else
                    {
                        for (int y = 0; y < h.H; y++)
                        {
                            IntPtr dst = bd.Scan0 + y * bd.Stride;
                            Marshal.Copy(full, y * stride, dst, stride);
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

        static bool ReadHeader(Stream s, out T32Header h)
        {
            h = default;
            int raw = ReadInt32At(s, 0);
            int magic;
            bool old;

            switch (raw)
            {
                case MagicT1:
                case MagicT4:
                case MagicT8:
                    magic = raw; old = false; break;
                case OldT8:
                    magic = MagicT8; old = true; break;
                case OldT4:
                    magic = MagicT4; old = true; break;
                case OldT1:
                    magic = MagicT1; old = true; break;
                default:
                    return false;
            }

            int baseOffset = old ? 32 : 36;
            int w = ReadInt32At(s, 20);
            int hh = ReadInt32At(s, 24);
            int parts = ReadInt32At(s, 28);

            h = new T32Header { Magic = magic, W = w, H = hh, Parts = parts, OffsetBase = baseOffset };
            return true;
        }

        static bool FillImage(Stream s, T32Header h, byte[] full, int fullStride)
        {
            int width = h.W, height = h.H;

            int tableBytes;
            try { tableBytes = checked(h.Parts * 4); } catch { return false; }
            if (h.OffsetBase < 0 || h.OffsetBase + tableBytes > s.Length) return false;

            for (int i = 0; i < h.Parts; i++)
            {
                int ofsPos = h.OffsetBase + i * 4;
                int ofs = ReadInt32At(s, ofsPos);
                if (ofs < 0 || ofs + 16 > s.Length) return false;

                int px = ReadInt32At(s, ofs + 0);
                int py = ReadInt32At(s, ofs + 4);
                int pw = ReadInt32At(s, ofs + 8);
                int ph = ReadInt32At(s, ofs + 12);

                if (pw <= 0 || ph <= 0) continue;
                if (px < 0 || py < 0) return false;
                if (px >= width || py >= height) continue;

                int cw = pw, ch = ph;
                if (px + cw > width) cw = width - px;
                if (py + ch > height) ch = height - py;
                if (cw <= 0 || ch <= 0) continue;

                int bpp = (h.Magic == MagicT8) ? 4 : 2;
                int pitch = Align4(checked(pw * bpp));

                long start = (long)ofs + 16;
                long total = (long)pitch * ph;
                if (start < 0 || start + total > s.Length) return false;

                s.Position = start;

                if (h.Magic == MagicT8)
                {
                    var rowBuf = new byte[pitch];
                    for (int row = 0; row < ch; row++)
                    {
                        ReadExactlyInto(s, rowBuf, 0, pitch);
                        int dst = (py + row) * fullStride + px * 4;
                        Buffer.BlockCopy(rowBuf, 0, full, dst, cw * 4);
                    }
                }
                else
                {
                    bool is1555 = h.Magic == MagicT1;
                    var rowBuf = new byte[pitch];
                    for (int row = 0; row < ch; row++)
                    {
                        ReadExactlyInto(s, rowBuf, 0, pitch);
                        for (int col = 0; col < cw; col++)
                        {
                            int si = col * 2;
                            ushort v = (ushort)(rowBuf[si] | (rowBuf[si + 1] << 8));
                            byte a, r, g, b;
                            if (is1555) From1555(v, out a, out r, out g, out b);
                            else From4444(v, out a, out r, out g, out b);

                            int di = (py + row) * fullStride + (px + col) * 4;
                            full[di + 0] = b;
                            full[di + 1] = g;
                            full[di + 2] = r;
                            full[di + 3] = a;
                        }
                    }
                }
            }

            return true;
        }

        static int Align4(int x) => (x + 3) & ~3;

        static void From1555(ushort v, out byte a, out byte r, out byte g, out byte b)
        {
            a = (byte)((v & 0x8000) != 0 ? 255 : 0);
            int r5 = (v >> 10) & 0x1F;
            int g5 = (v >> 5) & 0x1F;
            int b5 = v & 0x1F;
            r = (byte)((r5 << 3) | (r5 >> 2));
            g = (byte)((g5 << 3) | (g5 >> 2));
            b = (byte)((b5 << 3) | (b5 >> 2));
        }

        static void From4444(ushort v, out byte a, out byte r, out byte g, out byte b)
        {
            int a4 = (v >> 12) & 0xF;
            int r4 = (v >> 8) & 0xF;
            int g4 = (v >> 4) & 0xF;
            int b4 = v & 0xF;
            a = (byte)((a4 << 4) | a4);
            r = (byte)((r4 << 4) | r4);
            g = (byte)((g4 << 4) | g4);
            b = (byte)((b4 << 4) | b4);
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
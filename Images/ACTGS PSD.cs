using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    /// <summary>
    /// ACTGS（PC 版）归档里的 .psd 条目。
    ///
    /// 这些 PSD 结构完全合法（8BPS / depth=8 / mode=RGB / PackBits RLE），
    /// 但实测 GDI+ 的 <c>Image.FromStream</c> 对它们抛“参数无效”，
    /// 因此这里在 GDI+ 失败后自行解析平面数据。
    ///
    /// 顺序刻意做成“先 GDI+，失败再自解”：对任何原本就能被内置解码处理的
    /// PSD，本插件行为与 builtin 完全一致，只是额外兜住了 GDI+ 解不开的那一类，
    /// 因此不会让其他游戏的 PSD 预览变差。
    /// </summary>
    [ImagePlugin(
        id: "ACTGS PSD",
        extensions: new[] { "psd" },
        magics: new[] { "hex:38 42 50 53" }
    )]
    internal sealed class ActgsPsdImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (stream == null || !stream.CanRead) return null;

            byte[] all;
            try { all = ReadAll(stream); }
            catch { return null; }
            if (all.Length < 26) return null;

            // 1) 先交给 GDI+，保持与内置行为一致
            try
            {
                using var ms = new MemoryStream(all, writable: false);
                using var img = Image.FromStream(ms, true, true);
                return new Bitmap(img);
            }
            catch { /* GDI+ 解不开，走自解 */ }

            // 2) 自行解析 PSD 平面数据
            try { return DecodePsd(all); }
            catch { return null; }
        }

        // ---------------- PSD 解析 ----------------

        static Image? DecodePsd(byte[] d)
        {
            if (!(d[0] == 0x38 && d[1] == 0x42 && d[2] == 0x50 && d[3] == 0x53)) return null;

            int ch = BE16(d, 12);
            int height = (int)BE32(d, 14);
            int width = (int)BE32(d, 18);
            int depth = BE16(d, 22);
            int mode = BE16(d, 24);

            if (ch < 1 || ch > 4) return null;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;
            if (depth != 8) return null;          // 仅支持 8bit/通道
            if (mode != 3 && mode != 1) return null;   // 3=RGB, 1=灰度

            int o = 26;
            o += 4 + (int)BE32(d, o);             // Color Mode Data
            if (o + 4 > d.Length) return null;
            o += 4 + (int)BE32(d, o);             // Image Resources
            if (o + 4 > d.Length) return null;
            o += 4 + (int)BE32(d, o);             // Layer and Mask Information
            if (o + 2 > d.Length) return null;
            int compression = BE16(d, o);
            o += 2;

            int plane = width * height;
            int nc = Math.Min(ch, 3);             // 只取 R,G,B（第 4 通道忽略）
            byte[][] planes = new byte[nc][];

            if (compression == 0)
            {
                for (int c = 0; c < nc; c++)
                {
                    int from = o + c * plane;
                    if (from + plane > d.Length) return null;
                    planes[c] = new byte[plane];
                    Buffer.BlockCopy(d, from, planes[c], 0, plane);
                }
            }
            else if (compression == 1)
            {
                int tableLen = 2 * ch * height;
                if (o + tableLen > d.Length) return null;
                var counts = new int[ch * height];
                for (int i = 0; i < counts.Length; i++) counts[i] = BE16(d, o + 2 * i);
                int p = o + tableLen;

                for (int c = 0; c < nc; c++)
                {
                    var buf = new byte[plane];
                    int filled = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int n = counts[c * height + y];
                        if (n < 0 || p + n > d.Length) return null;
                        int wrote = PackBits(d, p, n, buf, filled);
                        if (wrote != width) return null;
                        filled += wrote;
                        p += n;
                    }
                    planes[c] = buf;
                }
            }
            else return null;

            return BuildBitmap(width, height, mode, planes);
        }

        static Bitmap BuildBitmap(int w, int h, int mode, byte[][] planes)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, w, h);
            BitmapData data;
            try { data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb); }
            catch { bmp.Dispose(); return null!; }

            try
            {
                int stride = data.Stride;
                var row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    int line = y * w;
                    if (planes.Length >= 3 && mode == 3)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int i = line + x;
                            row[x * 3 + 0] = planes[2][i];   // B
                            row[x * 3 + 1] = planes[1][i];   // G
                            row[x * 3 + 2] = planes[0][i];   // R
                        }
                    }
                    else
                    {
                        var g = planes[0];
                        for (int x = 0; x < w; x++)
                        {
                            byte v = g[line + x];
                            row[x * 3 + 0] = v;
                            row[x * 3 + 1] = v;
                            row[x * 3 + 2] = v;
                        }
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * stride), stride);
                }
            }
            finally { bmp.UnlockBits(data); }

            return bmp;
        }

        /// <summary>Photoshop PackBits：返回写入 dst 的字节数。</summary>
        static int PackBits(byte[] src, int off, int len, byte[] dst, int dstOff)
        {
            int i = off, end = off + len, n = 0;
            while (i < end)
            {
                int hdr = (sbyte)src[i++];
                if (hdr >= 0)
                {
                    int cnt = hdr + 1;
                    if (i + cnt > end) cnt = end - i;
                    Buffer.BlockCopy(src, i, dst, dstOff + n, cnt);
                    i += cnt;
                    n += cnt;
                }
                else if (hdr > -127)
                {
                    int cnt = 1 - hdr;
                    byte v = src[i++];
                    for (int k = 0; k < cnt; k++) dst[dstOff + n + k] = v;
                    n += cnt;
                }
                // hdr == -128 : nop
            }
            return n;
        }

        static int BE16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
        static uint BE32(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

        static byte[] ReadAll(Stream s)
        {
            s = s.EnsureSeekable();
            if (s.CanSeek) s.Position = 0;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}

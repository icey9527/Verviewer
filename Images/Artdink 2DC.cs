using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "Artdink 2DC",
        extensions: new[] { "2dc" },
        magics: new[] { "ENDILTLE", "ENDIBIGE" }
    )]
    internal sealed class Artdink2DCHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            var s = stream.EnsureSeekable();
            try
            {
                if (s.Length < 0x40 || s.Length > int.MaxValue) return null;
                var data = new byte[(int)s.Length];
                s.Position = 0;
                s.ReadExactly(data, 0, data.Length);
                return Decode(data);
            }
            catch { return null; }
            finally { if (!ReferenceEquals(s, stream)) s.Dispose(); }
        }

        static Image? Decode(byte[] data)
        {
            int planX = 0, planY = 0, planW = 960, planH = 544;
            var textures = new List<(int x, int y, byte[] gim)>();
            bool foundPlan = false;
            int pos = 0;

            while (pos + 8 <= data.Length)
            {
                if (foundPlan && textures.Count >= 2) break;

                var sig = System.Text.Encoding.ASCII.GetString(data, pos, 8);

                if (sig == "CH2DPLAN")
                {
                    if (foundPlan) break;
                    foundPlan = true;

                    if (pos + 0x40 <= data.Length)
                    {
                        planX = BE32S(data, pos + 0x30);
                        planY = BE32S(data, pos + 0x34);
                        planW = BE32(data, pos + 0x38);
                        planH = BE32(data, pos + 0x3C);

                        if (Math.Abs(planX) > 4000) planX = 0;
                        if (Math.Abs(planY) > 4000) planY = 0;
                        if (planW <= 0 || planH <= 0 || planW > 4000 || planH > 4000)
                        {
                            planW = 960;
                            planH = 544;
                        }
                    }
                    int headerSize = BE32(data, pos + 0x0C);
                    pos += Math.Max(headerSize, 16);
                }
                else if (sig == "CH2DTEXI")
                {
                    if (foundPlan && pos + 0x30 <= data.Length)
                    {
                        int chunk = BE32(data, pos + 0x08);
                        int gimOff = BE32(data, pos + 0x0C);
                        int gimSz = BE32(data, pos + 0x10);
                        int tx = BE32S(data, pos + 0x20);
                        int ty = BE32S(data, pos + 0x24);
                        int start = pos + gimOff;

                        if (start + gimSz <= data.Length && gimSz > 0)
                        {
                            var gim = new byte[gimSz];
                            Buffer.BlockCopy(data, start, gim, 0, gimSz);
                            textures.Add((tx, ty, gim));
                        }
                        pos += Math.Max((chunk + 15) & ~15, 16);
                    }
                    else
                    {
                        int headerSize = BE32(data, pos + 0x0C);
                        pos += Math.Max(headerSize, 16);
                    }
                }
                else if (sig == "CH2DEXPR")
                {
                    break;
                }
                else if (sig.StartsWith("CH2D"))
                {
                    int headerSize = BE32(data, pos + 0x0C);
                    pos += Math.Max(headerSize, 16);
                }
                else
                {
                    pos += 4;
                }
            }

            if (textures.Count == 0) return null;

            var bmp = new Bitmap(planW, planH, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Transparent);

                foreach (var (tx, ty, gim) in textures)
                {
                    using var ms = new MemoryStream(gim);
                    var img = new GimImageHandler().TryDecode(ms, null);
                    if (img != null)
                    {
                        g.DrawImage(img, tx, ty);
                        img.Dispose();
                    }
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        static int BE32(byte[] d, int o)
        {
            if (o + 4 > d.Length) return 0;
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }

        static int BE32S(byte[] d, int o)
        {
            uint v = (uint)BE32(d, o);
            return v > 0x7FFFFFFF ? (int)(v - 0x100000000) : (int)v;
        }
    }
}
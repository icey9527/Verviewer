using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(id: "Artdink TEX", extensions: new[] { "tex" }, magics: new[] {"TEX "})]
    internal sealed class TexImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;

                int wReal = (int)s.ReadUInt32LEAt(0x14);
                int hReal = (int)s.ReadUInt32LEAt(0x18);
                long baseOff = s.ReadUInt32LEAt(0x20);
                int numSprites = s.ReadUInt16LEAt(0x24);
                int hasClut = s.ReadUInt16LEAt(0x26);

                if (wReal <= 0 || hReal <= 0 || wReal > 16384 || hReal > 16384 || numSprites == 0)
                    return null;

                byte[]? pal8 = null;
                byte[]? pal4 = null;

                if (hasClut != 0)
                {
                    long cAddr = 0x28 + (numSprites * 20);
                    long cRelOff = s.ReadUInt32LEAt((int)cAddr);
                    s.Position = baseOff + cRelOff;
                    byte[] rawPal = new byte[1024];
                    if (s.Read(rawPal, 0, 1024) == 1024)
                    {
                        pal8 = ImageUtils.BuildPs2Palette256Bgra_Block32(rawPal);
                        pal4 = ImageUtils.BuildPaletteBgraFromRgba(rawPal, 16, true);
                    }
                }

                var bmp = ImageUtils.CreateArgbBitmap(wReal, hReal, out var bd, out int stride);
                int currentY = 0;

                try
                {
                    for (int i = 0; i < numSprites; i++)
                    {
                        int addr = 0x28 + i * 20;
                        long sRelOff = s.ReadUInt32LEAt(addr);
                        byte psm = s.ReadByteAt(addr + 6);
                        int sw = s.ReadUInt16LEAt(addr + 16);
                        int sh = s.ReadUInt16LEAt(addr + 18);

                        if (sw == 0 || sh == 0) continue;

                        int bpp = psm switch { 0x00 => 32, 0x01 => 24, 0x02 => 16, 0x13 => 8, 0x14 => 4, _ => 0 };
                        if (bpp == 0) continue;

                        s.Position = baseOff + sRelOff;
                        int rowSize = (sw * bpp + 7) / 8;
                        byte[] rowSrc = new byte[rowSize];
                        
                        int copyW = Math.Min(sw, wReal);
                        byte[] rowBgra = new byte[copyW * 4];

                        for (int y = 0; y < sh; y++)
                        {
                            int destY = currentY + y;
                            if (destY >= hReal) break;

                            s.ReadExactly(rowSrc, 0, rowSize);

                            if (psm == 0x00) ImageUtils.ConvertRowRgba32ToBgraWithPs2Alpha(rowSrc, rowBgra, copyW);
                            else if (psm == 0x01) ImageUtils.ConvertRowRgb24ToBgra(rowSrc, rowBgra, copyW);
                            else if (psm == 0x02) ImageUtils.ConvertRowRgb555ToBgra(rowSrc, rowBgra, copyW);
                            else if (psm == 0x13 && pal8 != null) ImageUtils.ConvertRowIndexed8ToBgra(rowSrc, rowBgra, copyW, pal8);
                            else if (psm == 0x14 && pal4 != null) ImageUtils.ConvertRowIndexed4ToBgra(rowSrc, rowBgra, copyW, pal4);

                            ImageUtils.CopyRowToBitmap(bd, destY, rowBgra, stride);
                        }
                        currentY += sh;
                    }
                }
                catch
                {
                    ImageUtils.UnlockBitmap(bd, bmp);
                    bmp.Dispose();
                    throw;
                }

                ImageUtils.UnlockBitmap(bd, bmp);
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
    }
}
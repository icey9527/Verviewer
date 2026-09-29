using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(id: "Artdink TAG", extensions: new[] { "tag" })]
    internal sealed class TagImageHandler : IImageHandler
    {
        struct TransferEvent
        {
            public int Offset, Size, Width, Height;
        }

        struct TagImage
        {
            public int PaletteOffset, ColorCount, Width, Height, PixelOffset, PixelSize;
        }

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();
            try
            {
                if (!s.CanSeek || s.Length < 0x40) return null;

                byte[] data = s.ReadBytesAt(0, checked((int)s.Length));
                List<TagImage> images = ParseImages(data);
                if (images.Count == 0) return null;

                int width = 0, height = 0;
                foreach (var img in images)
                {
                    if (img.Width <= 0 || img.Height <= 0 || img.Width > 16384 || img.Height > 16384) return null;
                    width = Math.Max(width, img.Width);
                    height = checked(height + img.Height);
                }
                if (width == 0 || height > 65535) return null;

                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
                try
                {
                    int currentY = 0;
                    foreach (var img in images)
                    {
                        DrawImage(data, img, bd, stride, currentY);
                        currentY += img.Height;
                    }
                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    return null;
                }
                finally
                {
                    ImageUtils.UnlockBitmap(bd, bmp);
                }
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

        static List<TagImage> ParseImages(byte[] d)
        {
            var events = new List<TransferEvent>();
            var seen = new HashSet<int>();
            int pw = 0, ph = 0;

            void Walk(int cursor)
            {
                while (cursor >= 0 && cursor + 16 <= d.Length && seen.Add(cursor))
                {
                    uint w0 = BitConverter.ToUInt32(d, cursor);
                    uint addr = BitConverter.ToUInt32(d, cursor + 4);
                    switch (w0 & 0x70000000)
                    {
                        case 0x10000000:
                            if ((w0 & 0xFFFF) > 0) ReadRecords(d, cursor + 16, ref pw, ref ph);
                            cursor += 16 + (int)(w0 & 0xFFFF) * 16;
                            break;
                        case 0x20000000:
                            cursor = (int)addr;
                            break;
                        case 0x30000000:
                        case 0x40000000:
                            events.Add(new TransferEvent
                            {
                                Offset = (int)addr,
                                Size = (int)(w0 & 0xFFFF) * 16,
                                Width = pw,
                                Height = ph
                            });
                            pw = ph = 0;
                            cursor += 16;
                            break;
                        case 0x50000000:
                            Walk((int)addr);
                            cursor += 16 + (int)(w0 & 0xFFFF) * 16;
                            break;
                        default:
                            return;
                    }
                }
            }

            Walk((int)BitConverter.ToUInt32(d, 4));

            var images = new List<TagImage>();
            for (int i = 0; i + 1 < events.Count; i += 2)
            {
                var pal = events[i];
                var pix = events[i + 1];
                int colors = pal.Width * pal.Height;
                int bpp = colors == 16 ? 4 : colors == 256 ? 8 : 0;
                int needed = pix.Width * pix.Height * bpp / 8;
                if (bpp == 0 || pal.Size != colors * 4 || Math.Abs(pix.Size - needed) > 16) return new List<TagImage>();

                images.Add(new TagImage
                {
                    PaletteOffset = pal.Offset,
                    ColorCount = colors,
                    Width = pix.Width,
                    Height = pix.Height,
                    PixelOffset = pix.Offset,
                    PixelSize = pix.Size
                });
            }
            return images;
        }

        static void ReadRecords(byte[] d, int packet, ref int width, ref int height)
        {
            int count = (int)(BitConverter.ToUInt32(d, packet) & 0xFFFF);
            for (int i = 0; i < count; i++)
            {
                int rec = packet + 16 + i * 16;
                if (rec + 16 > d.Length) return;
                if (BitConverter.ToUInt32(d, rec + 12) == 0x52)
                {
                    width = BitConverter.ToInt32(d, rec + 4);
                    height = BitConverter.ToInt32(d, rec + 8);
                }
            }
        }

        static void DrawImage(byte[] d, in TagImage img, BitmapData bd, int stride, int destY)
        {
            var palRaw = new byte[img.ColorCount * 4];
            Buffer.BlockCopy(d, img.PaletteOffset, palRaw, 0, palRaw.Length);

            byte[] pal = img.ColorCount == 256
                ? ImageUtils.BuildPs2Palette256Bgra_Block32(palRaw)
                : ImageUtils.BuildPaletteBgraFromRgba(palRaw, img.ColorCount, true);

            int bpp = img.ColorCount == 16 ? 4 : 8;
            int rowBytes = (img.Width * bpp + 7) / 8;
            int valid = Math.Min(img.PixelSize, img.Width * img.Height * bpp / 8);
            var rowSrc = new byte[rowBytes];
            var row = new byte[img.Width * 4];

            for (int y = 0; y < img.Height; y++)
            {
                int rowStart = y * rowBytes;
                if (rowStart >= valid) break;

                int copy = Math.Min(rowBytes, valid - rowStart);
                Buffer.BlockCopy(d, img.PixelOffset + rowStart, rowSrc, 0, copy);
                if (copy < rowBytes) Array.Clear(rowSrc, copy, rowBytes - copy);

                if (bpp == 8) ImageUtils.ConvertRowIndexed8ToBgra(rowSrc, row, img.Width, pal);
                else ImageUtils.ConvertRowIndexed4ToBgra(rowSrc, row, img.Width, pal);

                ImageUtils.CopyRowToBitmap(bd, destY + y, row, stride);
            }
        }
    }
}

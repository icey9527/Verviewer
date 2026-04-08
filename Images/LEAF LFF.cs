using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "LEAF LFF",
        extensions: new[] { "lff" },
        magics: new[] { "LEAFFUL" }
    )]
    internal sealed class LeafLffImageHandler : IImageHandler
    {
        const int HeaderSize = 20;

        public Image? TryDecode(Stream stream, string? ext)
        {
            Stream s = stream.EnsureSeekable();

            try
            {
                if (s.Length < HeaderSize)
                    return null;

                ushort x = s.ReadUInt16LEAt(8);
                ushort y = s.ReadUInt16LEAt(10);
                ushort widthU = s.ReadUInt16LEAt(12);
                ushort heightU = s.ReadUInt16LEAt(14);
                uint dataOffsetU = s.ReadUInt32LEAt(16);

                int width = widthU;
                int height = heightU;
                long dataOffset = dataOffsetU;

                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    return null;
                if (x != 0 || y != 0)
                    return null;
                if (dataOffset < HeaderSize || dataOffset > s.Length)
                    return null;

                int rowSize = checked(width * 3);
                int pixelSize = checked(rowSize * height);
                byte[] compressed = s.ReadBytesAt(dataOffset, checked((int)(s.Length - dataOffset)));
                if (!Leaf.Decompress(compressed, pixelSize, out byte[] pixels))
                    return null;

                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bmpData, out int stride);
                try
                {
                    var row = new byte[rowSize];
                    var rowOut = new byte[width * 4];

                    for (int srcY = 0; srcY < height; srcY++)
                    {
                        Buffer.BlockCopy(pixels, srcY * rowSize, row, 0, rowSize);
                        ImageUtils.ConvertRowBgr24ToBgra(row, rowOut, width);
                        int destY = height - 1 - srcY;
                        ImageUtils.CopyRowToBitmap(bmpData, destY, rowOut, stride);
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
                    ImageUtils.UnlockBitmap(bmpData, bmp);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }
    }
}

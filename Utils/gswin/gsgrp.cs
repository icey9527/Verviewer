// 放在 Verviewer.Archives 命名空间里，和 GSWIN.Decompress 同一个文件或同一个项目即可
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Utils
{
    /// <summary>
    /// GS 系列 GRP 图像的公共辅助函数。
    /// 目前只包含: 把整张 32bpp 位图的 Alpha 统一设为 255。
    /// </summary>
    internal static class GswinImageHelpers
    {
        public static void EnsureOpaqueAlpha(BitmapData data, int width, int height)
        {
            int stride = data.Stride;
            var row = new byte[width * 4];

            for (int y = 0; y < height; y++)
            {
                IntPtr src = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(src, row, 0, row.Length);

                for (int x = 0; x < width; x++)
                    row[x * 4 + 3] = 255;

                Marshal.Copy(row, 0, src, row.Length);
            }
        }
    }

    /// <summary>
    /// GSWIN 8bpp GRP 图像写入器:
    ///   - 前 256*4 字节为 BGRA 调色板;
    ///   - 后续为像素索引行, 每行 width 个字节。
    /// 用法: GSWIN.Decompress(..., new Grp8WriterStream(...))
    /// </summary>
    internal sealed class Grp8WriterStream : Stream
    {
        readonly BitmapData data;
        readonly int width;
        readonly int height;
        readonly int stride;

        readonly byte[] palette = new byte[256 * 4];
        readonly byte[] rowIdx;
        readonly byte[] rowBgra;

        int paletteIndex;
        int rowIdxPos;
        int y;

        public bool Completed => paletteIndex >= palette.Length && y >= height && rowIdxPos == 0;
        public bool HasAlpha { get; private set; }

        public Grp8WriterStream(BitmapData data, int width, int height)
        {
            this.data = data;
            this.width = width;
            this.height = height;
            stride = data.Stride;

            rowIdx = new byte[width];
            rowBgra = new byte[width * 4];
        }

        void Process(byte b)
        {
            // 先吃完 256*4 字节的 BGRA 调色板
            if (paletteIndex < palette.Length)
            {
                palette[paletteIndex++] = b;
                return;
            }

            if (y >= height) return;

            rowIdx[rowIdxPos++] = b;
            if (rowIdxPos == width)
            {
                for (int x = 0; x < width; x++)
                {
                    byte idx = rowIdx[x];
                    int p = idx * 4;
                    int d = x * 4;

                    byte a = palette[p + 3];
                    if (a != 0) HasAlpha = true;

                    rowBgra[d + 0] = palette[p + 0];
                    rowBgra[d + 1] = palette[p + 1];
                    rowBgra[d + 2] = palette[p + 2];
                    rowBgra[d + 3] = a;
                }

                IntPtr dest = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(rowBgra, 0, dest, rowBgra.Length);

                y++;
                rowIdxPos = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            while (offset < end) Process(buffer[offset++]);
        }

        public override void WriteByte(byte value) => Process(value);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// GSWIN 24bpp GRP 图像写入器:
    ///   - 数据为 BGR 行, 每行 width*3 字节, Decompress 直接写入。
    /// 用法: GSWIN.Decompress(..., new Grp24WriterStream(...))
    /// </summary>
    internal sealed class Grp24WriterStream : Stream
    {
        readonly BitmapData data;
        readonly int width;
        readonly int height;
        readonly int stride;

        readonly byte[] rowBgr;
        readonly byte[] rowBgra;

        int rowPos;
        int y;

        public bool Completed => y >= height && rowPos == 0;

        public Grp24WriterStream(BitmapData data, int width, int height)
        {
            this.data = data;
            this.width = width;
            this.height = height;
            stride = data.Stride;

            rowBgr = new byte[width * 3];
            rowBgra = new byte[width * 4];
        }

        void Process(byte b)
        {
            if (y >= height) return;

            rowBgr[rowPos++] = b;
            if (rowPos == rowBgr.Length)
            {
                int src = 0;
                for (int x = 0; x < width; x++)
                {
                    byte bb = rowBgr[src++];
                    byte gg = rowBgr[src++];
                    byte rr = rowBgr[src++];
                    int d = x * 4;

                    rowBgra[d + 0] = bb;
                    rowBgra[d + 1] = gg;
                    rowBgra[d + 2] = rr;
                    rowBgra[d + 3] = 255;
                }

                IntPtr dest = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(rowBgra, 0, dest, rowBgra.Length);

                y++;
                rowPos = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            while (offset < end) Process(buffer[offset++]);
        }

        public override void WriteByte(byte value) => Process(value);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// GSWIN 32bpp GRP 图像写入器:
    ///   - 数据为 BGRA 行, 每行 width*4 字节;
    ///   - 顺便统计是否存在非零 Alpha, 用于后续 Decide Opaque。
    /// 用法: GSWIN.Decompress(..., new Grp32WriterStream(...))
    /// </summary>
    internal sealed class Grp32WriterStream : Stream
    {
        readonly BitmapData data;
        readonly int width;
        readonly int height;
        readonly int stride;

        readonly byte[] row;
        int rowPos;
        int y;

        public bool Completed => y >= height && rowPos == 0;
        public bool HasAlpha { get; private set; }

        public Grp32WriterStream(BitmapData data, int width, int height)
        {
            this.data = data;
            this.width = width;
            this.height = height;
            stride = data.Stride;

            row = new byte[width * 4];
        }

        void Process(byte b)
        {
            if (y >= height) return;

            row[rowPos++] = b;
            if (rowPos == row.Length)
            {
                // 检查这一行是否有非零 Alpha
                for (int x = 0; x < width; x++)
                {
                    if (row[x * 4 + 3] != 0)
                    {
                        HasAlpha = true;
                        break;
                    }
                }

                IntPtr dest = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(row, 0, dest, row.Length);

                y++;
                rowPos = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            while (offset < end) Process(buffer[offset++]);
        }

        public override void WriteByte(byte value) => Process(value);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
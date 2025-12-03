using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Archives;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "GSWIN2 GRP"
    )]
    internal sealed class GameImgImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (!stream.CanRead) return null;
            if (stream.CanSeek) stream.Position = 0;
            var header = new byte[40];
            if (!ReadExact(stream, header, 0, header.Length)) return null;
            uint compSizeU = BitConverter.ToUInt32(header, 0);
            uint w = BitConverter.ToUInt32(header, 16);
            uint h = BitConverter.ToUInt32(header, 20);
            uint bpp = BitConverter.ToUInt32(header, 24);
            if (w == 0 || h == 0) return null;
            int width;
            int height;
            int bitsPerPixel;
            try
            {
                width = checked((int)w);
                height = checked((int)h);
                bitsPerPixel = checked((int)bpp);
            }
            catch
            {
                return null;
            }
            if (bitsPerPixel != 8 && bitsPerPixel != 24 && bitsPerPixel != 32) return null;
            if (compSizeU > int.MaxValue) return null;
            int compSize = (int)compSizeU;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData;
            try
            {
                bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
            bool ok = false;
            try
            {
                if (bitsPerPixel == 8)
                {
                    var writer = new Grp8WriterStream(bmpData, width, height);
                    long need = 256L * 4 + (long)width * height;
                    if (compSize > 0)
                        GSWINArchiveHandler.DecompressLzss(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);
                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha) EnsureOpaqueAlpha(bmpData, width, height);
                }
                else if (bitsPerPixel == 24)
                {
                    var writer = new Grp24WriterStream(bmpData, width, height);
                    long need = (long)width * height * 3;
                    if (compSize > 0)
                        GSWINArchiveHandler.DecompressLzss(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);
                    ok = writer.Completed;
                }
                else
                {
                    var writer = new Grp32WriterStream(bmpData, width, height);
                    long need = (long)width * height * 4;
                    if (compSize > 0)
                        GSWINArchiveHandler.DecompressLzss(stream, compSize, writer);
                    else
                        CopyRaw(stream, writer, need);
                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha) EnsureOpaqueAlpha(bmpData, width, height);
                }
            }
            catch
            {
                ok = false;
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
            if (!ok)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }

        static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) return false;
                offset += n;
                count -= n;
            }
            return true;
        }

        static void CopyRaw(Stream input, Stream output, long bytes)
        {
            var buf = new byte[4096];
            while (bytes > 0)
            {
                int n = input.Read(buf, 0, (int)Math.Min(buf.Length, bytes));
                if (n <= 0) break;
                output.Write(buf, 0, n);
                bytes -= n;
            }
        }

        static void EnsureOpaqueAlpha(BitmapData data, int width, int height)
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
                    rowBgra[d] = palette[p];
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
                    rowBgra[d] = bb;
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
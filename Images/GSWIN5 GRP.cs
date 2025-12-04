using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Verviewer.Archives;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "GSWIN5 GRP"
    )]
    internal sealed class Gswin5GrpImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (!stream.CanRead) return null;
            if (stream.CanSeek) stream.Position = 0;

            var header = new byte[0x74];
            try
            {
                stream.ReadExactly(header, 0, header.Length);
            }
            catch
            {
                return null;
            }

            int compSize   = BitConverter.ToInt32(header, 4);
            int uncompSize = BitConverter.ToInt32(header, 8);
            int dataOffset = BitConverter.ToInt32(header, 12);
            int width      = BitConverter.ToInt32(header, 20);
            int height     = BitConverter.ToInt32(header, 24);
            int bpp        = BitConverter.ToInt32(header, 28);

            if (width <= 0 || height <= 0 || compSize <= 0 || uncompSize <= 0 || dataOffset < 0)
                return null;

            if (bpp != 8 && bpp != 0x18 && bpp != 0x20)
                return null;

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
                Stream compStream;
                int headerLen = header.Length;

                if (dataOffset < headerLen)
                {
                    int prefixLen = headerLen - dataOffset;
                    if (prefixLen > compSize) prefixLen = compSize;

                    var prefix = new byte[prefixLen];
                    Buffer.BlockCopy(header, dataOffset, prefix, 0, prefixLen);

                    compStream = new PrefixConcatStream(prefix, stream);
                }
                else
                {
                    long skip = dataOffset - headerLen;
                    if (!SkipBytes(stream, skip))
                    {
                        bmp.UnlockBits(bmpData);
                        bmp.Dispose();
                        return null;
                    }
                    compStream = stream;
                }

                if (bpp == 8)
                {
                    var writer = new Grp8WriterStream(bmpData, width, height);
                    GSWIN.Decompress(compStream, compSize, writer);
                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha)
                        GswinImageHelpers.EnsureOpaqueAlpha(bmpData, width, height);
                }
                else if (bpp == 0x18)
                {
                    var writer = new Grp24WriterStream(bmpData, width, height);
                    GSWIN.Decompress(compStream, compSize, writer);
                    ok = writer.Completed;
                }
                else // 0x20 (32bpp)
                {
                    var writer = new Grp32WriterStream(bmpData, width, height);
                    GSWIN.Decompress(compStream, compSize, writer);
                    ok = writer.Completed;
                    if (ok && !writer.HasAlpha)
                        GswinImageHelpers.EnsureOpaqueAlpha(bmpData, width, height);
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

        static bool SkipBytes(Stream stream, long count)
        {
            var buf = new byte[4096];
            while (count > 0)
            {
                int n = stream.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) return false;
                count -= n;
            }
            return true;
        }
    }

    internal sealed class PrefixConcatStream : Stream
    {
        readonly byte[] prefix;
        readonly Stream inner;
        int index;

        public PrefixConcatStream(byte[] prefix, Stream inner)
        {
            this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override int ReadByte()
        {
            if (index < prefix.Length) return prefix[index++];
            return inner.ReadByte();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;

            while (count > 0 && index < prefix.Length)
            {
                buffer[offset++] = prefix[index++];
                count--;
                read++;
            }

            if (count > 0)
            {
                int n = inner.Read(buffer, offset, count);
                read += n;
            }

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
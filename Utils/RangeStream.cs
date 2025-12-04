// Utils/RangeStream.cs
// ---------------------------------------------------------------
// 公共子流类型
//
// 1) RangeStream
//    - 表示一个可 Seek 底层流 inner 的子区间 [start, start+length)
//    - 只读、不支持写入和修改长度
//    - 一般在 Archive 插件中用于按条目打开文件内容:
//
//        using Utils;
//
//        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
//        {
//            return new RangeStream(arc.Stream, entry.Offset, entry.Size, leaveOpen: true);
//        }
//
//    参数:
//      inner    : 底层流，必须 CanSeek 为 true（如 FileStream）。
//      start    : 子流起始偏移（相对 inner.Position = 0）。
//      length   : 子流长度。
//      leaveOpen: true 表示释放 RangeStream 时不关闭 inner，由外部统一管理；
//
// 2) SubReadStream  (兼容旧代码的包装类型)
//    - 旧代码里常用的名字，被一些插件引用。
//    - 现在实现为 RangeStream 的子类：
//          new SubReadStream(stream, start, length)
//        等价于：
//          new RangeStream(stream, start, length, leaveOpen: true);
//
//    - 建议新代码直接使用 RangeStream；SubReadStream 仅为过渡兼容。
// ---------------------------------------------------------------

using System;
using System.IO;

namespace Utils
{
    internal class RangeStream : Stream
    {
        private readonly Stream inner;
        private readonly long start;
        private readonly long length;
        private readonly bool leaveOpen;
        private long position;

        public RangeStream(Stream inner, long start, long length, bool leaveOpen)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (!inner.CanSeek) throw new NotSupportedException("Base stream must be seekable.");
            if (start < 0 || length < 0 || start + length > inner.Length)
                throw new ArgumentOutOfRangeException();

            this.inner = inner;
            this.start = start;
            this.length = length;
            this.leaveOpen = leaveOpen;

            inner.Position = start;
            position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
                inner.Position = start + position;
            }
        }

        public override void Flush()
        {
            // 只读视图，不做任何事
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            long remaining = length - position;
            if (remaining <= 0)
                return 0;

            if (count > remaining)
                count = (int)remaining;

            int read = inner.Read(buffer, offset, count);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End     => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0 || target > length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            Position = target;
            return position;
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();

            base.Dispose(disposing);
        }
    }

    // 兼容旧代码的别名：SubReadStream == RangeStream(leaveOpen: true)
    internal sealed class SubReadStream : RangeStream
    {
        public SubReadStream(Stream baseStream, long start, long length)
            : base(baseStream, start, length, leaveOpen: true)
        {
        }
    }
}
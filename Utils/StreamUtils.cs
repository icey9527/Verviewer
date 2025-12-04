// Utils/StreamUtils.cs
// ---------------------------------------------------------------
// 公共流工具 (扩展方法)
//
// 作用:
//   封装各种对 System.IO.Stream 的常用读操作，避免在每个插件里
//   重复写读满 / 保存还原 Position / 读字符串 等样板代码。
//
// 常用场景示例（写插件时可以这样用）：
//
//   using Utils;
//
//   // 1) 精确读取固定长度
//   byte[] header = stream.ReadExactly(12);
//   stream.ReadExactly(buffer, 0, buffer.Length);
//
//   // 2) 在指定偏移读取整数 / 字节
//   int   i32   = stream.ReadInt32LEAt(0x20);
//   uint  u16   = stream.ReadUInt16LEAt(0x40);
//   uint  u32   = stream.ReadUInt32LEAt(0x44);
//   byte  b     = stream.ReadByteAt(0x10);
//   byte[] buf  = stream.ReadBytesAt(0x100, 32);
//
//   // 3) 空终止字符串（需要提供最大字节数上限 maxBytes）
//   string name = stream.ReadNullTerminatedString(Encoding.UTF8, maxBytes: 0x40);
//   string name2 = stream.ReadNullTerminatedStringAt(0x200, Encoding.GetEncoding(932));
//
//   // 4) 固定长度字符串（读 N 字节，遇 0 截断，其余丢弃）
//   string fixedName  = stream.ReadFixedString(256, Encoding.UTF8);
//   string fixedName2 = stream.ReadFixedStringAt(0x300, 256, Encoding.GetEncoding(932));
//
//   // 5) 临时 Seek 到某处读取，再自动恢复原 Position
//   int val = stream.WithTemporarySeek(0x400, s =>
//   {
//       return s.ReadInt32LEAt(s.Position); // 或者在内部按自己的方式读
//   });
//
//   // 6) 对齐 / 跳过 / 读到结尾
//   stream.AlignPosition(0x10);
//   stream.Skip(4);
//   byte[] rest = stream.ReadToEnd();
//
//   // 7) 确保流可 Seek（如果是网络流 / 解压流等不可 Seek，就复制到内存）
//   Stream seekable = stream.EnsureSeekable();
//
// 注意:
//   - 所有 ReadXXXAt 方法都假设底层流可 Seek（FileStream 之类），
//     内部会保存并恢复 Position。
//   - ReadNullTerminatedString 的 maxBytes 不能为 0，调用者需要给出上限。
// ---------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Utils
{
    internal static class StreamUtils
    {
        // 精确读取 count 字节到 buffer[offset..offset+count)
        public static void ReadExactly(this Stream s, byte[] buffer, int offset, int count)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            int total = 0;
            while (total < count)
            {
                int r = s.Read(buffer, offset + total, count - total);
                if (r <= 0) throw new EndOfStreamException();
                total += r;
            }
        }

        // 精确读取 count 字节并返回新数组
        public static byte[] ReadExactly(this Stream s, int count)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            var buf = new byte[count];
            s.ReadExactly(buf, 0, count);
            return buf;
        }

        // 在指定偏移读取一个小端 int32，不改变最终 Position
        public static int ReadInt32LEAt(this Stream s, long offset)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream must be seekable for ReadInt32LEAt.");

            long save = s.Position;
            s.Position = offset;
            Span<byte> b = stackalloc byte[4];
            int read = s.Read(b);
            s.Position = save;
            if (read < 4) throw new EndOfStreamException();
            return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
        }

        // 在指定偏移读取一个小端 uint16
        public static ushort ReadUInt16LEAt(this Stream s, long offset)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream must be seekable for ReadUInt16LEAt.");

            long save = s.Position;
            s.Position = offset;
            int lo = s.ReadByte();
            int hi = s.ReadByte();
            s.Position = save;
            if (lo < 0 || hi < 0) throw new EndOfStreamException();
            return (ushort)(lo | (hi << 8));
        }

        // 在指定偏移读取一个小端 uint32
        public static uint ReadUInt32LEAt(this Stream s, long offset)
        {
            return unchecked((uint)ReadInt32LEAt(s, offset));
        }

        // 在指定偏移读取一个字节
        public static byte ReadByteAt(this Stream s, long offset)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream must be seekable for ReadByteAt.");

            long save = s.Position;
            s.Position = offset;
            int v = s.ReadByte();
            s.Position = save;
            if (v < 0) throw new EndOfStreamException();
            return (byte)v;
        }

        // 从当前位置读取空终止字符串（最多 maxBytes 字节）
        public static string ReadNullTerminatedString(this Stream s, Encoding enc, int maxBytes)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (enc == null) throw new ArgumentNullException(nameof(enc));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            var buffer = new byte[maxBytes];
            int count = 0;
            for (; count < maxBytes; count++)
            {
                int b = s.ReadByte();
                if (b <= 0) break;
                buffer[count] = (byte)b;
            }
            return count == 0 ? string.Empty : enc.GetString(buffer, 0, count);
        }

        // 在指定偏移读取空终止字符串，直到遇到 0 或 EOF
        public static string ReadNullTerminatedStringAt(this Stream s, long offset, Encoding enc)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (enc == null) throw new ArgumentNullException(nameof(enc));
            if (!s.CanSeek) throw new NotSupportedException("Stream must be seekable for ReadNullTerminatedStringAt.");

            long save = s.Position;
            s.Position = offset;

            var bytes = new List<byte>();
            while (true)
            {
                int b = s.ReadByte();
                if (b <= 0) break;
                bytes.Add((byte)b);
            }

            s.Position = save;
            return bytes.Count == 0 ? string.Empty : enc.GetString(bytes.ToArray());
        }

        // 若流不可 Seek，则复制到 MemoryStream 并返回；若可 Seek，则返回自身
        public static Stream EnsureSeekable(this Stream s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (s.CanSeek) return s;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        // ---------------- 下面是新增的一些通用小工具 ----------------

        // 在指定偏移精确读取 count 字节并返回新数组
        public static byte[] ReadBytesAt(this Stream s, long offset, int count)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream must be seekable for ReadBytesAt.");
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            long save = s.Position;
            s.Position = offset;
            try
            {
                return s.ReadExactly(count);
            }
            finally
            {
                s.Position = save;
            }
        }

        // 临时 Seek 到 offset，执行 action(s)，完毕后自动恢复 Position，返回结果
        public static T WithTemporarySeek<T>(this Stream s, long offset, Func<Stream, T> action)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (!s.CanSeek) throw new NotSupportedException("Stream is not seekable.");

            long save = s.Position;
            s.Position = offset;
            try
            {
                return action(s);
            }
            finally
            {
                s.Position = save;
            }
        }

        // 无返回值版本的 WithTemporarySeek
        public static void WithTemporarySeek(this Stream s, long offset, Action<Stream> action)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (!s.CanSeek) throw new NotSupportedException("Stream is not seekable.");

            long save = s.Position;
            s.Position = offset;
            try
            {
                action(s);
            }
            finally
            {
                s.Position = save;
            }
        }

        // 固定长度字符串：读取 byteCount 字节，遇到 0 截断，其余忽略
        public static string ReadFixedString(this Stream s, int byteCount, Encoding enc)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (enc == null) throw new ArgumentNullException(nameof(enc));
            if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));

            byte[] buf = s.ReadExactly(byteCount);
            int zeroIndex = Array.IndexOf(buf, (byte)0);
            if (zeroIndex < 0)
                zeroIndex = buf.Length;

            return enc.GetString(buf, 0, zeroIndex);
        }

        // 在指定偏移读取固定长度字符串
        public static string ReadFixedStringAt(this Stream s, long offset, int byteCount, Encoding enc)
        {
            return s.WithTemporarySeek(offset, st => st.ReadFixedString(byteCount, enc));
        }

        // 将 Position 对齐到 alignment 的倍数（例如 0x10）
        public static void AlignPosition(this Stream s, int alignment)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream is not seekable.");
            if (alignment <= 0) throw new ArgumentOutOfRangeException(nameof(alignment));

            long mod = s.Position % alignment;
            if (mod != 0)
                s.Position += (alignment - mod);
        }

        // 跳过指定字节数，相当于 Position += count
        public static void Skip(this Stream s, long count)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.CanSeek) throw new NotSupportedException("Stream is not seekable.");

            long newPos = s.Position + count;
            if (newPos < 0) throw new IOException("Skip leads to negative position.");
            s.Position = newPos;
        }

        // 从当前位置一直读到结尾，返回所有剩余数据
        public static byte[] ReadToEnd(this Stream s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    /// <summary>
    /// ACTGS ver 2.33 (ACTRESS) PC 版归档容器。
    ///
    /// 已实测确认三种布局，本插件支持前两种：
    ///
    /// A) 交错布局（arc.cg）—— 引擎 FUN_00429568 的 param_4 != 0 分支
    ///    记录与数据块交错存放，从文件偏移 0x1000 开始：
    ///        record_i @ pos_i            32 字节，逐条独立解密（相位从 0 起）
    ///            [0:4]   uint32 size     该条目原始长度
    ///            [4:32]  char[28] name   NUL 结尾，cp932
    ///        data_i   @ pos_i + 32       size 字节，整块连续解密（相位从 0 起）
    ///        pos_{i+1} = pos_i + align16(size_i) + 32
    ///    判据极强：末条数据块结束地址必须精确等于文件大小。
    ///
    /// B) 连续索引布局（arc.scr）—— param_4 == 0 分支
    ///        [0:16)   uint32 count + 12 字节保留（明文）
    ///        [16, 16+count*32)  索引区，整块连续解密（相位从 0 起）
    ///            record: [0:4] 绝对偏移  [4:8] 长度  [8:32] 名字(NUL, cp932)
    ///        数据块：首字节 0x58('X') 表示已混淆，明文首字节恒为 0x4E('N')，
    ///                其余按 key[(i-1) % 5] 解密。
    ///
    /// C) 明文布局（arc.wav）：24 字节记录 [off][size][name16]，数据不混淆。
    ///    其数据区起点与索引区之间存在未解析的间隔，故本版本暂不支持，
    ///    也未注册 wav 扩展名，以免误判普通 wav 音频。
    /// </summary>
    [ArchivePlugin(
        id: "ACTGS ARC",
        extensions: new[] { "scr", "cg" },
        magics: null
    )]
    internal sealed class ActgsArchiveHandler : IArchiveHandler
    {
        /// <summary>明文存于 exe .data（VA 0x4C5460 / 0x4BFEDF / 0x4C2A28 / 0x4C5857 四份副本，内容相同）。</summary>
        static readonly byte[] Key = { 0x11, 0x05, 0x22, 0x33, 0x41 };

        const int RecordSize = 32;
        const long InterleavedStart = 0x1000;
        const int Align = 16;

        enum BlockMode { Plain, CgStream, ScrMarked }

        /// <summary>ArchiveEntry 是 sealed，无法携带额外状态，故用引用侧表记录每条的解密模式。</summary>
        static readonly ConcurrentDictionary<ArchiveEntry, BlockMode> Modes = new();

        static readonly Encoding Cp932 = MakeCp932();

        static Encoding MakeCp932()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
            return Encoding.GetEncoding(932);
        }

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                options: FileOptions.RandomAccess);

            try
            {
                long fileLen = fs.Length;
                if (fileLen < RecordSize + 16)
                    throw new InvalidDataException("文件过小，不是 ACTGS 归档。");

                int count = fs.ReadInt32LEAt(0);
                if (count <= 0 || count > 200_000)
                    throw new InvalidDataException("条目数异常：" + count);

                var entries = new List<ArchiveEntry>(count);

                if (TryParseInterleaved(fs, count, fileLen, entries))
                    return new OpenedArchive(archivePath, fs, entries, this);

                entries.Clear();
                if (TryParseFlatIndex(fs, count, fileLen, entries))
                    return new OpenedArchive(archivePath, fs, entries, this);

                throw new InvalidDataException("无法按已知的任一 ACTGS 布局解析该文件。");
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("目录没有数据流。");

            var raw = new byte[entry.Size];
            arc.Stream.Position = entry.Offset;
            arc.Stream.ReadExactly(raw, 0, raw.Length);

            var mode = Modes.TryGetValue(entry, out var m) ? m : BlockMode.Plain;

            byte[] data = mode switch
            {
                BlockMode.CgStream => XorStream(raw, 0),
                BlockMode.ScrMarked => ScrDecode(raw),
                _ => raw
            };

            return new MemoryStream(data, writable: false);
        }

        // ---------------- 布局 A：交错（arc.cg） ----------------

        static bool TryParseInterleaved(FileStream fs, int count, long fileLen, List<ArchiveEntry> outEntries)
        {
            if (fileLen < InterleavedStart + RecordSize) return false;

            long pos = InterleavedStart;
            var list = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                if (pos + RecordSize > fileLen) return false;

                var rec = XorStream(fs.ReadBytesAt(pos, RecordSize), 0);

                long size = BitConverter.ToUInt32(rec, 0);
                if (size <= 0 || size > fileLen) return false;

                string name = ReadFixedName(rec, 4, 28);
                if (name.Length == 0) return false;

                long dataOff = pos + RecordSize;
                long next = dataOff + AlignUp(size);
                if (next > fileLen) return false;

                var entry = new ArchiveEntry
                {
                    Path = name.Replace('\\', '/'),
                    Offset = dataOff,
                    Size = (int)size,
                    UncompressedSize = (int)size,
                    IsDirectory = false
                };
                Modes.TryAdd(entry, BlockMode.CgStream);
                list.Add(entry);

                pos = next;   // 下一条记录紧跟本条数据（含对齐填充）之后
            }

            // 强判据：最后一条数据块结束必须正好等于文件大小
            long lastEnd = list[^1].Offset + AlignUp(list[^1].Size);
            if (lastEnd != fileLen) return false;

            outEntries.AddRange(list);
            return true;
        }

        // ---------------- 布局 B：连续索引（arc.scr） ----------------

        static bool TryParseFlatIndex(FileStream fs, int count, long fileLen, List<ArchiveEntry> outEntries)
        {
            long indexLen = (long)count * RecordSize;
            long dataStart = 16 + indexLen;
            if (dataStart > fileLen) return false;

            var idx = XorStream(fs.ReadBytesAt(16, (int)indexLen), 0);
            var list = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                var r = new byte[RecordSize];
                Buffer.BlockCopy(idx, i * RecordSize, r, 0, RecordSize);

                long off = BitConverter.ToUInt32(r, 0);
                long size = BitConverter.ToUInt32(r, 4);
                string name = ReadFixedName(r, 8, 24);

                if (name.Length == 0) return false;
                if (size <= 0 || size > fileLen) return false;
                if (off < dataStart || off + size > fileLen) return false;
                if (i == 0 && off != dataStart) return false;
                if (i > 0 && off < list[i - 1].Offset) return false;

                var entry = new ArchiveEntry
                {
                    Path = name.Replace('\\', '/'),
                    Offset = off,
                    Size = (int)size,
                    UncompressedSize = (int)size,
                    IsDirectory = false
                };
                Modes.TryAdd(entry, BlockMode.ScrMarked);
                list.Add(entry);
            }

            outEntries.AddRange(list);
            return true;
        }

        // ---------------- 变换 ----------------

        /// <summary>整段连续异或，相位以缓冲区首为 0。</summary>
        static byte[] XorStream(byte[] data, int startPhase)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                o[i] = (byte)(data[i] ^ Key[(i + startPhase) % Key.Length]);
            return o;
        }

        /// <summary>arc.scr 数据块：raw[0] 为 'X' 标记，明文首字节恒为 'N'，其后按 (i-1)%5。</summary>
        static byte[] ScrDecode(byte[] raw)
        {
            var o = new byte[raw.Length];
            if (raw.Length == 0) return o;
            o[0] = raw[0] == 0x58 ? (byte)0x4E : raw[0];
            for (int i = 1; i < raw.Length; i++)
                o[i] = (byte)(raw[i] ^ Key[(i - 1) % Key.Length]);
            return o;
        }

        static long AlignUp(long v) => (v + Align - 1) / Align * Align;

        static string ReadFixedName(byte[] buf, int offset, int len)
        {
            int end = 0;
            while (end < len && buf[offset + end] != 0) end++;
            if (end == 0) return string.Empty;
            try { return Cp932.GetString(buf, offset, end).Trim(); }
            catch { return string.Empty; }
        }
    }
}

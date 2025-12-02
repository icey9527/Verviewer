using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    internal static class ArtdinkCompression
    {
        public static bool TryDecompress(byte[] data, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (data == null || data.Length < 8)
                return false;

            uint expectedSizeRaw = BitConverter.ToUInt32(data, 4);
            if (expectedSizeRaw == 0 || expectedSizeRaw > int.MaxValue)
                return false;

            int expectedSize = (int)expectedSizeRaw;
            int dataSize = data.Length;

            int readIndex = 8;
            int outIndex = 0;
            uint dictPos = 0xFEE;
            uint control = 0;
            byte[] dict = new byte[0x1000];
            byte[] buffer = new byte[expectedSize];

            bool isMode1 = dataSize > 3 &&
                           data[1] == (byte)'3' &&
                           data[2] == (byte)';' &&
                           data[3] == (byte)'1';

            bool isMode0 = dataSize > 3 &&
                           data[1] == (byte)'3' &&
                           data[2] == (byte)';' &&
                           data[3] == (byte)'0';

            if (isMode1)
            {
                bool stop = false;
                while (!stop)
                {
                    while (true)
                    {
                        control >>= 1;
                        uint temp = control;

                        if ((control & 0x100) == 0)
                        {
                            if (dataSize <= readIndex)
                            {
                                stop = true;
                                break;
                            }

                            byte pb = data[readIndex++];
                            byte x = (byte)(pb ^ 0x72);
                            control = (uint)(x | 0xFF00);
                            temp = x;
                        }

                        if ((temp & 1) != 0)
                            break;

                        if (dataSize <= readIndex + 1)
                        {
                            stop = true;
                            break;
                        }

                        int nextIndex = readIndex + 1;
                        byte b1 = data[readIndex];
                        readIndex += 2;
                        byte b2 = data[nextIndex];

                        int cnt = 0;
                        int len = ((b2 ^ 0x72) & 0x0F) + 2;

                        while (cnt <= len)
                        {
                            uint offset = (uint)((b1 ^ 0x72) |
                                                 (((b2 ^ 0x72) & 0xF0) << 4));
                            offset += (uint)cnt;
                            cnt++;

                            byte value = dict[offset & 0x0FFF];

                            if (outIndex >= expectedSize)
                            {
                                stop = true;
                                break;
                            }

                            buffer[outIndex++] = value;
                            dict[dictPos] = value;
                            dictPos = (dictPos + 1) & 0x0FFF;
                        }

                        if (stop)
                            break;
                    }

                    if (stop)
                        break;

                    if (dataSize <= readIndex)
                        break;

                    byte b = data[readIndex++];
                    byte value2 = (byte)(b ^ 0x72);

                    if (outIndex >= expectedSize)
                        break;

                    buffer[outIndex++] = value2;
                    dict[dictPos] = value2;
                    dictPos = (dictPos + 1) & 0x0FFF;
                }
            }
            else if (isMode0)
            {
                if (dataSize > 8)
                {
                    for (int i = 8; i < dataSize; i++)
                    {
                        byte value = (byte)(data[i] ^ 0x72);
                        if (outIndex >= expectedSize)
                            break;

                        buffer[outIndex++] = value;
                    }
                }
            }
            else
            {
                return false;
            }

            if (outIndex < 0) outIndex = 0;
            if (outIndex > expectedSize) outIndex = expectedSize;

            output = new byte[outIndex];
            Array.Copy(buffer, output, outIndex);
            return true;
        }
    }

    [ArchivePlugin(
        id: "Artdink DAT",
        extensions: new[] { "dat" },
        magics: new[] { "PIDX0" }
        )]
    internal class Artdink_DAT : IArchiveHandler
{

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            // 2. 检查 0x8 == 1
            fs.Position = 0x8;
            uint flag = br.ReadUInt32();
            if (flag != 1)
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException("又调皮了哈？可不兴对idx用这个啊");
            }

            // 3. 公用头信息（不管是否有 FSTS 嵌套）
            fs.Position = 0xC;
            uint start = br.ReadUInt32();       // 对应 C 里的 start
            uint indexCount = br.ReadUInt32();  // 旧格式用

            fs.Position = 0x20;
            uint nameStart = br.ReadUInt32();

            // 尝试读取 FSTS 子索引个数（0x50 处），读不到就当没有
            uint subIndexCount = 0;
            if (fs.Length >= 0x54)
            {
                fs.Position = 0x50;
                subIndexCount = br.ReadUInt32();
            }

            List<ArchiveEntry> entries;

            if (subIndexCount > 0)
            {
                // 新格式：DAT 里嵌套若干 FSTS 子包
                entries = ParseNestedFsts(fs, br, start, nameStart, subIndexCount);

                // 如果解析结果是空表，可以当作失败，退回旧格式逻辑
                if (entries.Count == 0)
                {
                    entries = ParseFlatDatIndex(fs, br, start, indexCount, nameStart);
                }
            }
            else
            {
                // 旧格式：传统 DAT 索引（type/name_offset/...）
                entries = ParseFlatDatIndex(fs, br, start, indexCount, nameStart);
            }

            br.Dispose();

            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("目录没有数据流。");

            var fs = archive.Stream;

            fs.Position = entry.Offset;
            byte[] compData = new byte[entry.Size];
            int read = fs.Read(compData, 0, compData.Length);
            if (read < compData.Length)
                Array.Resize(ref compData, read);

            if (ArtdinkCompression.TryDecompress(compData, out var dec))
            {
                return new MemoryStream(dec, writable: false);
            }
            else
            {
                fs.Position = entry.Offset;
                byte[] raw = new byte[entry.UncompressedSize];
                read = fs.Read(raw, 0, raw.Length);
                if (read < raw.Length)
                    Array.Resize(ref raw, read);
                return new MemoryStream(raw, writable: false);
            }
        }

        #region 旧格式 DAT 索引解析（type/name_offset/sign/offset/...）

        private List<ArchiveEntry> ParseFlatDatIndex(
            FileStream fs,
            BinaryReader br,
            uint indexStart,
            uint indexCount,
            uint nameStart)
        {
            var datEntries = new List<DatEntry>();

            fs.Position = indexStart;
            for (int i = 0; i < indexCount; i++)
            {
                var e = new DatEntry
                {
                    Index = i,
                    Type = br.ReadUInt32(),
                    NameOffset = br.ReadUInt32(),
                    Sign = br.ReadUInt32(),
                    Offset = br.ReadUInt32(),
                    UncompressedSize = br.ReadUInt32(),
                    Size = br.ReadUInt32()
                };
                datEntries.Add(e);
            }

            // 文件名
            foreach (var e in datEntries)
            {
                fs.Position = nameStart + e.NameOffset;
                e.FileName = ReadNullTerminatedString(br);
            }

            var entries = new List<ArchiveEntry>(datEntries.Count);
            foreach (var e in datEntries)
            {
                bool isDir = e.Type == 1;
                string path = e.FileName.Replace('\\', '/');

                entries.Add(new ArchiveEntry
                {
                    Path = path,
                    IsDirectory = isDir,
                    Offset = e.Offset,
                    Size = (int)e.Size,
                    UncompressedSize = (int)e.UncompressedSize
                });
            }

            return entries;
        }

        #endregion

        #region 嵌套 FSTS 解析（process_pidx0 + process_fsts）

        private List<ArchiveEntry> ParseNestedFsts(
            FileStream fs,
            BinaryReader br,
            uint start,
            uint nameStart,
            uint subIndexCount)
        {
            var result = new List<ArchiveEntry>();

            // sub_index_start = start + 4;
            long subIndexStart = start + 4;

            // 读取所有 sub_index_pointers
            uint[] subPtrs = new uint[subIndexCount];
            for (uint i = 0; i < subIndexCount; i++)
            {
                fs.Position = subIndexStart + i * 4;
                uint pointer = br.ReadUInt32();
                subPtrs[i] = pointer + start;
            }

            // 对每个二级索引，读取名字和 FSTS 区块位置，然后解析 FSTS
            for (uint i = 0; i < subIndexCount; i++)
            {
                fs.Position = subPtrs[i];

                uint nameOffset = br.ReadUInt32();
                uint placeholder = br.ReadUInt32();      // 未用
                uint fstOffset = br.ReadUInt32();
                uint fstSize = br.ReadUInt32();
                uint num = br.ReadUInt32();              // 未用

                string name = ReadNullTerminatedStringAt(fs, nameStart + nameOffset);
                string prefix = name.Replace('\\', '/').Trim('/');

                // 解析嵌套 FSTS（prefix 作为子目录）
                ParseFstsAt(fs, fstOffset, fstSize, prefix, result);
            }

            return result;
        }

        private void ParseFstsAt(
            FileStream fs,
            uint fstOffset,
            uint fstSize,
            string prefix,
            List<ArchiveEntry> output)
        {
            long baseOffset = fstOffset;
            if (baseOffset + 4 > fs.Length)
                return;

            long saved = fs.Position;
            fs.Position = baseOffset;
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            // FSTS 头
            byte[] magicBytes = br.ReadBytes(4);
            string magic = Encoding.ASCII.GetString(magicBytes);
            if (magic != "FSTS")
            {
                fs.Position = saved;
                return;
            }

            uint idxq = br.ReadUInt32();
            uint indexStart = br.ReadUInt32();
            uint nameStart = br.ReadUInt32();

            // 读取 FSTS 内部索引表
            fs.Position = baseOffset + indexStart;
            for (uint i = 0; i < idxq; i++)
            {
                uint nameOffset = br.ReadUInt32();
                uint offset = br.ReadUInt32();
                uint size = br.ReadUInt32();
                uint uncompressSize = br.ReadUInt32();

                string name = ReadNullTerminatedStringAt(fs, baseOffset + nameStart + nameOffset);
                string innerPath = name.Replace('\\', '/').Trim('/');

                string fullPath = string.IsNullOrEmpty(prefix)
                    ? innerPath
                    : $"{prefix}/{innerPath}";

                // FSTS 内部 offset 是相对于 FSTS 开头的，这里转换成相对于整个 DAT 的全局偏移
                long globalOffset = baseOffset + offset;

                output.Add(new ArchiveEntry
                {
                    Path = fullPath,
                    IsDirectory = false,
                    Offset = globalOffset,
                    Size = (int)size,
                    UncompressedSize = (int)uncompressSize
                });
            }

            fs.Position = saved;
        }

        #endregion

        #region 辅助方法

        private static string ReadNullTerminatedString(BinaryReader br)
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte b = br.ReadByte();
                if (b == 0) break;
                bytes.Add(b);
            }
            return Encoding.GetEncoding(932).GetString(bytes.ToArray());
        }

        private static string ReadNullTerminatedStringAt(FileStream fs, long offset)
        {
            long current = fs.Position;
            fs.Position = offset;

            var bytes = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == 0)
                    break;
                bytes.Add((byte)b);
            }

            fs.Position = current;
            return Encoding.GetEncoding(932).GetString(bytes.ToArray());
        }

        private class DatEntry
        {
            public int Index { get; set; }
            public uint Type { get; set; }
            public uint NameOffset { get; set; }
            public uint Sign { get; set; }
            public uint Offset { get; set; }
            public uint UncompressedSize { get; set; }
            public uint Size { get; set; }
            public string FileName { get; set; } = string.Empty;
        }

        #endregion
    }
}
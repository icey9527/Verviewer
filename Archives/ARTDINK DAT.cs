using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    internal static class ArtdinkCompression
    {
        public static bool TryDecompress(byte[] data, out byte[] output)
        {
            if (data == null)
            {
                output = Array.Empty<byte>();
                return false;
            }
            using var ms = new MemoryStream(data, false);
            return TryDecompress(ms, data.Length, out output);
        }

        public static bool TryDecompress(Stream input, int compressedSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (input == null || !input.CanRead || compressedSize < 8) return false;
            var header = new byte[8];
            if (!ReadExact(input, header, 0, 8)) return false;
            uint expectedSizeRaw = BitConverter.ToUInt32(header, 4);
            if (expectedSizeRaw == 0 || expectedSizeRaw > int.MaxValue) return false;
            int expectedSize = (int)expectedSizeRaw;
            int remaining = compressedSize - 8;
            var dict = new byte[0x1000];
            uint dictPos = 0xFEE;
            uint control = 0;
            int outIndex = 0;
            var buffer = new byte[expectedSize];
            bool isMode1 = compressedSize > 3 &&
                           header[1] == (byte)'3' &&
                           header[2] == (byte)';' &&
                           header[3] == (byte)'1';
            bool isMode0 = compressedSize > 3 &&
                           header[1] == (byte)'3' &&
                           header[2] == (byte)';' &&
                           header[3] == (byte)'0';
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
                            if (remaining <= 0)
                            {
                                stop = true;
                                break;
                            }
                            int pb = input.ReadByte();
                            if (pb < 0)
                            {
                                stop = true;
                                break;
                            }
                            remaining--;
                            byte x = (byte)(pb ^ 0x72);
                            control = (uint)(x | 0xFF00);
                            temp = x;
                        }
                        if ((temp & 1) != 0) break;
                        if (remaining <= 1)
                        {
                            stop = true;
                            break;
                        }
                        int b1 = input.ReadByte();
                        int b2 = input.ReadByte();
                        if (b1 < 0 || b2 < 0)
                        {
                            stop = true;
                            break;
                        }
                        remaining -= 2;
                        int cnt = 0;
                        int len = ((b2 ^ 0x72) & 0x0F) + 2;
                        while (cnt <= len)
                        {
                            uint offset = (uint)((b1 ^ 0x72) | (((b2 ^ 0x72) & 0xF0) << 4));
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
                        if (stop) break;
                    }
                    if (stop) break;
                    if (remaining <= 0) break;
                    int b = input.ReadByte();
                    if (b < 0) break;
                    remaining--;
                    byte value2 = (byte)(b ^ 0x72);
                    if (outIndex >= expectedSize) break;
                    buffer[outIndex++] = value2;
                    dict[dictPos] = value2;
                    dictPos = (dictPos + 1) & 0x0FFF;
                }
            }
            else if (isMode0)
            {
                while (remaining > 0 && outIndex < expectedSize)
                {
                    int b = input.ReadByte();
                    if (b < 0) break;
                    remaining--;
                    buffer[outIndex++] = (byte)(b ^ 0x72);
                }
            }
            else
            {
                return false;
            }
            if (outIndex < 0) outIndex = 0;
            if (outIndex > expectedSize) outIndex = expectedSize;
            output = new byte[outIndex];
            Buffer.BlockCopy(buffer, 0, output, 0, outIndex);
            return true;
        }

        static bool ReadExact(Stream s, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buffer, offset, count);
                if (n <= 0) return false;
                offset += n;
                count -= n;
            }
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
            fs.Position = 0x8;
            uint flag = br.ReadUInt32();
            if (flag != 1)
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException();
            }
            fs.Position = 0xC;
            uint start = br.ReadUInt32();
            uint indexCount = br.ReadUInt32();
            fs.Position = 0x20;
            uint nameStart = br.ReadUInt32();
            uint subIndexCount = 0;
            if (fs.Length >= 0x54)
            {
                fs.Position = 0x50;
                subIndexCount = br.ReadUInt32();
            }
            List<ArchiveEntry> entries;
            if (subIndexCount > 0)
            {
                entries = ParseNestedFsts(fs, br, start, nameStart, subIndexCount);
                if (entries.Count == 0)
                    entries = ParseFlatDatIndex(fs, br, start, indexCount, nameStart);
            }
            else
            {
                entries = ParseFlatDatIndex(fs, br, start, indexCount, nameStart);
            }
            br.Dispose();
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory) throw new InvalidOperationException();
            var fs = archive.Stream;
            if (entry.Size > 0)
            {
                fs.Position = entry.Offset;
                if (ArtdinkCompression.TryDecompress(fs, entry.Size, out var dec))
                    return new MemoryStream(dec, false);
            }
            int rawSize = entry.UncompressedSize > 0 ? entry.UncompressedSize : entry.Size;
            if (rawSize <= 0) return Stream.Null;
            var raw = new byte[rawSize];
            fs.Position = entry.Offset;
            int read = fs.Read(raw, 0, raw.Length);
            if (read < raw.Length) Array.Resize(ref raw, read);
            return new MemoryStream(raw, false);
        }

        List<ArchiveEntry> ParseFlatDatIndex(FileStream fs, BinaryReader br, uint indexStart, uint indexCount, uint nameStart)
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

        List<ArchiveEntry> ParseNestedFsts(FileStream fs, BinaryReader br, uint start, uint nameStart, uint subIndexCount)
        {
            var result = new List<ArchiveEntry>();
            long subIndexStart = start + 4;
            uint[] subPtrs = new uint[subIndexCount];
            for (uint i = 0; i < subIndexCount; i++)
            {
                fs.Position = subIndexStart + i * 4;
                uint pointer = br.ReadUInt32();
                subPtrs[i] = pointer + start;
            }
            for (uint i = 0; i < subIndexCount; i++)
            {
                fs.Position = subPtrs[i];
                uint nameOffset = br.ReadUInt32();
                uint placeholder = br.ReadUInt32();
                uint fstOffset = br.ReadUInt32();
                uint fstSize = br.ReadUInt32();
                uint num = br.ReadUInt32();
                string name = ReadNullTerminatedStringAt(fs, nameStart + nameOffset);
                string prefix = name.Replace('\\', '/').Trim('/');
                ParseFstsAt(fs, fstOffset, fstSize, prefix, result);
            }
            return result;
        }

        void ParseFstsAt(FileStream fs, uint fstOffset, uint fstSize, string prefix, List<ArchiveEntry> output)
        {
            long baseOffset = fstOffset;
            if (baseOffset + 4 > fs.Length) return;
            long saved = fs.Position;
            fs.Position = baseOffset;
            using var br = new BinaryReader(fs, Encoding.ASCII, true);
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
            fs.Position = baseOffset + indexStart;
            for (uint i = 0; i < idxq; i++)
            {
                uint nameOffset = br.ReadUInt32();
                uint offset = br.ReadUInt32();
                uint size = br.ReadUInt32();
                uint uncompressSize = br.ReadUInt32();
                string name = ReadNullTerminatedStringAt(fs, baseOffset + nameStart + nameOffset);
                string innerPath = name.Replace('\\', '/').Trim('/');
                string fullPath = string.IsNullOrEmpty(prefix) ? innerPath : $"{prefix}/{innerPath}";
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

        static string ReadNullTerminatedString(BinaryReader br)
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

        static string ReadNullTerminatedStringAt(FileStream fs, long offset)
        {
            long current = fs.Position;
            fs.Position = offset;
            var bytes = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == 0) break;
                bytes.Add((byte)b);
            }
            fs.Position = current;
            return Encoding.GetEncoding(932).GetString(bytes.ToArray());
        }

        class DatEntry
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
    }
}
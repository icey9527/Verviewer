using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
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
                if (Artdink.Decompress(fs, entry.Size, out var dec))
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

            var encSjis = Encoding.GetEncoding(932);
            foreach (var e in datEntries)
            {
                long namePos = nameStart + e.NameOffset;
                e.FileName = fs.ReadNullTerminatedStringAt(namePos, encSjis);
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

            var encSjis = Encoding.GetEncoding(932);

            for (uint i = 0; i < subIndexCount; i++)
            {
                fs.Position = subPtrs[i];

                uint nameOffset = br.ReadUInt32();
                uint placeholder = br.ReadUInt32();
                uint fstOffset = br.ReadUInt32();
                uint fstSize = br.ReadUInt32();
                uint num = br.ReadUInt32();

                string name = fs.ReadNullTerminatedStringAt(nameStart + nameOffset, encSjis);
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
            var encSjis = Encoding.GetEncoding(932);

            for (uint i = 0; i < idxq; i++)
            {
                uint nameOffset = br.ReadUInt32();
                uint offset = br.ReadUInt32();
                uint size = br.ReadUInt32();
                uint uncompressSize = br.ReadUInt32();

                long namePos = baseOffset + nameStart + nameOffset;
                string name = fs.ReadNullTerminatedStringAt(namePos, encSjis);
                string innerPath = name.Replace('\\', '/').Trim('/');

                string fullPath = string.IsNullOrEmpty(prefix)
                    ? innerPath
                    : $"{prefix}/{innerPath}";

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